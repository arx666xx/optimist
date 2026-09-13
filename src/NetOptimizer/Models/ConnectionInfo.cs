using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace NetOptimizer.Models;

/// <summary>
/// One network connection / listener row.
/// </summary>
public class ConnectionInfo : INotifyPropertyChanged
{
    public string Protocol { get; init; } = "";        // "TCP" or "UDP"
    public string LocalAddress { get; init; } = "";
    public int LocalPort { get; init; }
    public string RemoteAddress { get; init; } = "";
    public int RemotePort { get; init; }
    public int Pid { get; init; }
    public string ProcessName { get; init; } = "";
    public string? ProcessPath { get; init; }

    /// <summary>Friendly description: product name, or hosted service(s) for svchost.</summary>
    public string Description { get; init; } = "";

    /// <summary>Small icon extracted from the executable (frozen, thread-safe).</summary>
    public ImageSource? Icon { get; init; }

    /// <summary>Authenticode signature status: "Подписан" / "Не подписан" / "".</summary>
    public string Signature { get; init; } = "";

    public bool IsIPv6 => LocalAddress.Contains(':');

    // Stable identity across refreshes.
    public string Key => $"{Protocol}|{LocalAddress}:{LocalPort}|{RemoteAddress}:{RemotePort}|{Pid}";

    public string LocalEndpoint => $"{LocalAddress}:{LocalPort}";
    public string RemoteEndpoint => RemotePort > 0 ? $"{RemoteAddress}:{RemotePort}" : RemoteAddress;

    private string _state = "";
    public string State
    {
        get => _state;
        set { if (_state != value) { _state = value; OnChanged(); } }
    }

    private string _remoteHost = "";
    public string RemoteHost
    {
        get => _remoteHost;
        set { if (_remoteHost != value) { _remoteHost = value; OnChanged(); } }
    }

    private string _country = "";
    public string Country
    {
        get => _country;
        set { if (_country != value) { _country = value; OnChanged(); } }
    }

    private bool _suspicious;
    public bool Suspicious
    {
        get => _suspicious;
        set { if (_suspicious != value) { _suspicious = value; OnChanged(); } }
    }

    private double _downloadRate;
    public double DownloadRate
    {
        get => _downloadRate;
        set { if (Math.Abs(_downloadRate - value) > 0.5) { _downloadRate = value; Raise(nameof(DownloadRate)); Raise(nameof(DownloadRateText)); } }
    }

    private double _uploadRate;
    public double UploadRate
    {
        get => _uploadRate;
        set { if (Math.Abs(_uploadRate - value) > 0.5) { _uploadRate = value; Raise(nameof(UploadRate)); Raise(nameof(UploadRateText)); } }
    }

    public string DownloadRateText => FormatRate(_downloadRate);
    public string UploadRateText => FormatRate(_uploadRate);

    private long _totalDown;
    /// <summary>Bytes received by this process since the app was started.</summary>
    public long TotalDown
    {
        get => _totalDown;
        set { if (_totalDown != value) { _totalDown = value; Raise(nameof(TotalDown)); Raise(nameof(TotalText)); } }
    }

    private long _totalUp;
    /// <summary>Bytes sent by this process since the app was started.</summary>
    public long TotalUp
    {
        get => _totalUp;
        set { if (_totalUp != value) { _totalUp = value; Raise(nameof(TotalUp)); Raise(nameof(TotalText)); } }
    }

    public long TotalBytes => _totalDown + _totalUp;

    /// <summary>Combined volume for the session, e.g. "1,4 ГБ".</summary>
    public string TotalText => TotalBytes > 0 ? Services.UsageStats.FormatBytes(TotalBytes) : "";

    public static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec < 1) return "";
        if (bytesPerSec < 1024) return $"{bytesPerSec:0} Б/с";
        if (bytesPerSec < 1024 * 1024) return $"{bytesPerSec / 1024:0.0} КБ/с";
        return $"{bytesPerSec / 1024 / 1024:0.00} МБ/с";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    private void Raise(string n)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
