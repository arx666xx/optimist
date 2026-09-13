using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace NetOptimizer.Services;

/// <summary>
/// Checks GitHub Releases for a newer version, downloads it and swaps it in.
///
/// SECURITY: the downloaded file is executed with administrator rights, so it is
/// never trusted on the strength of "it came from an https URL" alone. Before the
/// swap happens the update must pass, in order:
///   1. the release must publish a SHA-256 checksum (a `*.sha256` / `SHA256SUMS`
///      asset, or a checksum written into the release notes) — no checksum, no update;
///   2. the downloaded bytes must hash to exactly that value;
///   3. the file must be a real Windows executable (PE header);
///   4. if the currently running .exe is Authenticode-signed, the new one must be
///      signed by the same publisher — this stops a swapped release asset from
///      silently replacing a signed build with an unsigned one.
/// Any failure aborts, deletes the download and leaves the installed version alone.
/// </summary>
public static class UpdateService
{
    // ↓↓↓ ПРОВЕРЬ И ПРИ НЕОБХОДИМОСТИ ИСПРАВЬ НА СВОИ ЗНАЧЕНИЯ ↓↓↓
    public const string Owner = "arx666xx";
    public const string Repo = "optimist";
    // ↑↑↑ логин на GitHub и имя репозитория ↑↑↑

    public sealed record UpdateInfo(Version Version, string DownloadUrl, string? Sha256, long Size, string Notes);

