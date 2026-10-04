using System.Diagnostics;
using System.IO;
using System.Windows;

namespace NetOptimizer.Services;

public static class SettingsService
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetOptimizer");

    private static string ThemeFile => Path.Combine(DataDir, "theme.txt");
    private static string TrustedFile => Path.Combine(DataDir, "trusted.txt");

    private static HashSet<string>? _trusted;

    private static HashSet<string> Trusted
    {
        get
        {
            if (_trusted == null)
            {
                _trusted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(TrustedFile))
                        foreach (var line in File.ReadAllLines(TrustedFile))
                            if (!string.IsNullOrWhiteSpace(line)) _trusted.Add(line.Trim());
                }
                catch { }
            }
            return _trusted;
        }
    }

    public static bool IsTrusted(string processName) =>
        !string.IsNullOrEmpty(processName) && Trusted.Contains(processName);

    public static void AddTrusted(string processName) => ToggleTrusted(processName, true);
    public static void RemoveTrusted(string processName) => ToggleTrusted(processName, false);

    private static void ToggleTrusted(string processName, bool add)
    {
        if (string.IsNullOrWhiteSpace(processName)) return;
        if (add) Trusted.Add(processName); else Trusted.Remove(processName);
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllLines(TrustedFile, Trusted);
        }
        catch { }
    }

    public static string LoadTheme()
    {
        try
        {
            if (File.Exists(ThemeFile))
            {
                string t = File.ReadAllText(ThemeFile).Trim();
                if (t == ThemeService.Light || t == ThemeService.Dark) return t;
            }
        }
        catch { }
        return ThemeService.Dark;
    }

    public static void SaveTheme(string theme)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(ThemeFile, theme);
        }
        catch { }
    }

    private static string RebootFile => Path.Combine(DataDir, "reboot.txt");

    /// <summary>Auto-reboot delay in seconds (0..300). Default 300 (5 min).</summary>
    public static int LoadRebootDelaySeconds()
    {
        try
        {
            if (File.Exists(RebootFile) &&
                int.TryParse(File.ReadAllText(RebootFile).Trim(), out var s) &&
                s >= 0 && s <= 300)
                return s;
        }
        catch { }
        return 300;
    }

    public static void SaveRebootDelaySeconds(int seconds)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(RebootFile, seconds.ToString());
        }
        catch { }
    }

    /// <summary>
    /// Fully removes the app: writes a temp script that waits for this process
    /// to exit, deletes the executable, helper files and settings, then deletes itself.
    /// </summary>
    public static void Uninstall()
    {
        string exe = Environment.ProcessPath
                     ?? Process.GetCurrentProcess().MainModule!.FileName;
        string localData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetOptimizer");
        string bat = Path.Combine(Path.GetTempPath(), "netoptimizer_uninstall.cmd");

        // Same rules as the updater script: paths go in as arguments (a .cmd is
        // read in the OEM code page, so a Cyrillic user name baked into the file
        // would be mangled), `ping` instead of `timeout` (which fails without a
        // console), and a bounded retry instead of spinning forever.
        const string script =
@"@echo off
setlocal EnableExtensions
set ""TARGET=%~1""
set ""ROAMING=%~2""
set ""LOCAL=%~3""
for %%I in (""%TARGET%"") do set ""DIR=%%~dpI""

set /a tries=0
:retry
ping -n 2 127.0.0.1 >nul
del ""%TARGET%"" >nul 2>&1
if exist ""%TARGET%"" (
  set /a tries+=1
  if %tries% lss 30 goto retry
)

del ""%DIR%NetOptimizer.old.exe"" >nul 2>&1
del ""%DIR%netoptimizer_update.bat"" >nul 2>&1
del ""%DIR%NetOptimizer_new.exe"" >nul 2>&1
rd /s /q ""%ROAMING%"" >nul 2>&1
rd /s /q ""%LOCAL%"" >nul 2>&1
(goto) 2>nul & del ""%~f0""
";
        File.WriteAllText(bat, script, new System.Text.UTF8Encoding(false));

        Process.Start(new ProcessStartInfo("cmd.exe", CmdLine(bat, exe, DataDir, localData))
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });

        Application.Current.Shutdown();
    }

    /// <summary>
    /// Arguments for <c>cmd.exe</c> running a script with quoted parameters.
    /// Without /s, cmd strips the first and last quote of the line whenever the
    /// script path itself is quoted (a user name with a space), breaking every
    /// argument. With /s it always strips exactly that outer pair, so the outer
    /// pair is added on purpose. Windows paths cannot contain quotes.
    /// </summary>
    public static string CmdLine(string script, params string[] args)
        => "/d /s /c \"\"" + script + "\" " + string.Join(" ", args.Select(a => "\"" + a + "\"")) + "\"";
}
