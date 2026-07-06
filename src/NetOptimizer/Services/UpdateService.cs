using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace NetOptimizer.Services;

/// <summary>
/// Checks the project's GitHub Releases for a newer version and,
/// if requested, downloads the new .exe and swaps it in via a small
/// batch script (a running .exe can't overwrite itself directly).
///
/// IMPORTANT: set <see cref="Owner"/> and <see cref="Repo"/> to your repo.
/// Updates are pulled only from https://github.com/{Owner}/{Repo}/releases (HTTPS).
/// </summary>
public static class UpdateService
{
    // ↓↓↓ ПРОВЕРЬ И ПРИ НЕОБХОДИМОСТИ ИСПРАВЬ НА СВОИ ЗНАЧЕНИЯ ↓↓↓
    public const string Owner = "arx666xx";
    public const string Repo = "optimist";
    // ↑↑↑ логин на GitHub и имя репозитория ↑↑↑

    public sealed record UpdateInfo(Version Version, string DownloadUrl, string Notes);

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    private static HttpClient CreateClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetOptimizer-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }

    /// <summary>Returns update info if a newer release exists, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync()
    {
        using var http = CreateClient();
        string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
        string json = await http.GetStringAsync(url);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
        Version? latest = ParseVersion(tag);
        if (latest == null) return null;

        // Normalize both to 3 parts for a fair comparison.
        if (Normalize(latest) <= Normalize(CurrentVersion))
            return null;

        string? downloadUrl = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    downloadUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    break;
                }
            }
        }
        if (string.IsNullOrEmpty(downloadUrl)) return null;

        string notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
        return new UpdateInfo(latest, downloadUrl!, notes);
    }

    /// <summary>Downloads the new exe and launches a swap-and-restart script.</summary>
    public static async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        string current = Environment.ProcessPath
                         ?? Process.GetCurrentProcess().MainModule!.FileName;
        string dir = Path.GetDirectoryName(current)!;
        string newExe = Path.Combine(dir, "NetOptimizer_new.exe");

        using (var http = CreateClient())
        {
            byte[] bytes = await http.GetByteArrayAsync(info.DownloadUrl);
            await File.WriteAllBytesAsync(newExe, bytes);
        }

        string bat = Path.Combine(dir, "netoptimizer_update.bat");
        string script =
$@"@echo off
timeout /t 2 /nobreak >nul
:retry
del ""{current}"" >nul 2>&1
if exist ""{current}"" (
  timeout /t 1 /nobreak >nul
  goto retry
)
move /y ""{newExe}"" ""{current}"" >nul
start """" ""{current}""
del ""%~f0"" >nul 2>&1
";
        await File.WriteAllTextAsync(bat, script);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });

        Application.Current.Shutdown();
    }

    private static Version? ParseVersion(string tag)
    {
        tag = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(tag, out var v) ? v : null;
    }

    private static Version Normalize(Version v)
        => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
