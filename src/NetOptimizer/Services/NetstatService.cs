using NetOptimizer.Models;

namespace NetOptimizer.Services;

/// <summary>
/// Builds the connection list shown in the UI.
///
/// Data sources (all native, no child processes, no localized text):
///   * <see cref="ConnectionTable"/> — TCP/UDP rows from iphlpapi (IPv4 + IPv6);
///   * <see cref="ProcessCache"/>    — PID -> name / path / description, cached;
///   * <see cref="ServiceMap"/>      — PID -> Windows service names for svchost.
///
/// The class name is kept for compatibility with the rest of the app; it no
/// longer shells out to netstat.
/// </summary>
public static class NetstatService
{
    private static readonly string[] SuspiciousFolders =
    {
        @"\appdata\local\temp\",
        @"\windows\temp\",
        @"\temp\",
        @"\downloads\",
        @"\$recycle.bin\",
    };

    public static List<ConnectionInfo> GetConnections()
    {
        var rows = ConnectionTable.GetAll();
        if (rows.Count == 0) return new List<ConnectionInfo>();

        var procs = ProcessCache.Resolve(rows.Select(r => r.Pid));
        var services = ServiceMap.Get();

        var result = new List<ConnectionInfo>(rows.Count);

        foreach (var r in rows)
        {
            procs.TryGetValue(r.Pid, out var proc);
            string name = string.IsNullOrEmpty(proc.Name) ? $"PID {r.Pid}" : proc.Name;

            // Friendly description: hosted services (svchost) > product name > empty.
            string description;
            if (services.TryGetValue(r.Pid, out var svc) && !string.IsNullOrWhiteSpace(svc))
                description = svc;
            else
                description = string.IsNullOrEmpty(proc.Description) ? "" : proc.Description;

            var info = new ConnectionInfo
            {
                Protocol = r.Protocol,
                LocalAddress = r.LocalAddress,
                LocalPort = r.LocalPort,
                RemoteAddress = r.RemoteAddress,
                RemotePort = r.RemotePort,
                State = r.State,
                Pid = r.Pid,
                ProcessName = name,
                ProcessPath = proc.Path,
                Description = description,
                Icon = IconHelper.Get(proc.Path),
                Signature = SignatureService.Get(proc.Path),
            };
            info.Suspicious = IsSuspicious(info);
            result.Add(info);
        }

        return result
            .OrderBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Protocol, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsSuspicious(ConnectionInfo c)
    {
        // Trusted processes are never flagged.
        if (SettingsService.IsTrusted(c.ProcessName))
            return false;

        if (!string.IsNullOrEmpty(c.ProcessPath))
        {
            string p = c.ProcessPath!.ToLowerInvariant();
            foreach (var f in SuspiciousFolders)
                if (p.Contains(f)) return true;
        }

        bool hasRemote = c.RemotePort > 0 && c.RemoteAddress != "*"
                         && c.RemoteAddress != "0.0.0.0" && c.RemoteAddress != "::";
        if (hasRemote && c.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)
                      && c.ProcessName.StartsWith("PID ", StringComparison.Ordinal))
            return true;

        return false;
    }
}
