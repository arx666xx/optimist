using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace NetOptimizer.Services;

internal readonly record struct ProcMeta(string Name, string? Path, string Description);

/// <summary>
/// Resolves PID -> executable name / full path / file description, with a cache
/// that survives across refreshes.
///
/// The previous implementation called <c>Process.GetProcesses()</c> and touched
/// <c>MainModule</c> (plus <c>FileVersionInfo</c>) for every process on every
/// 2-second tick — hundreds of handle opens and file reads per second.
///
/// Here each refresh only opens a lightweight
/// PROCESS_QUERY_LIMITED_INFORMATION handle and reads the process creation time
/// (a few microseconds). The expensive part — image path plus version info — runs
/// once per process and is reused until that PID is recycled, which the creation
/// time reliably detects.
/// </summary>
internal static class ProcessCache
{
    private sealed class Entry
    {
        public long CreationTime;
        public ProcMeta Meta;
    }

    private static readonly Dictionary<int, Entry> Cache = new();

    /// <summary>Returns metadata for the requested PIDs and drops entries for dead ones.</summary>
    public static Dictionary<int, ProcMeta> Resolve(IEnumerable<int> pids)
    {
        var result = new Dictionary<int, ProcMeta>();
        var alive = new HashSet<int>();

        foreach (int pid in pids)
        {
            if (!alive.Add(pid)) continue;      // already handled in this pass
            result[pid] = Lookup(pid);
        }

        // Forget processes that no longer hold a connection, so the cache
        // cannot grow without bound over a long session.
        if (Cache.Count > alive.Count * 2 + 64)
        {
            var dead = Cache.Keys.Where(k => !alive.Contains(k)).ToList();
            foreach (int k in dead) Cache.Remove(k);
        }

        return result;
    }

    private static ProcMeta Lookup(int pid)
    {
        if (pid == 0) return new ProcMeta("System Idle", null, "");
        if (pid == 4) return new ProcMeta("System", null, "");

        long creation = GetCreationTime(pid);

        if (Cache.TryGetValue(pid, out var cached))
        {
            // Same PID and same creation time -> definitely the same process.
            if (creation != 0 && cached.CreationTime == creation)
                return cached.Meta;
            if (creation == 0 && cached.CreationTime == 0)
                return cached.Meta;
        }

        var meta = Build(pid);
        Cache[pid] = new Entry { CreationTime = creation, Meta = meta };
        return meta;
    }

    private static ProcMeta Build(int pid)
    {
        string? path = GetImagePath(pid);
        string name;
        string desc = "";

        if (!string.IsNullOrEmpty(path))
        {
            name = System.IO.Path.GetFileNameWithoutExtension(path);
            try { desc = FileVersionInfo.GetVersionInfo(path).FileDescription ?? ""; }
            catch { /* file locked or gone */ }
        }
        else
        {
            // Protected process: the name is still readable through the toolhelp snapshot.
            name = GetNameFallback(pid) ?? $"PID {pid}";
        }

        return new ProcMeta(name, path, desc);
    }

    private static string? GetNameFallback(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return null; }
    }

    // ---------------- Native ----------------

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags,
        StringBuilder exeName, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr hProcess,
        out long creation, out long exit, out long kernel, out long user);

    private static long GetCreationTime(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return 0;
        try
        {
            return GetProcessTimes(h, out long creation, out _, out _, out _) ? creation : 0;
        }
        finally { CloseHandle(h); }
    }

    private static string? GetImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int size = 1024;
            var sb = new StringBuilder(size);
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        catch { return null; }
        finally { CloseHandle(h); }
    }
}
