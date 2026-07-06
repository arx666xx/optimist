using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetOptimizer.Services;

/// <summary>
/// Measures per-process network throughput (bytes/sec) using an ETW
/// kernel session (TCP/IP + UDP/IP send/receive events). Requires admin.
/// Fails gracefully: if the session can't start, <see cref="Available"/> is false
/// and rates simply stay at zero — the rest of the app keeps working.
///
/// Note: only IPv4 traffic is counted to keep the event surface small and robust.
/// </summary>
public sealed class TrafficMonitor : IDisposable
{
    private readonly ConcurrentDictionary<int, long> _recv = new();
    private readonly ConcurrentDictionary<int, long> _sent = new();
    private Dictionary<int, long> _prevRecv = new();
    private Dictionary<int, long> _prevSent = new();
    private readonly ConcurrentDictionary<int, (double down, double up)> _rates = new();

    private TraceEventSession? _session;
    private Thread? _thread;
    private System.Threading.Timer? _timer;

    public bool Available { get; private set; }

    public void Start()
    {
        try
        {
            _session = new TraceEventSession("NetOptimizerNetSession") { StopOnDispose = true };
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);

            var k = _session.Source.Kernel;
            // Inside handlers we only touch base TraceEvent members (ProcessID, PayloadByName)
            // so we don't depend on derived property names.
            k.TcpIpSend += d => Add(_sent, d.ProcessID, GetSize(d));
            k.TcpIpRecv += d => Add(_recv, d.ProcessID, GetSize(d));
            k.UdpIpSend += d => Add(_sent, d.ProcessID, GetSize(d));
            k.UdpIpRecv += d => Add(_recv, d.ProcessID, GetSize(d));

            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch { /* session stopped */ }
            })
            { IsBackground = true, Name = "ETW-Net" };
            _thread.Start();

            _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
            Available = true;
        }
        catch
        {
            Available = false;
        }
    }

    private static int GetSize(TraceEvent d)
    {
        try
        {
            object? o = d.PayloadByName("size");
            return o switch
            {
                int i => i,
                uint u => (int)u,
                long l => (int)l,
                null => 0,
                _ => Convert.ToInt32(o)
            };
        }
        catch { return 0; }
    }

    private static void Add(ConcurrentDictionary<int, long> d, int pid, int size)
    {
        if (pid <= 0 || size <= 0) return;
        d.AddOrUpdate(pid, size, (_, v) => v + size);
    }

    private void Tick()
    {
        var pids = new HashSet<int>(_recv.Keys);
        foreach (var k in _sent.Keys) pids.Add(k);

        foreach (var pid in pids)
        {
            long r = _recv.TryGetValue(pid, out var rv) ? rv : 0;
            long s = _sent.TryGetValue(pid, out var sv) ? sv : 0;
            long pr = _prevRecv.TryGetValue(pid, out var prv) ? prv : 0;
            long ps = _prevSent.TryGetValue(pid, out var psv) ? psv : 0;
            _rates[pid] = (Math.Max(0, r - pr), Math.Max(0, s - ps)); // interval = 1 s -> bytes/sec
        }

        _prevRecv = new Dictionary<int, long>(_recv);
        _prevSent = new Dictionary<int, long>(_sent);
    }

    public (double down, double up) GetRate(int pid)
        => _rates.TryGetValue(pid, out var x) ? x : (0d, 0d);

    public void Dispose()
    {
        try { _timer?.Dispose(); } catch { }
        try { _session?.Dispose(); } catch { }
    }
}
