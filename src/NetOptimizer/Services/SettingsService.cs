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
        string dir = Path.GetDirectoryName(exe)!;
        string bat = Path.Combine(Path.GetTempPath(), "netoptimizer_uninstall.bat");

        string script =
$@"@echo off
timeout /t 2 /nobreak >nul
:retry
del ""{exe}"" >nul 2>&1
if exist ""{exe}"" (
  timeout /t 1 /nobreak >nul
  goto retry
)
del ""{dir}\netoptimizer_update.bat"" >nul 2>&1
del ""{dir}\NetOptimizer_new.exe"" >nul 2>&1
rd /s /q ""{DataDir}"" >nul 2>&1
del ""%~f0"" >nul 2>&1
";
        File.WriteAllText(bat, script);

        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{bat}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        });

        Application.Current.Shutdown();
    }
}
