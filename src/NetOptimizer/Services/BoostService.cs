using System.Diagnostics;
using NetOptimizer.Models;

namespace NetOptimizer.Services;

public sealed record BoostResult(int Raised, int Lowered, int ConnectionsClosed, List<string> Notes);

/// <summary>
/// Frees the channel for one application.
///
/// Honest about what each part does: raising the CPU priority helps the app stay
/// responsive but does NOT give it more bandwidth — Windows has no per-process
/// network priority available from user mode. The part that actually helps the
/// connection is the second one: lowering background apps and closing their
/// established connections, so they stop competing for the link.
///
/// Processes protected by <see cref="ProcessGuard"/> are never touched.
/// </summary>
public static class BoostService
{
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
            notes.Add($"Приоритет «{p.ProcessName}» повышен до «Высокий» (процессор, не сеть).");
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
                    if (ProcessGuard.IsProtected(pr.ProcessName)) continue;
                    pr.PriorityClass = ProcessPriorityClass.BelowNormal;
                    lowered++;
                }
                catch { /* exited or access denied */ }
            }
            notes.Add($"Понижен приоритет у {lowered} фоновых приложений.");
            if (lowered == 0) notes.Add("Фоновых приложений, которым можно понизить приоритет, не нашлось.");
        }

        if (closeOthersConnections)
        {
            foreach (var c in connections)
            {
                if (c.Pid == targetPid || c.Pid <= 4) continue;
                if (ProcessGuard.IsProtected(c.ProcessName)) continue;
                if (c.Protocol != "TCP" || c.IsIPv6) continue;
                if (!c.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)) continue;

                if (ProcessActions.CloseConnection(c).Ok) closed++;
            }
            notes.Add($"Закрыто фоновых соединений: {closed}.");
        }

        ActionLog.Action($"Разгрузка сети для PID {targetPid}: понижено приоритетов {lowered}, закрыто соединений {closed}.");
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
                if (ProcessGuard.IsProtected(p.ProcessName)) continue;
                p.PriorityClass = ProcessPriorityClass.Normal;
                n++;
            }
            catch { }
        }
        ActionLog.Action($"Приоритеты возвращены к «Обычный» у {n} процессов.");
        return $"Приоритеты возвращены к «Обычный» у {n} процессов.";
    }
}
