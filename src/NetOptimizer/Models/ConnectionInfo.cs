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

    private bool _suspicious;
    public bool Suspicious
    {
        get => _suspicious;
        set { if (_suspicious != value) { _suspicious = value; OnChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
