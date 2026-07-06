using System.Runtime.InteropServices;

namespace NetOptimizer.Services;

internal static class NativeMethods
{
    // ---- Closing an IPv4 TCP connection (SetTcpEntry) ----
    [StructLayout(LayoutKind.Sequential)]
    public struct MIB_TCPROW
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
    }

    public const uint MIB_TCP_STATE_DELETE_TCB = 12;

    [DllImport("iphlpapi.dll")]
    public static extern int SetTcpEntry(ref MIB_TCPROW pTcpRow);

    // ---- Suspend / resume a process ----
    [DllImport("ntdll.dll")]
    public static extern uint NtSuspendProcess(IntPtr hProcess);

    [DllImport("ntdll.dll")]
    public static extern uint NtResumeProcess(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
}
