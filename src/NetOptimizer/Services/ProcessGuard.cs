using System.Diagnostics;

namespace NetOptimizer.Services;

/// <summary>
/// Single source of truth for "do not touch this process".
///
/// Windows has no protection against a process running as administrator killing
/// lsass or csrss — it simply bluescreens the machine (CRITICAL_PROCESS_DIED).
/// A confirmation dialog is not protection here: people click "yes". So the
/// hard list is refused outright, and only the softer list asks.
/// </summary>
public static class ProcessGuard
{
    /// <summary>Killing or suspending these takes the whole machine down. Never allowed.</summary>
    private static readonly HashSet<string> Critical = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "System Idle", "Idle", "Registry", "Memory Compression", "Secure System",
        "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "fontdrvhost", "dwm", "sihost", "WUDFHost", "audiodg",
        "NetOptimizer",
    };

    /// <summary>
    /// Important but survivable — Windows restarts them or the user only loses the
    /// desktop shell. Allowed with a warning.
    /// </summary>
    private static readonly HashSet<string> Sensitive = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "svchost", "spoolsv", "conhost", "RuntimeBroker", "dllhost",
        "taskhostw", "ctfmon", "SearchIndexer", "SearchHost", "ShellExperienceHost",
        "StartMenuExperienceHost", "WmiPrvSE", "TextInputHost", "SecurityHealthService",
        "MsMpEng", "NisSrv",
    };

    public enum Verdict { Allowed, NeedsConfirmation, Blocked }

    public static Verdict Check(int pid, string? processName, out string reason)
    {
        if (pid <= 4)
        {
            reason = "Это системный процесс ядра Windows — трогать его нельзя.";
            return Verdict.Blocked;
        }

        string name = processName ?? NameOf(pid) ?? "";

        if (name.Length > 0 && Critical.Contains(name))
        {
            reason = name.Equals("NetOptimizer", StringComparison.OrdinalIgnoreCase)
                ? "Это сама программа NetOptimizer."
                : $"«{name}» — критичный процесс Windows. Его завершение немедленно уронит систему в синий экран, поэтому действие заблокировано.";
            return Verdict.Blocked;
        }

        if (name.Length > 0 && Sensitive.Contains(name))
        {
            reason = $"«{name}» — служебный процесс Windows. Возможны последствия: пропадёт панель задач, звук или часть служб. Продолжить?";
            return Verdict.NeedsConfirmation;
        }

        reason = "";
        return Verdict.Allowed;
    }

    /// <summary>True for processes that background-optimisation passes must skip.</summary>
    public static bool IsProtected(string? processName) =>
        !string.IsNullOrEmpty(processName) &&
        (Critical.Contains(processName) || Sensitive.Contains(processName));

    public static string? NameOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch { return null; }
    }
}
