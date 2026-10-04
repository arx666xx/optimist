using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using NetOptimizer.Services;

namespace NetOptimizer.Models;

/// <summary>
/// One application in the main list: every connection of every process that runs
/// the same executable, folded into a single row. This is what a person actually
/// asks about ("what is Chrome doing?"), instead of forty lines of
/// "TCP 192.168.0.5:52144 → 142.250.74.14:443 ESTABLISHED".
///
/// Speed is summed over distinct PIDs, so it is the application's real rate —
/// the per-connection table used to repeat the whole process rate on every row.
/// </summary>
public sealed class AppGroup : INotifyPropertyChanged
{
    public AppGroup(string key) => Key = key;

    /// <summary>Executable path (lower-case) or process name when the path is unreadable.</summary>
    public string Key { get; }

    public string ProcessName { get; private set; } = "";
    public string? ProcessPath { get; private set; }
    public ImageSource? Icon { get; private set; }
    public string Signature { get; private set; } = "";
    public string ServiceNames { get; private set; } = "";

    public IReadOnlyList<ConnectionInfo> Connections { get; private set; } = Array.Empty<ConnectionInfo>();
    public IReadOnlyList<int> Pids { get; private set; } = Array.Empty<int>();

    // ---------------- Display ----------------

    private string _title = "";
    /// <summary>Human name: "Google Chrome", "Службы Windows", or the exe name as a fallback.</summary>
    public string Title { get => _title; private set => Set(ref _title, value); }

    private string _subtitle = "";
    /// <summary>"chrome.exe · 3 процесса · 24 подключения".</summary>
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    private string _activity = "";
    /// <summary>One plain sentence about what the app is doing on the network.</summary>
    public string Activity { get => _activity; private set => Set(ref _activity, value); }

    private bool _suspicious;
    public bool Suspicious { get => _suspicious; private set => Set(ref _suspicious, value); }

    private bool _unsigned;
    public bool Unsigned { get => _unsigned; private set => Set(ref _unsigned, value); }

    private bool _isOnline;
    /// <summary>Has a live connection to the outside world or is moving data right now.</summary>
    public bool IsOnline { get => _isOnline; private set => Set(ref _isOnline, value); }

    private int _remoteCount;
    public int RemoteCount { get => _remoteCount; private set => Set(ref _remoteCount, value); }

    // ---------------- Traffic ----------------

    private double _down;
    public double DownRate
    {
        get => _down;
        private set { if (Math.Abs(_down - value) > 0.5) { _down = value; Raise(); Raise(nameof(DownRateText)); Raise(nameof(Rate)); } }
    }

    private double _up;
    public double UpRate
    {
        get => _up;
        private set { if (Math.Abs(_up - value) > 0.5) { _up = value; Raise(); Raise(nameof(UpRateText)); Raise(nameof(Rate)); } }
    }

    public double Rate => _down + _up;

    public string DownRateText => _down >= 1 ? ConnectionInfo.FormatRate(_down) : "—";
    public string UpRateText => _up >= 1 ? ConnectionInfo.FormatRate(_up) : "—";

    private long _total;
    /// <summary>Bytes moved since NetOptimizer started.</summary>
    public long TotalBytes
    {
        get => _total;
        private set { if (_total != value) { _total = value; Raise(); Raise(nameof(TotalText)); } }
    }

    public string TotalText => _total > 0 ? UsageStats.FormatBytes(_total) : "—";

    // ---------------- Update ----------------

    public static string KeyOf(ConnectionInfo c)
        => string.IsNullOrEmpty(c.ProcessPath) ? "name:" + c.ProcessName.ToLowerInvariant() : c.ProcessPath.ToLowerInvariant();

