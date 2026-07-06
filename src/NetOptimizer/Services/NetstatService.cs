using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using NetOptimizer.Models;

namespace NetOptimizer.Services;

/// <summary>
/// Enumerates active TCP/UDP connections using `netstat -ano`
/// (rock-solid, no fragile struct marshalling) and enriches each
/// row with the owning process name / path.
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
        string output = RunNetstat();
        if (string.IsNullOrWhiteSpace(output))
            return result;

        var procCache = BuildProcessCache();

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
        // Running from a temp / downloads location and talking to a remote host.
        if (!string.IsNullOrEmpty(c.ProcessPath))
        {
            string p = c.ProcessPath!.ToLowerInvariant();
            foreach (var f in SuspiciousFolders)
                if (p.Contains(f)) return true;
        }

        // Established connection to a public IP from an unnamed process.
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

    private readonly record struct ProcMeta(string? Name, string? Path);

    private static Dictionary<int, ProcMeta> BuildProcessCache()
    {
        var dict = new Dictionary<int, ProcMeta>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* protected process */ }
                dict[p.Id] = new ProcMeta(p.ProcessName, path);
            }
            catch { /* process exited */ }
            finally { p.Dispose(); }
        }
        return dict;
    }

    private static string RunNetstat()
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.ASCII,
            };
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
