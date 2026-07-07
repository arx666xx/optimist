using System.Diagnostics;
using System.Text;
using NetOptimizer.Models;

namespace NetOptimizer.Services;

/// <summary>
/// Enumerates active TCP/UDP connections using `netstat -ano`
/// (rock-solid, no fragile struct marshalling) and enriches each
/// row with the owning process name, icon, and a friendly description
/// (product name, or the Windows service(s) hosted by svchost).
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
        var result = new List<ConnectionInfo>();
        string output = RunProcess("netstat", "-ano", Encoding.ASCII);
        if (string.IsNullOrWhiteSpace(output))
            return result;

        var procCache = BuildProcessCache();
        var servicesByPid = GetServicesByPid();

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;

            string proto = parts[0].ToUpperInvariant();
            if (proto != "TCP" && proto != "UDP") continue;

            string localTok, remoteTok, state;
            int pid;

            if (proto == "TCP" && parts.Length >= 5)
            {
                localTok = parts[1];
                remoteTok = parts[2];
                state = parts[3];
                if (!int.TryParse(parts[4], out pid)) continue;
            }
            else if (proto == "UDP" && parts.Length >= 4)
            {
                localTok = parts[1];
                remoteTok = parts[2];
                state = "";
                if (!int.TryParse(parts[3], out pid)) continue;
            }
            else continue;

            var (la, lp) = ParseEndpoint(localTok);
            var (ra, rp) = ParseEndpoint(remoteTok);

            procCache.TryGetValue(pid, out var proc);
            string name = proc.Name ?? (pid == 0 ? "System Idle" : $"PID {pid}");
            string? path = proc.Path;

            // Friendly description: hosted services (svchost) > product name > empty.
            string description;
            if (servicesByPid.TryGetValue(pid, out var svc) && !string.IsNullOrWhiteSpace(svc))
                description = svc;
            else if (!string.IsNullOrWhiteSpace(proc.FileDescription))
                description = proc.FileDescription!;
            else
                description = "";

            var info = new ConnectionInfo
            {
                Protocol = proto,
                LocalAddress = la,
                LocalPort = lp,
                RemoteAddress = ra,
                RemotePort = rp,
                State = state,
                Pid = pid,
                ProcessName = name,
                ProcessPath = path,
                Description = description,
                Icon = IconHelper.Get(path),
            };
            info.Suspicious = IsSuspicious(info);
            result.Add(info);
        }

        return result
            .OrderBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Protocol)
            .ToList();
    }

    private static bool IsSuspicious(ConnectionInfo c)
    {
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

    private static (string addr, int port) ParseEndpoint(string s)
    {
        if (string.IsNullOrEmpty(s)) return ("", 0);
        int idx = s.LastIndexOf(':');
        if (idx < 0) return (s, 0);

        string addr = s.Substring(0, idx).Trim('[', ']');
        string portStr = s.Substring(idx + 1);
        int port = int.TryParse(portStr, out var p) ? p : 0;
        return (addr, port);
    }

    private readonly record struct ProcMeta(string? Name, string? Path, string? FileDescription);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> DescCache = new();

    private static Dictionary<int, ProcMeta> BuildProcessCache()
    {
        var dict = new Dictionary<int, ProcMeta>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path = null;
                string? desc = null;
                try
                {
                    path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                        desc = DescCache.GetOrAdd(path, GetFileDescription);
                }
                catch { /* protected process */ }
                dict[p.Id] = new ProcMeta(p.ProcessName, path, desc);
            }
            catch { /* process exited */ }
            finally { p.Dispose(); }
        }
        return dict;
    }

    private static string? GetFileDescription(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path).FileDescription; }
        catch { return null; }
    }

    /// <summary>Maps PID -> comma-separated Windows service names (mainly for svchost).</summary>
    private static Dictionary<int, string> GetServicesByPid()
    {
        var map = new Dictionary<int, string>();
        // /svc = show services, /fo csv = machine-readable, /nh = no header
        string output = RunProcess("tasklist", "/svc /fo csv /nh", null);
        if (string.IsNullOrWhiteSpace(output))
            return map;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var fields = ParseCsvLine(line);
            if (fields.Count < 3) continue;
            if (!int.TryParse(fields[1], out int pid)) continue;

            string services = fields[2].Trim();
            if (services.Length == 0 || services.Equals("N/A", StringComparison.OrdinalIgnoreCase))
                continue;

            map[pid] = services;
        }
        return map;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (char ch in line)
        {
            if (ch == '"') inQuotes = !inQuotes;
            else if (ch == ',' && !inQuotes) { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    private static string RunProcess(string file, string args, Encoding? encoding)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (encoding != null) psi.StandardOutputEncoding = encoding;

            using var proc = Process.Start(psi);
            if (proc == null) return "";
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            return output;
        }
        catch
        {
            return "";
        }
    }
}
