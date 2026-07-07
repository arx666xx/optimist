using System.Diagnostics;
using System.IO;
using System.Windows;

namespace NetOptimizer.Services;

public static class SettingsService
{
    public static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NetOptimizer");

    private static string ThemeFile => Path.Combine(DataDir, "theme.txt");

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
