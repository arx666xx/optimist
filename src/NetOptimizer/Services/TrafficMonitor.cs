using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace NetOptimizer.Services;

/// <summary>
/// Measures per-process network throughput using an ETW kernel session
/// (TCP/IP + UDP/IP send/receive events). Requires admin.
/// Fails gracefully: if the session can't start, <see cref="Available"/> is false
/// and rates simply stay at zero — the rest of the app keeps working.
///
/// Besides the instantaneous rate it keeps two things the UI needs:
///   * cumulative totals per process since the app started ("who ate my traffic"),
///   * a 60-second ring of rate samples for the sparkline.
/// Dead processes are pruned so a long session does not grow without bound.
/// </summary>
public sealed class TrafficMonitor : IDisposable
{
    private const int HistoryLength = 60;
    private const int IdleTicksBeforePrune = 600; // 10 minutes of silence

    private readonly ConcurrentDictionary<int, long> _recv = new();
    private readonly ConcurrentDictionary<int, long> _sent = new();
    private Dictionary<int, long> _prevRecv = new();
    private Dictionary<int, long> _prevSent = new();
    private readonly ConcurrentDictionary<int, (double down, double up)> _rates = new();
    private readonly ConcurrentDictionary<int, double[]> _history = new();
    private readonly ConcurrentDictionary<int, int> _idleTicks = new();

    private TraceEventSession? _session;
    private Thread? _thread;
    private System.Threading.Timer? _timer;
    private int _tick;

    public bool Available { get; private set; }

    /// <summary>Moment the counters started, so the UI can say "за 2 ч 14 мин".</summary>
    public DateTime StartedAt { get; private set; } = DateTime.Now;

    public void Start()
    {
        try
        {
            // A session with this name may survive a crash; clearing it first
            // stops the restart from failing with "already exists".
            TryStopStaleSession();

            _session = new TraceEventSession(SessionName) { StopOnDispose = true };
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
            StartedAt = DateTime.Now;
            Available = true;
        }
        catch (Exception ex)
        {
            Available = false;
            ActionLog.Warn("Мониторинг трафика недоступен: " + ex.Message);
        }
    }

    private const string SessionName = "NetOptimizerNetSession";

    /// <summary>
    /// An ETW session outlives the process that created it, so after a crash the
    /// old one is still registered and creating it again fails. Attach and stop it.
    /// </summary>
    private static void TryStopStaleSession()
    {
        try
        {
            foreach (string name in TraceEventSession.GetActiveSessionNames())
            {
                if (!string.Equals(name, SessionName, StringComparison.OrdinalIgnoreCase)) continue;
                using var stale = new TraceEventSession(SessionName, TraceEventSessionOptions.Attach);
                stale.Stop();
                break;
            }
        }
        catch { /* nothing to clean up, or no permission — Start() will report it */ }
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
        _tick++;

        var pids = new HashSet<int>(_recv.Keys);
        foreach (var k in _sent.Keys) pids.Add(k);

        foreach (var pid in pids)
        {
            long r = _recv.TryGetValue(pid, out var rv) ? rv : 0;
            long s = _sent.TryGetValue(pid, out var sv) ? sv : 0;
            long pr = _prevRecv.TryGetValue(pid, out var prv) ? prv : 0;
            long ps = _prevSent.TryGetValue(pid, out var psv) ? psv : 0;

            double down = Math.Max(0, r - pr);   // interval = 1 s -> bytes/sec
            double up = Math.Max(0, s - ps);
            _rates[pid] = (down, up);

            PushHistory(pid, down + up);

            // Prune processes that stopped transferring a long time ago.
            if (down + up > 0) _idleTicks[pid] = 0;
            else if (_idleTicks.AddOrUpdate(pid, 1, (_, v) => v + 1) > IdleTicksBeforePrune)
                Forget(pid);
        }

        _prevRecv = new Dictionary<int, long>(_recv);
        _prevSent = new Dictionary<int, long>(_sent);
    }

    private void PushHistory(int pid, double value)
    {
        var buf = _history.GetOrAdd(pid, _ => new double[HistoryLength]);
        lock (buf) buf[_tick % HistoryLength] = value;
    }

    private void Forget(int pid)
    {
        _recv.TryRemove(pid, out _);
        _sent.TryRemove(pid, out _);
        _rates.TryRemove(pid, out _);
        _history.TryRemove(pid, out _);
        _idleTicks.TryRemove(pid, out _);
        _prevRecv.Remove(pid);
        _prevSent.Remove(pid);
    }

    /// <summary>Current speed in bytes per second.</summary>
    public (double down, double up) GetRate(int pid)
        => _rates.TryGetValue(pid, out var x) ? x : (0d, 0d);

    /// <summary>Bytes transferred by this PID since the app started.</summary>
    public (long down, long up) GetTotal(int pid)
        => (_recv.TryGetValue(pid, out var r) ? r : 0,
            _sent.TryGetValue(pid, out var s) ? s : 0);

    /// <summary>Last 60 one-second samples (bytes/sec, oldest first) for a sparkline.</summary>
    public double[] GetHistory(int pid)
    {
        if (!_history.TryGetValue(pid, out var buf)) return Array.Empty<double>();

        var copy = new double[HistoryLength];
        lock (buf)
        {
            int start = (_tick + 1) % HistoryLength;
            for (int i = 0; i < HistoryLength; i++)
                copy[i] = buf[(start + i) % HistoryLength];
        }
        return copy;
    }

    /// <summary>Every PID that has transferred anything, for the statistics view.</summary>
    public IEnumerable<int> KnownPids()
    {
        var set = new HashSet<int>(_recv.Keys);
        foreach (var k in _sent.Keys) set.Add(k);
        return set;
    }

    public void Dispose()
    {
        try { _timer?.Dispose(); } catch { }
        try { _session?.Dispose(); } catch { }
        try { _thread?.Join(1500); } catch { }
    }
}