    /// <summary>Refreshes the row from this tick's connections of the application.</summary>
    public void Update(List<ConnectionInfo> conns, TrafficMonitor traffic)
    {
        var first = conns[0];
        Connections = conns;
        Pids = conns.Select(c => c.Pid).Distinct().ToList();
        ProcessName = first.ProcessName;
        ProcessPath = first.ProcessPath;
        Icon ??= first.Icon;
        Signature = first.Signature;

        bool isSvchost = ProcessName.Equals("svchost", StringComparison.OrdinalIgnoreCase);
        ServiceNames = isSvchost
            ? string.Join(", ", conns.Select(c => c.Description)
                                     .Where(d => d.Length > 0)
                                     .SelectMany(d => d.Split(", "))
                                     .Distinct(StringComparer.OrdinalIgnoreCase)
                                     .OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            : "";

        Title = isSvchost ? "Службы Windows"
              : first.Description.Length > 0 ? first.Description
              : ProcessName;

        var external = conns.Where(IsExternal).ToList();
        RemoteCount = external.Select(c => c.RemoteAddress).Distinct().Count();
        int listening = conns.Count(c => c.State == "LISTENING" || (c.Protocol == "UDP" && c.RemotePort == 0));

        string exe = ProcessPath != null ? Path.GetFileName(ProcessPath) : ProcessName;
        var parts = new List<string> { exe };
        if (Pids.Count > 1) parts.Add(Plural(Pids.Count, "процесс", "процесса", "процессов"));
        parts.Add(Plural(conns.Count, "подключение", "подключения", "подключений"));
        Subtitle = string.Join(" · ", parts);

        Suspicious = conns.Any(c => c.Suspicious);
        Unsigned = Signature == "Не подписан";

        double down = 0, up = 0;
        long total = 0;
        foreach (int pid in Pids)
        {
            var (d, u) = traffic.GetRate(pid);
            var (td, tu) = traffic.GetTotal(pid);
            down += d; up += u; total += td + tu;
        }
        DownRate = down;
        UpRate = up;
        TotalBytes = total;

        IsOnline = external.Count > 0 || down + up >= 1;

        Activity = RemoteCount switch
        {
            0 when listening > 0 => "Ждёт входящих подключений, сейчас ни с кем не связано",
            0 => "Сейчас не обменивается данными с интернетом",
            1 => "Связано с 1 сервером",
            _ => $"Связано с {Plural(RemoteCount, "сервером", "серверами", "серверами")}"
        };
    }

    /// <summary>A live connection to some other machine (not loopback, not a listener).</summary>
    public static bool IsExternal(ConnectionInfo c)
        => c.Protocol == "TCP"
           && c.State == "ESTABLISHED"
           && c.RemotePort > 0
           && !IsLocal(c.RemoteAddress);

    public static bool IsLocal(string addr)
        => addr is "" or "*" or "0.0.0.0" or "::" or "::1"
           || addr.StartsWith("127.", StringComparison.Ordinal)
           || addr.StartsWith("::ffff:127.", StringComparison.OrdinalIgnoreCase);

    public static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        string word = m10 == 1 && m100 != 11 ? one
                    : m10 is >= 2 and <= 4 && (m100 < 12 || m100 > 14) ? few
                    : many;
        return $"{n} {word}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(n);
    }
}

/// <summary>One remote server an application talks to, for the details panel.</summary>
public sealed class RemoteItem : INotifyPropertyChanged
{
    public string Address { get; init; } = "";
    public int Port { get; init; }
    public int Count { get; init; }

    /// <summary>"Веб (HTTPS)", "DNS", "Порт 27015"…</summary>
    public string Service => PortNames.Describe(Port);

    private string _host = "";
    /// <summary>Reverse-DNS name when known, otherwise empty.</summary>
    public string Host
    {
        get => _host;
        set { if (_host != value) { _host = value; Raise(nameof(Host)); Raise(nameof(Display)); } }
    }

    private string _country = "";
    public string Country
    {
        get => _country;
        set { if (_country != value) { _country = value; Raise(nameof(Country)); } }
    }

    /// <summary>Host name if resolved, the bare address otherwise.</summary>
    public string Display => _host.Length > 0 && _host != "(не найдено)" ? _host : Address;

    public string Detail
    {
        get
        {
            var parts = new List<string> { Service };
            string kind = AddressKind(Address);
            if (kind.Length > 0) parts.Add(kind);
            if (Count > 1) parts.Add(AppGroup.Plural(Count, "подключение", "подключения", "подключений"));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Explains addresses that are not real internet servers, so nobody goes
    /// looking up who "198.18.0.94" is.
    /// </summary>
    public static string AddressKind(string ip)
    {
        if (!System.Net.IPAddress.TryParse(ip, out var a)) return "";
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();

        if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            byte[] b = a.GetAddressBytes();
            if (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168))
                return "в домашней сети";
            if (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                return "через VPN (адрес-заглушка)";
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                return "через VPN или провайдера";
            if (b[0] == 169 && b[1] == 254)
                return "локальная связь";
            return "";
        }

        if (a.IsIPv6LinkLocal) return "локальная связь";
        if (a.IsIPv6UniqueLocal) return "в домашней сети";
        return "";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
