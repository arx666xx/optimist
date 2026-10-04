using System.Diagnostics;
using NetOptimizer.Models;

namespace NetOptimizer.Services;

public sealed record BoostOptions(
    int? TargetPid,
    bool FreeMemory,
    bool PurgeCache,
    bool LowerOthers,
    bool CloseOthersConnections);

public sealed record BoostResult(MemorySnapshot Before, MemorySnapshot After, List<string> Notes);

/// <summary>
/// "Ускорение": frees RAM and, optionally, the network link for one application.
///
/// Honest about what each part does:
///  * memory — trims background programs' working sets and drops the standby
///    cache (see <see cref="MemoryService"/>); this is what moves the RAM gauge;
///  * CPU priority — helps the chosen app stay responsive, but does NOT give it
///    more bandwidth: Windows has no per-process network priority from user mode;
///  * closing background connections — the part that actually frees the link.
///
/// Every priority it changes is remembered, so "Вернуть как было" restores the
/// original values instead of flattening everything to Normal.
/// Processes protected by <see cref="ProcessGuard"/> are never touched.
/// </summary>
public static class BoostService
{
    /// <summary>PID → (process start time, priority before we touched it).</summary>
    private static readonly Dictionary<int, (DateTime Start, ProcessPriorityClass Original)> Changed = new();
    private static readonly object Gate = new();

    public static bool HasChanges { get { lock (Gate) return Changed.Count > 0; } }

    public static BoostResult Apply(BoostOptions o, IReadOnlyList<ConnectionInfo> connections)
    {
        var notes = new List<string>();
        var before = MemoryService.GetStatus();

        // The boosted app and every process of the same program (browsers run dozens).
        var targetPids = new HashSet<int>();
        string? targetName = null;
        if (o.TargetPid is int tp)
        {
            targetName = ProcessGuard.NameOf(tp);
            targetPids.Add(tp);
            if (targetName != null)
                foreach (var c in connections)
                    if (c.ProcessName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                        targetPids.Add(c.Pid);
        }

        // ---- Priorities ----
        if (o.TargetPid is int target)
        {
            if (SetPriority(target, ProcessPriorityClass.High))
                notes.Add($"✓ «{targetName}» получил высокий приоритет процессора.");
            else
                notes.Add($"✗ Не удалось повысить приоритет «{targetName ?? "PID " + target}».");
        }

        if (o.LowerOthers)
        {
            int lowered = 0;
            foreach (int pid in connections.Select(c => c.Pid).Distinct())
            {
                if (pid <= 4 || targetPids.Contains(pid)) continue;
                if (ProcessGuard.IsProtected(ProcessGuard.NameOf(pid))) continue;
                if (SetPriority(pid, ProcessPriorityClass.BelowNormal)) lowered++;
            }
            notes.Add(lowered > 0
                ? $"✓ Фоновым программам понижен приоритет: {lowered}."
                : "• Фоновых программ, которым можно понизить приоритет, не нашлось.");
        }

        // ---- Network ----
        if (o.CloseOthersConnections)
        {
            int closed = 0;
            foreach (var c in connections)
            {
                if (c.Pid <= 4 || targetPids.Contains(c.Pid)) continue;
                if (ProcessGuard.IsProtected(c.ProcessName)) continue;
                if (c.Protocol != "TCP" || c.IsIPv6) continue;
                if (c.State != "ESTABLISHED" || !AppGroup.IsExternal(c)) continue;

                if (ProcessActions.CloseConnection(c).Ok) closed++;
            }
            notes.Add($"✓ Закрыто фоновых соединений: {closed}.");
        }

        // ---- Memory ----
        if (o.FreeMemory)
        {
            int trimmed = MemoryService.TrimWorkingSets(targetPids);
            notes.Add($"✓ Освобождена память у фоновых программ: {trimmed}.");
        }

        if (o.PurgeCache)
        {
            string? err = MemoryService.PurgeStandbyList();
            notes.Add(err == null ? "✓ Системный кэш памяти очищен." : "✗ " + err);
        }

        // Windows updates its counters a moment after the trim.
        if (o.FreeMemory || o.PurgeCache) Thread.Sleep(800);
        var after = MemoryService.GetStatus();

        ActionLog.Action($"Ускорение: цель {targetName ?? "—"}, память {before.LoadPercent}% → {after.LoadPercent}%.");
        return new BoostResult(before, after, notes);
    }

    /// <summary>Puts back every priority this session changed.</summary>
    public static string RestorePriorities()
    {
        List<KeyValuePair<int, (DateTime Start, ProcessPriorityClass Original)>> items;
        lock (Gate)
        {
            items = Changed.ToList();
            Changed.Clear();
        }

        if (items.Count == 0)
            return "Приоритеты не менялись — возвращать нечего.";

        int restored = 0;
        foreach (var (pid, saved) in items)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                // Same PID but a different start time: the original process is gone.
                if (p.StartTime != saved.Start) continue;
                p.PriorityClass = saved.Original;
                restored++;
            }
            catch { /* exited */ }
        }

        ActionLog.Action($"Приоритеты возвращены у {restored} процессов.");
        return $"Приоритеты возвращены как было у {restored} процессов.";
    }

    private static bool SetPriority(int pid, ProcessPriorityClass priority)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var original = p.PriorityClass;
            var start = p.StartTime;
            if (original == priority) return true;

            p.PriorityClass = priority;
            lock (Gate)
            {
                // Keep the very first original if boosted twice (unless the PID was recycled).
                if (!Changed.TryGetValue(pid, out var e) || e.Start != start)
                    Changed[pid] = (start, original);
            }
            return true;
        }
        catch { return false; }
    }
}
