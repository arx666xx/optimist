using System.IO;
using System.Text;

namespace NetOptimizer.Services;

/// <summary>
/// Append-only log of everything the app did with administrator rights, plus the
/// errors it used to swallow silently.
///
/// Two reasons it exists: a user who wonders "what did I just close?" can look,
/// and a developer who gets "it doesn't work" has something to ask for. The file
/// lives next to the settings and rotates at 1 MB.
/// </summary>
public static class ActionLog
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1024 * 1024;

    public static string FilePath => Path.Combine(SettingsService.DataDir, "actions.log");

    /// <summary>A privileged operation the user triggered.</summary>
    public static void Action(string message) => Write("ДЕЙСТВИЕ", message);

    /// <summary>Something did not work, but the app carried on.</summary>
    public static void Warn(string message) => Write("ПРЕДУПР.", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ОШИБКА", ex == null ? message : $"{message} — {ex.GetType().Name}: {ex.Message}");

    public static void Info(string message) => Write("ИНФО", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsService.DataDir);
                Rotate();
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {level,-10} {message.Replace("\r", " ").Replace("\n", " ")}{Environment.NewLine}";
                File.AppendAllText(FilePath, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never be the reason an action fails.
        }
    }

    private static void Rotate()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists || fi.Length < MaxBytes) return;

            string old = FilePath + ".1";
            if (File.Exists(old)) File.Delete(old);
            File.Move(FilePath, old);
        }
        catch { }
    }
}
