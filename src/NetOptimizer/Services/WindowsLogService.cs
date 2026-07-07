using System.Diagnostics;
using NetOptimizer.Models;

namespace NetOptimizer.Services;

/// <summary>
/// Reads Windows event logs (System / Application / Security) newest-first.
/// Great for finding why the PC shut down, crashed, or an app failed.
/// Security log requires administrator rights.
/// </summary>
public static class WindowsLogService
{
    // Bound the scan so filtering for rare errors doesn't walk a huge log forever.
    private const int MaxScan = 6000;

    public static List<LogEntry> Read(string logName, bool onlyErrors, int maxResults)
    {
        var result = new List<LogEntry>();
        try
        {
            using var log = new EventLog(logName);
            var entries = log.Entries;
            int count = entries.Count;
            int scanned = 0;

            for (int i = count - 1; i >= 0 && result.Count < maxResults && scanned < MaxScan; i--, scanned++)
            {
                EventLogEntry e;
                try { e = entries[i]; }
                catch { continue; }

                var type = e.EntryType;
                if (onlyErrors && type != EventLogEntryType.Error && type != EventLogEntryType.Warning)
                    continue;

                string message;
                try { message = e.Message ?? ""; }
                catch { message = "(текст события недоступен — отсутствует библиотека сообщений)"; }

                message = message.Replace("\r", " ").Replace("\n", " ").Trim();
                if (message.Length > 400) message = message.Substring(0, 400) + "…";

                result.Add(new LogEntry
                {
                    Time = e.TimeGenerated,
                    Level = LevelName(type),
                    Source = e.Source ?? "",
                    EventId = e.InstanceId & 0xFFFF, // low word = human-visible Event ID
                    Message = message
                });
            }
        }
        catch (Exception ex)
        {
            result.Add(new LogEntry
            {
                Time = DateTime.Now,
                Level = "Ошибка",
                Source = "NetOptimizer",
                EventId = 0,
                Message = "Не удалось прочитать журнал: " + ex.Message +
                          " (для журнала «Безопасность» нужны права администратора)."
            });
        }
        return result;
    }

    private static string LevelName(EventLogEntryType t) => t switch
    {
        EventLogEntryType.Error => "Ошибка",
        EventLogEntryType.Warning => "Предупреждение",
        EventLogEntryType.Information => "Сведения",
        EventLogEntryType.SuccessAudit => "Аудит успеха",
        EventLogEntryType.FailureAudit => "Аудит отказа",
        _ => t.ToString()
    };
}