    /// <summary>Raised when an update cannot be trusted — the message is user-facing.</summary>
    public sealed class UpdateRejectedException : Exception
    {
        public UpdateRejectedException(string message) : base(message) { }
    }

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0, 0);

    private static string ExePath =>
        Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;

    private static string StagingDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "NetOptimizer", "update");

    private static HttpClient CreateClient()
    {
        var http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
        http.DefaultRequestHeaders.UserAgent.ParseAdd("NetOptimizer-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.Timeout = TimeSpan.FromMinutes(5);
        return http;
    }

    // ---------------- Check ----------------

    /// <summary>Returns update info if a newer release exists, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = CreateClient();
        string url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

        using var resp = await http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new UpdateRejectedException(
                $"В репозитории {Owner}/{Repo} нет опубликованных релизов " +
                "(или указаны неверные логин и имя репозитория в UpdateService).");
        if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
            throw new UpdateRejectedException(
                "GitHub временно ограничил число запросов. Попробуйте через несколько минут.");
        resp.EnsureSuccessStatusCode();

        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
        Version? latest = ParseVersion(tag);
        if (latest == null) return null;
        if (Normalize(latest) <= Normalize(CurrentVersion)) return null;

        string? downloadUrl = null;
        long size = 0;
        string? checksumUrl = null;

        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                string? assetUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (assetUrl == null) continue;

                if (IsChecksumAsset(name))
                    checksumUrl ??= assetUrl;
                else if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && downloadUrl == null)
                {
                    downloadUrl = assetUrl;
                    size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                }
            }
        }

        if (string.IsNullOrEmpty(downloadUrl)) return null;

        string notes = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";

        string? sha = null;
        if (checksumUrl != null)
        {
            try { sha = ExtractSha256(await http.GetStringAsync(checksumUrl, ct)); }
            catch { /* fall back to the notes below */ }
        }
        sha ??= ExtractSha256(notes);

        return new UpdateInfo(latest, downloadUrl!, sha, size, notes);
    }

    private static bool IsChecksumAsset(string name) =>
        name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".sha256.txt", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("SHA256SUMS", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("checksums.txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pulls the first 64-hex-character token out of a checksum file or release notes.</summary>
    private static string? ExtractSha256(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"\b[A-Fa-f0-9]{64}\b");
        return m.Success ? m.Value.ToLowerInvariant() : null;
    }

    // ---------------- Download, verify, apply ----------------

    /// <summary>
    /// Downloads the new .exe, verifies it, then launches a swap-and-restart script.
    /// Throws <see cref="UpdateRejectedException"/> with a user-readable reason if
    /// anything about the download cannot be trusted.
    /// </summary>
    public static async Task DownloadAndApplyAsync(UpdateInfo info,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.Sha256))
            throw new UpdateRejectedException(
                "У этого релиза не опубликована контрольная сумма SHA-256, поэтому подлинность " +
                "файла проверить нельзя — обновление отменено.\n\n" +
                "Добавьте в релиз файл NetOptimizer.exe.sha256 (это делает GitHub Actions) " +
                "или обновите программу вручную со страницы релизов.");

        Directory.CreateDirectory(StagingDir);
        string staged = Path.Combine(StagingDir, $"NetOptimizer-{info.Version}.exe");

        try
        {
            await DownloadAsync(info, staged, progress, ct);
            Verify(staged, info);
        }
        catch
        {
            TryDelete(staged);
            throw;
        }

        ApplyAndRestart(staged);
    }

    private static async Task DownloadAsync(UpdateInfo info, string destination,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var http = CreateClient();
        using var resp = await http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? info.Size;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[81920];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
        }
    }

    private static void Verify(string file, UpdateInfo info)
    {
        var fi = new FileInfo(file);
        if (!fi.Exists || fi.Length < 64 * 1024)
            throw new UpdateRejectedException("Загруженный файл повреждён или пуст — обновление отменено.");

        // 1. Checksum.
        string actual = Sha256File(file);
        if (!string.Equals(actual, info.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UpdateRejectedException(
                "Контрольная сумма загруженного файла не совпадает с опубликованной в релизе.\n\n" +
                $"Ожидалось: {info.Sha256}\nПолучено:  {actual}\n\n" +
                "Файл мог быть повреждён при загрузке или подменён. Обновление отменено.");

        // 2. Real Windows executable.
        if (!IsPortableExecutable(file))
            throw new UpdateRejectedException("Загруженный файл не является программой Windows — обновление отменено.");

        // 3. Publisher must not silently change on a signed build.
        string? currentPublisher = SignatureService.GetSubject(ExePath);
        if (!string.IsNullOrEmpty(currentPublisher))
        {
            string? newPublisher = SignatureService.GetSubject(file);
            if (!string.Equals(currentPublisher, newPublisher, StringComparison.OrdinalIgnoreCase))
                throw new UpdateRejectedException(
                    "Цифровая подпись обновления не совпадает с подписью установленной версии — обновление отменено.");
        }
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static bool IsPortableExecutable(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return fs.ReadByte() == 'M' && fs.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    /// <summary>
    /// Replaces the running .exe. A running executable cannot overwrite itself, so a
    /// small script waits for this process to exit, keeps the old file as a backup,
    /// moves the new one in, restarts it and finally cleans up after itself.
    /// </summary>
    private static void ApplyAndRestart(string staged)
    {
        string current = ExePath;
        string backup = Path.ChangeExtension(current, null) + ".old.exe";
        string script = Path.Combine(StagingDir, "apply_update.cmd");

        // Paths are passed as arguments rather than baked into the file: a .cmd is read
        // in the OEM code page, so a Cyrillic user name in the path would otherwise be
        // mangled. `ping` is used instead of `timeout`, which fails without a console.
        const string body =
@"@echo off
setlocal EnableExtensions
set ""TARGET=%~1""
set ""STAGED=%~2""
set ""BACKUP=%~3""
for %%I in (""%TARGET%"") do set ""DIR=%%~dpI""

set /a tries=0
:wait
ping -n 2 127.0.0.1 >nul
del ""%BACKUP%"" >nul 2>&1
move /y ""%TARGET%"" ""%BACKUP%"" >nul 2>&1
if exist ""%TARGET%"" (
  set /a tries+=1
  if %tries% lss 30 goto wait
  goto fail
)

move /y ""%STAGED%"" ""%TARGET%"" >nul 2>&1
if not exist ""%TARGET%"" goto rollback

rem Success: drop the backup and leftovers from older update schemes.
del ""%BACKUP%"" >nul 2>&1
del ""%DIR%NetOptimizer_new.exe"" >nul 2>&1
del ""%DIR%netoptimizer_update.bat"" >nul 2>&1
start """" ""%TARGET%""
goto done

:rollback
move /y ""%BACKUP%"" ""%TARGET%"" >nul 2>&1
start """" ""%TARGET%""
goto done

:fail
start """" ""%TARGET%""

:done
del ""%STAGED%"" >nul 2>&1
(goto) 2>nul & del ""%~f0""
";
        File.WriteAllText(script, body, new System.Text.UTF8Encoding(false));

        var psi = new ProcessStartInfo("cmd.exe")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add(current);
        psi.ArgumentList.Add(staged);
        psi.ArgumentList.Add(backup);
        Process.Start(psi);

        Application.Current.Shutdown();
    }

    // ---------------- Housekeeping ----------------

    /// <summary>
    /// Removes files left behind by an interrupted or completed update:
    /// the backup copy, the old-style helper files, and stale staged downloads.
    /// Safe to call at every startup.
    /// </summary>
    public static void CleanupLeftovers()
    {
        try
        {
            string dir = Path.GetDirectoryName(ExePath)!;
            TryDelete(Path.ChangeExtension(ExePath, null) + ".old.exe");
            TryDelete(Path.Combine(dir, "NetOptimizer_new.exe"));
            TryDelete(Path.Combine(dir, "netoptimizer_update.bat"));

            if (Directory.Exists(StagingDir))
                foreach (var f in Directory.EnumerateFiles(StagingDir))
                    TryDelete(f);
        }
        catch { /* housekeeping must never break startup */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---------------- Version helpers ----------------

    private static Version? ParseVersion(string tag)
    {
        tag = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(tag, out var v) ? v : null;
    }

    private static Version Normalize(Version v)
        => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>Safe "1.7.0" formatting — Version.ToString(3) throws on a two-part tag.</summary>
    public static string Format(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";
}
