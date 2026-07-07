using System.Diagnostics;
using NetOptimizer.Models;

namespace NetOptimizer.Services;

public sealed record BoostResult(int Raised, int Lowered, int ConnectionsClosed, List<string> Notes);

/// <summary>
/// "Game boost": raises the chosen app's priority and, optionally, lowers other
/// apps' priority and closes their background connections to free up resources.
/// System-critical processes are never touched.
/// </summary>
public static class BoostService
{
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "System Idle", "Idle", "Registry", "Memory Compression",
        "svchost", "services", "lsass", "csrss", "wininit", "winlogon", "smss",
        "dwm", "explorer", "fontdrvhost", "spoolsv", "conhost", "RuntimeBroker",
        "dllhost", "taskhostw", "SearchIndexer", "SearchHost", "ctfmon",
        "audiodg", "WmiPrvSE", "NetOptimizer"
    };

    public static BoostResult Apply(int targetPid, IReadOnlyList<ConnectionInfo> connections,
        bool lowerOthers, bool closeOthersConnections)
    {
        var notes = new List<string>();
        int raised = 0, lowered = 0, closed = 0;

        try
        {
            using var p = Process.GetProcessById(targetPid);
            p.PriorityClass = ProcessPriorityClass.High;
            raised = 1;
            notes.Add($"Приоритет «{p.ProcessName}» повышен до «Высокий».");
        }
        catch (Exception ex)
        {
            notes.Add("Не удалось повысить приоритет выбранного приложения: " + ex.Message);
        }

        var otherPids = connections
            .Select(c => c.Pid)
            .Distinct()
            .Where(pid => pid != targetPid && pid > 4)
            .ToList();

        if (lowerOthers)
        {
            foreach (var pid in otherPids)
            {
                try
                {
                    using var pr = Process.GetProcessById(pid);
                    if (Protected.Contains(pr.ProcessName)) continue;
                    pr.PriorityClass = ProcessPriorityClass.BelowNormal;
                    lowered++;
                }
                catch { /* exited or access denied */ }
            }
            notes.Add($"Понижен приоритет у {lowered} фоновых приложений.");
        }

        if (closeOthersConnections)
        {
            foreach (var c in connections)
            {
                if (c.Pid == targetPid || c.Pid <= 4) continue;
                if (Protected.Contains(c.ProcessName)) continue;
                if (c.Protocol != "TCP" || c.IsIPv6) continue;
                if (!c.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) continue;

                if (ProcessActions.CloseConnection(c).Ok) closed++;
            }
            notes.Add($"Закрыто фоновых соединений: {closed}.");
        }

        return new BoostResult(raised, lowered, closed, notes);
    }

    /// <summary>Restores Normal priority for all non-system processes seen in the list.</summary>
    public static string ResetPriorities(IReadOnlyList<ConnectionInfo> connections)
    {
        int n = 0;
        foreach (var pid in connections.Select(c => c.Pid).Distinct())
        {
            if (pid <= 4) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (Protected.Contains(p.ProcessName)) continue;
                p.PriorityClass = ProcessPriorityClass.Normal;
                n++;
            }
            catch { }
        }
        return $"Приоритеты возвращены к «Обычный» у {n} процессов.";
    }
}
