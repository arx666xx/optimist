using System.Runtime.InteropServices;

namespace NetOptimizer.Services;

/// <summary>
/// Maps PID -> Windows service name(s), mainly so that a generic "svchost"
/// row can show which services it actually hosts.
///
/// Replaces `tasklist /svc /fo csv`, which spawned a process on every refresh
/// and whose "N/A" placeholder is localized (on a Russian Windows it prints
/// "Н/Д", so the old parser silently dropped every service name).
/// Uses <c>EnumServicesStatusEx</c> instead: no child process, no text parsing,
/// no language dependency. The result is cached for a few seconds because
/// service-to-PID assignments change rarely.
/// </summary>
internal static class ServiceMap
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);
    private static readonly object Gate = new();
    private static Dictionary<int, string> _cache = new();
    private static DateTime _stamp = DateTime.MinValue;

    public static Dictionary<int, string> Get()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _stamp < Ttl)
                return _cache;

            try { _cache = Enumerate(); }
            catch { /* keep the previous snapshot */ }

            _stamp = DateTime.UtcNow;
            return _cache;
        }
    }

    // ---------------- Native ----------------

    private const int SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const int SERVICE_WIN32 = 0x00000030;
    private const int SERVICE_STATE_ALL = 0x00000003;
    private const int ERROR_MORE_DATA = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS_PROCESS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
        public int dwProcessId;
        public int dwServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ENUM_SERVICE_STATUS_PROCESS
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string lpServiceName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpDisplayName;
        public SERVICE_STATUS_PROCESS ServiceStatusProcess;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, int access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "EnumServicesStatusExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(
        IntPtr hSCManager, int infoLevel, int serviceType, int serviceState,
        IntPtr services, int bufSize, out int bytesNeeded, out int servicesReturned,
        ref int resumeHandle, string? groupName);

    private static Dictionary<int, string> Enumerate()
    {
        var map = new Dictionary<int, string>();

        IntPtr scm = OpenSCManager(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return map;

        IntPtr buffer = IntPtr.Zero;
        try
        {
            int resume = 0;
            int bufSize = 0;

            // First call sizes the buffer.
            EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                IntPtr.Zero, 0, out int needed, out _, ref resume, null);
            if (needed <= 0) return map;

            bufSize = needed;
            buffer = Marshal.AllocHGlobal(bufSize);
            int structSize = Marshal.SizeOf<ENUM_SERVICE_STATUS_PROCESS>();

            do
            {
                bool ok = EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_STATE_ALL,
                    buffer, bufSize, out needed, out int returned, ref resume, null);

                if (!ok && Marshal.GetLastWin32Error() != ERROR_MORE_DATA)
                    break;

                for (int i = 0; i < returned; i++)
                {
                    var entry = Marshal.PtrToStructure<ENUM_SERVICE_STATUS_PROCESS>(buffer + i * structSize);
                    int pid = entry.ServiceStatusProcess.dwProcessId;
                    if (pid <= 0 || string.IsNullOrEmpty(entry.lpServiceName)) continue;

                    map[pid] = map.TryGetValue(pid, out var existing)
                        ? existing + ", " + entry.lpServiceName
                        : entry.lpServiceName;
                }

                if (ok) break;          // everything fetched
                if (resume == 0) break; // nothing more to resume from
            }
            while (true);
        }
        catch { /* fall through with whatever was collected */ }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            CloseServiceHandle(scm);
        }

        return map;
    }
}
