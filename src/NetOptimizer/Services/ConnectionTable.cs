using System.Net;
using System.Runtime.InteropServices;

namespace NetOptimizer.Services;

/// <summary>One raw TCP/UDP row as reported by the Windows IP Helper API.</summary>
internal readonly record struct NetRow(
    string Protocol,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    string State,
    int Pid);

/// <summary>
/// Enumerates TCP/UDP connections directly through <c>iphlpapi.dll</c>
/// (<c>GetExtendedTcpTable</c> / <c>GetExtendedUdpTable</c>), for both IPv4 and IPv6.
///
/// Replaces the previous `netstat -ano` parsing:
///  * no child process is spawned every refresh (netstat took 80-200 ms per tick);
///  * output does not depend on the Windows display language — connection states
///    come back as numeric constants, so nothing breaks on a localized system.
/// </summary>
internal static class ConnectionTable
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;

    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;

    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int dwOutBufLen,
        [MarshalAs(UnmanagedType.Bool)] bool sort, int ipVersion, int tblClass, int reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int dwOutBufLen,
        [MarshalAs(UnmanagedType.Bool)] bool sort, int ipVersion, int tblClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint dwState;
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwRemoteAddr;
        public uint dwRemotePort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] ucRemoteAddr;
        public uint dwRemoteScopeId;
        public uint dwRemotePort;
        public uint dwState;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDPROW_OWNER_PID
    {
        public uint dwLocalAddr;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_UDP6ROW_OWNER_PID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] ucLocalAddr;
        public uint dwLocalScopeId;
        public uint dwLocalPort;
        public uint dwOwningPid;
    }

    public static List<NetRow> GetAll()
    {
        var rows = new List<NetRow>(512);
        ReadTcp4(rows);
        ReadTcp6(rows);
        ReadUdp4(rows);
        ReadUdp6(rows);
        return rows;
    }

    // ---------------- TCP ----------------

    private static void ReadTcp4(List<NetRow> rows) => ReadTable(
        tcp: true, af: AF_INET, rowType: typeof(MIB_TCPROW_OWNER_PID),
        read: ptr =>
        {
            var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(ptr);
            string remote = Ip4(r.dwRemoteAddr);
            int remotePort = Port(r.dwRemotePort);
            return new NetRow("TCP", Ip4(r.dwLocalAddr), Port(r.dwLocalPort),
                remote, remotePort, TcpState(r.dwState), (int)r.dwOwningPid);
        }, rows);

    private static void ReadTcp6(List<NetRow> rows) => ReadTable(
        tcp: true, af: AF_INET6, rowType: typeof(MIB_TCP6ROW_OWNER_PID),
        read: ptr =>
        {
            var r = Marshal.PtrToStructure<MIB_TCP6ROW_OWNER_PID>(ptr);
            return new NetRow("TCP",
                Ip6(r.ucLocalAddr, r.dwLocalScopeId), Port(r.dwLocalPort),
                Ip6(r.ucRemoteAddr, r.dwRemoteScopeId), Port(r.dwRemotePort),
                TcpState(r.dwState), (int)r.dwOwningPid);
        }, rows);

    // ---------------- UDP ----------------

    private static void ReadUdp4(List<NetRow> rows) => ReadTable(
        tcp: false, af: AF_INET, rowType: typeof(MIB_UDPROW_OWNER_PID),
        read: ptr =>
        {
            var r = Marshal.PtrToStructure<MIB_UDPROW_OWNER_PID>(ptr);
            return new NetRow("UDP", Ip4(r.dwLocalAddr), Port(r.dwLocalPort),
                "*", 0, "", (int)r.dwOwningPid);
        }, rows);

    private static void ReadUdp6(List<NetRow> rows) => ReadTable(
        tcp: false, af: AF_INET6, rowType: typeof(MIB_UDP6ROW_OWNER_PID),
        read: ptr =>
        {
            var r = Marshal.PtrToStructure<MIB_UDP6ROW_OWNER_PID>(ptr);
            return new NetRow("UDP", Ip6(r.ucLocalAddr, r.dwLocalScopeId), Port(r.dwLocalPort),
                "*", 0, "", (int)r.dwOwningPid);
        }, rows);

    // ---------------- Shared table reader ----------------

    private static void ReadTable(bool tcp, int af, Type rowType, Func<IntPtr, NetRow> read, List<NetRow> sink)
    {
        int size = 0;
        int tblClass = tcp ? TCP_TABLE_OWNER_PID_ALL : UDP_TABLE_OWNER_PID;

        uint ret = tcp
            ? GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, tblClass, 0)
            : GetExtendedUdpTable(IntPtr.Zero, ref size, false, af, tblClass, 0);

        if (ret != ERROR_INSUFFICIENT_BUFFER && ret != 0) return;
        if (size <= 0) return;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            ret = tcp
                ? GetExtendedTcpTable(buffer, ref size, false, af, tblClass, 0)
                : GetExtendedUdpTable(buffer, ref size, false, af, tblClass, 0);
            if (ret != 0) return;

            int count = Marshal.ReadInt32(buffer);
            if (count <= 0) return;

            int rowSize = Marshal.SizeOf(rowType);
            // The table is { DWORD dwNumEntries; ROW table[]; } — rows start at offset 4.
            IntPtr p = buffer + 4;
            for (int i = 0; i < count; i++)
            {
                sink.Add(read(p));
                p += rowSize;
            }
        }
        catch
        {
            // Malformed table — return whatever was read so far.
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ---------------- Helpers ----------------

    private static string Ip4(uint addr) => new IPAddress(BitConverter.GetBytes(addr)).ToString();

    private static string Ip6(byte[] addr, uint scopeId)
    {
        try { return new IPAddress(addr, scopeId).ToString(); }
        catch { return "::"; }
    }

    /// <summary>Ports come back in network byte order in the low 16 bits.</summary>
    private static int Port(uint p) => (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF));

    /// <summary>
    /// MIB_TCP_STATE -> the same uppercase names netstat used, so existing
    /// filters and UI text keep working — but now independent of system language.
    /// </summary>
    private static string TcpState(uint state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTENING",
        3 => "SYN_SENT",
        4 => "SYN_RECEIVED",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT1",
        7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => "UNKNOWN"
    };
}
