namespace NetOptimizer.Models;

public class LogEntry
{
    public DateTime Time { get; init; }
    public string Level { get; init; } = "";
    public string Source { get; init; } = "";
    public long EventId { get; init; }
    public string Message { get; init; } = "";

    // Full date AND time so problems can be pinpointed.
    public string TimeText => Time.ToString("dd.MM.yyyy  HH:mm:ss");
}
