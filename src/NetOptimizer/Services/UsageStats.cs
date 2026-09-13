using System.IO;
using System.Text.Json;

namespace NetOptimizer.Services;

/// <summary>
/// Per-application traffic totals, accumulated by day and kept on disk so the
/// question "who ate 40 GB last week" has an answer after a reboot.
///
/// <see cref="TrafficMonitor"/> counts by PID and only while the app runs; this
/// turns those counters into deltas, files them under the process *name* and
/// writes one small JSON per day to %LOCALAPPDATA%\NetOptimizer\usage.
/// </summary>
public static class UsageStats
{
    public sealed class Entry
    {
        public long Down { get; set; }
        public long Up { get; set; }
    }

    private static readonly object Gate = new();

    /// <summary>Last cumulative counter seen per PID, to turn totals into deltas.</summary>
    private static readonly Dictionary<int, (long down, long up)> LastSeen = new();

    private static Dictionary<string, Entry> _today = new(StringComparer.OrdinalIgnoreCase);
    private static string _todayKey = "";
    private static bool _dirty;

    private static string Dir => Path.Combine(SettingsService.DataDir, "usage");
    private static string FileFor(DateTime d) => Path.Combine(Dir, d.ToString("yyyy-MM-dd") + ".json");

    /// <summary>
    /// Feeds one process's cumulative counters in. Only the growth since the last
    /// call is added; a counter that went backwards means the PID was recycled,
    /// so counting restarts from there instead of adding a bogus spike.
    /// </summary>
    public static void Record(int pid, string processName, long cumulativeDown, long cumulativeUp)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;

        lock (Gate)
        {
            EnsureToday();

            long dDown = cumulativeDown, dUp = cumulativeUp;
            if (LastSeen.TryGetValue(pid, out var prev))
            {
                dDown = cumulativeDown >= prev.down ? cumulativeDown - prev.down : cumulativeDown;
                dUp = cumulativeUp >= prev.up ? cumulativeUp - prev.up : cumulativeUp;
            }
            LastSeen[pid] = (cumulativeDown, cumulativeUp);

            if (dDown <= 0 && dUp <= 0) return;

            if (!_today.TryGetValue(processName, out var e))
                _today[processName] = e = new Entry();
            e.Down += dDown;
            e.Up += dUp;
            _dirty = true;
        }
    }

    /// <summary>Totals per application over the last <paramref name="days"/> days (1 = today).</summary>
    public static List<(string Name, long Down, long Up)> GetRange(int days)
    {
        var acc = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        lock (Gate)
        {
            EnsureToday();

            for (int i = 0; i < days; i++)
            {
                var day = DateTime.Today.AddDays(-i);
                var data = day.ToString("yyyy-MM-dd") == _todayKey ? _today : Load(FileFor(day));
                foreach (var kv in data)
                {
                    if (!acc.TryGetValue(kv.Key, out var e))
                        acc[kv.Key] = e = new Entry();
                    e.Down += kv.Value.Down;
                    e.Up += kv.Value.Up;
                }
            }
        }

        return acc.Select(kv => (kv.Key, kv.Value.Down, kv.Value.Up))
                  .OrderByDescending(x => x.Down + x.Up)
                  .ToList();
    }

    /// <summary>Writes the current day to disk if anything changed.</summary>
    public static void Flush()
    {
        lock (Gate)
        {
            if (!_dirty || _todayKey.Length == 0) return;
            try
            {
                Directory.CreateDirectory(Dir);
                string json = JsonSerializer.Serialize(_today, new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(FileFor(DateTime.Today), json);
                _dirty = false;
            }
            catch (Exception ex)
            {
                ActionLog.Warn("Не удалось сохранить статистику трафика: " + ex.Message);
            }
        }
    }

    /// <summary>Deletes stored days older than the retention window.</summary>
    public static void Prune(int keepDays = 60)
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var cutoff = DateTime.Today.AddDays(-keepDays);
            foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
            {
                string stem = Path.GetFileNameWithoutExtension(f);
                if (DateTime.TryParse(stem, out var d) && d < cutoff)
                    File.Delete(f);
            }
        }
        catch { /* housekeeping only */ }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            _today.Clear();
            LastSeen.Clear();
            _dirty = true;
            try
            {
                if (Directory.Exists(Dir))
                    foreach (var f in Directory.EnumerateFiles(Dir, "*.json")) File.Delete(f);
            }
            catch { }
            ActionLog.Action("Статистика трафика очищена пользователем.");
        }
    }

    // Caller holds the lock.
    private static void EnsureToday()
    {
        string key = DateTime.Today.ToString("yyyy-MM-dd");
        if (key == _todayKey) return;

        Flush();                       // the app may have been running past midnight
        _todayKey = key;
        _today = Load(FileFor(DateTime.Today));
        _dirty = false;
    }

    private static Dictionary<string, Entry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var parsed = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path));
            return parsed == null
                ? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Entry>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>Human-readable byte size, e.g. "1,4 ГБ".</summary>
    public static string FormatBytes(double b)
    {
        if (b < 1) return "—";
        if (b < 1024) return $"{b:0} Б";
        if (b < 1024L * 1024) return $"{b / 1024:0.0} КБ";
        if (b < 1024L * 1024 * 1024) return $"{b / 1024 / 1024:0.0} МБ";
        return $"{b / 1024 / 1024 / 1024:0.00} ГБ";
    }
}
