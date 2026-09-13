using System.Diagnostics;
using System.Text;

namespace NetOptimizer.Services;

/// <summary>
/// Runs standard, built-in Windows network-repair commands.
/// These are exactly what Microsoft support recommends for fixing
/// broken connectivity left behind by VPN software (corrupted Winsock,
/// TCP/IP stack, stale DNS cache, etc.).
/// </summary>
public static class NetworkRepair
{
    public static string Run(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = ConsoleText.Oem,
                StandardErrorEncoding = ConsoleText.Oem,
            };
            using var p = Process.Start(psi);
            if (p == null) return $"> {file} {args}\nНе удалось запустить команду.\n";

            string outp = p.StandardOutput.ReadToEnd();
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(60000);

            var sb = new StringBuilder();
            sb.AppendLine($"> {file} {args}");
            if (!string.IsNullOrWhiteSpace(outp)) sb.AppendLine(outp.Trim());
            if (!string.IsNullOrWhiteSpace(err)) sb.AppendLine(err.Trim());
            sb.AppendLine($"[код завершения: {p.ExitCode}]");
            return sb.ToString();
        }
        catch (Exception ex)
        {
            return $"> {file} {args}\nОшибка: {ex.Message}\n";
        }
    }

    public static string FlushDns() => Run("ipconfig", "/flushdns");

    public static string ReleaseRenew()
        => Run("ipconfig", "/release") + "\n" + Run("ipconfig", "/renew");

    public static string ResetWinsock() => Run("netsh", "winsock reset");

    public static string ResetTcpIp() => Run("netsh", "int ip reset");

    public static string ResetFirewall() => Run("netsh", "advfirewall reset");

    /// <summary>The full sequence — the classic "fix my network" combo.</summary>
    public static string FullReset()
    {
        var sb = new StringBuilder();
        sb.AppendLine(FlushDns());
        sb.AppendLine(ResetWinsock());
        sb.AppendLine(ResetTcpIp());
        sb.AppendLine(ReleaseRenew());
        return sb.ToString();
    }

    public static void Reboot() => Run("shutdown", "/r /t 5 /c \"NetOptimizer: перезагрузка для применения сетевых настроек\"");
}
