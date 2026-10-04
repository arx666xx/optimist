using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetOptimizer.Services;

public readonly record struct MemorySnapshot(int LoadPercent, ulong TotalBytes, ulong AvailableBytes)
{
    public ulong UsedBytes => TotalBytes > AvailableBytes ? TotalBytes - AvailableBytes : 0;

    /// <summary>"10,7 из 16,0 ГБ".</summary>
    public string UsedText => $"{UsedBytes / 1073741824.0:0.0} из {TotalBytes / 1073741824.0:0.0} ГБ";
}

/// <summary>
/// Frees RAM the way "memory cleaner" utilities do, with the same two levers:
///
///  1. Working-set trim (<c>EmptyWorkingSet</c>): every background program hands
///     the pages it is not actively touching back to Windows. They move to the
///     standby list and count as "available", so the "memory in use" figure in
///     Task Manager drops — this is where the big 67 % → 45 % jump comes from.
///     A program that needs a page again reads it back (mostly from RAM, cheaply).
///  2. Standby-list purge (<c>NtSetSystemInformation</c>): drops the file cache
///     Windows kept "just in case". Gives truly free memory to a game that is
///     about to load a lot at once.
///
/// Neither kills anything or loses data. The trade-off is honest: right after a
/// trim, switching back to a big background app can take a moment longer.
/// </summary>
public static class MemoryService
{
    public static MemorySnapshot GetStatus()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m)
            ? new MemorySnapshot((int)m.dwMemoryLoad, m.ullTotalPhys, m.ullAvailPhys)
            : default;
    }

    /// <summary>
    /// Trims every process it may touch, except <paramref name="skipPids"/>
    /// (the app being boosted) and the critical system processes.
    /// Returns the number of processes trimmed.
    /// </summary>
    public static int TrimWorkingSets(IReadOnlyCollection<int> skipPids)
    {
        int self = Environment.ProcessId;
        int trimmed = 0;

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                int pid = p.Id;
                if (pid <= 4 || pid == self || skipPids.Contains(pid)) continue;

                string name;
                try { name = p.ProcessName; } catch { continue; }
                // dwm/csrss being paged out shows up as a stutter on screen.
                if (ProcessGuard.IsCritical(name)) continue;

                IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_SET_QUOTA, false, pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    if (K32EmptyWorkingSet(h)) trimmed++;
                }
                finally { CloseHandle(h); }
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Flushes modified pages to disk and empties the standby list.
    /// Needs administrator rights; returns a user-facing error or null on success.
    /// </summary>
    public static string? PurgeStandbyList()
    {
        if (!EnablePrivilege("SeProfileSingleProcessPrivilege"))
            return "Нет права очищать системный кэш (запустите программу от администратора).";

        int cmd = MemoryFlushModifiedList;
        NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int));

        cmd = MemoryPurgeStandbyList;
        int status = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int));
        return status == 0 ? null : $"Windows отказала в очистке кэша (NTSTATUS 0x{status:X8}).";
    }

    private static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token))
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out long luid)) return false;

            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;

            // AdjustTokenPrivileges "succeeds" even when the privilege is not held.
            return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
        }
        finally { CloseHandle(token); }
    }

    // ---------------- Native ----------------

    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_SET_QUOTA = 0x0100;

    private const int SystemMemoryListInformation = 80;
    private const int MemoryFlushModifiedList = 3;
    private const int MemoryPurgeStandbyList = 4;

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;
    private const int ERROR_NOT_ALL_ASSIGNED = 1300;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
}
