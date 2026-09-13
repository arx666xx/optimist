using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using NetOptimizer.Models;

namespace NetOptimizer.Services;

public readonly record struct ActionResult(bool Ok, string Message)
{
    public static ActionResult Success(string m) => new(true, m);
    public static ActionResult Fail(string m) => new(false, m);
}

public static class ProcessActions
{
    /// <summary>
    /// Terminates a process and its children. Critical Windows processes are
    /// refused outright — killing lsass or csrss bluescreens the machine, so a
    /// confirmation dialog would be false comfort.
    /// </summary>
    public static ActionResult Kill(int pid, string? processName = null)
    {
        var verdict = ProcessGuard.Check(pid, processName, out string reason);
        if (verdict == ProcessGuard.Verdict.Blocked)
        {
            ActionLog.Warn($"Отклонено завершение PID {pid} ({processName}): {reason}");
            return ActionResult.Fail(reason);
        }

        try
        {
            using var p = Process.GetProcessById(pid);
            string name = p.ProcessName;
            p.Kill(entireProcessTree: true);
            ActionLog.Action($"Завершён процесс {name} (PID {pid}) вместе с дочерними.");
            return ActionResult.Success($"Процесс {name} (PID {pid}) завершён.");
        }
        catch (Exception ex)
        {
            ActionLog.Error($"Не удалось завершить PID {pid}", ex);
            return ActionResult.Fail($"Не удалось завершить PID {pid}: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks the guard whether this process may be touched at all, and what the
    /// user should be warned about. The UI calls this before Kill/Suspend.
    /// </summary>
    public static ProcessGuard.Verdict CanTouch(int pid, string? processName, out string reason)
        => ProcessGuard.Check(pid, processName, out reason);

    public static ActionResult Suspend(int pid) => SuspendResume(pid, suspend: true);
    public static ActionResult Resume(int pid) => SuspendResume(pid, suspend: false);

    private static ActionResult SuspendResume(int pid, bool suspend)
    {
        // Resuming is always safe; suspending a critical process hangs the system.
        if (suspend && ProcessGuard.Check(pid, null, out string blocked) == ProcessGuard.Verdict.Blocked)
        {
            ActionLog.Warn($"Отклонена приостановка PID {pid}: {blocked}");
            return ActionResult.Fail(blocked);
        }
        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_SUSPEND_RESUME, false, pid);
        if (h == IntPtr.Zero)
            return ActionResult.Fail($"Нет доступа к PID {pid} (запустите от администратора).");
        try
        {
            uint status = suspend
                ? NativeMethods.NtSuspendProcess(h)
                : NativeMethods.NtResumeProcess(h);
            if (status != 0)
                return ActionResult.Fail($"Операция не выполнена (NTSTATUS 0x{status:X}).");

            ActionLog.Action(suspend
                ? $"Приостановлен процесс PID {pid} ({ProcessGuard.NameOf(pid)})."
                : $"Возобновлён процесс PID {pid} ({ProcessGuard.NameOf(pid)}).");
            return ActionResult.Success(suspend
                ? $"Процесс PID {pid} приостановлен."
                : $"Процесс PID {pid} возобновлён.");
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    public static ActionResult SetPriority(int pid, ProcessPriorityClass priority)
    {
        if (ProcessGuard.Check(pid, null, out string blocked) == ProcessGuard.Verdict.Blocked)
            return ActionResult.Fail(blocked);

        try
        {
            using var p = Process.GetProcessById(pid);
            p.PriorityClass = priority;
            ActionLog.Action($"Приоритет {p.ProcessName} (PID {pid}) → {priority}.");
            return ActionResult.Success($"Приоритет {p.ProcessName} → {priority}.");
        }
        catch (Exception ex)
        {
            ActionLog.Error($"Не удалось изменить приоритет PID {pid}", ex);
            return ActionResult.Fail($"Не удалось изменить приоритет PID {pid}: {ex.Message}");
        }
    }

    /// <summary>
    /// Forcibly closes an IPv4 TCP connection via SetTcpEntry (DELETE_TCB).
    /// IPv6 / UDP are not supported by this API — kill the process instead.
    /// </summary>
    public static ActionResult CloseConnection(ConnectionInfo c)
    {
        if (c.Protocol != "TCP")
            return ActionResult.Fail("Закрытие поддерживается только для TCP. Для UDP завершите процесс.");
        if (c.IsIPv6)
            return ActionResult.Fail("SetTcpEntry работает только с IPv4. Для IPv6 завершите процесс.");

        if (!IPAddress.TryParse(c.LocalAddress, out var la) ||
            !IPAddress.TryParse(c.RemoteAddress, out var ra) ||
            la.AddressFamily != AddressFamily.InterNetwork ||
            ra.AddressFamily != AddressFamily.InterNetwork)
            return ActionResult.Fail("Некорректный IPv4-адрес соединения.");

        var row = new NativeMethods.MIB_TCPROW
        {
            dwState = NativeMethods.MIB_TCP_STATE_DELETE_TCB,
            dwLocalAddr = AddrToUint(la),
            dwLocalPort = NetPort(c.LocalPort),
            dwRemoteAddr = AddrToUint(ra),
            dwRemotePort = NetPort(c.RemotePort),
        };

        int r = NativeMethods.SetTcpEntry(ref row);
        if (r == 0)
        {
            ActionLog.Action($"Закрыто соединение {c.ProcessName} (PID {c.Pid}): {c.LocalEndpoint} → {c.RemoteEndpoint}.");
            return ActionResult.Success($"Соединение {c.LocalEndpoint} → {c.RemoteEndpoint} закрыто.");
        }
        if (r == 5)
            return ActionResult.Fail("Отказано в доступе. Запустите приложение от имени администратора.");
        if (r == 87)
            return ActionResult.Fail("Это соединение нельзя закрыть (не в состоянии ESTABLISHED).");
        return ActionResult.Fail($"SetTcpEntry вернул код ошибки {r}.");
    }

    private static uint AddrToUint(IPAddress a)
        => BitConverter.ToUInt32(a.GetAddressBytes(), 0);

    // Port must sit in the low 16 bits in network byte order.
    private static uint NetPort(int port)
        => (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF));
}

public static class FirewallService
{
    private const string RulePrefix = "NetOptimizer Block";

    /// <summary>Adds an outbound + inbound firewall rule blocking the given program.</summary>
    public static ActionResult BlockProgram(string? programPath)
    {
        if (string.IsNullOrWhiteSpace(programPath))
            return ActionResult.Fail("Не удалось определить путь к программе.");

        string name = $"{RulePrefix} - {System.IO.Path.GetFileName(programPath)}";
        var outRes = Netsh($"advfirewall firewall add rule name=\"{name} (out)\" dir=out action=block program=\"{programPath}\" enable=yes");
        var inRes = Netsh($"advfirewall firewall add rule name=\"{name} (in)\" dir=in action=block program=\"{programPath}\" enable=yes");

        if (outRes.Ok && inRes.Ok)
        {
            ActionLog.Action($"Заблокирована в брандмауэре программа: {programPath}");
            return ActionResult.Success($"Программа заблокирована в брандмауэре:\n{programPath}\n\nСнять блокировку можно кнопкой «Разблокировать» или в «Брандмауэре Защитника Windows».");
        }
        ActionLog.Warn($"Не удалось заблокировать {programPath}: {outRes.Message} / {inRes.Message}");
        return ActionResult.Fail($"Не удалось создать правило.\n{outRes.Message}\n{inRes.Message}");
    }

    /// <summary>Removes previously created block rules for the given program.</summary>
    public static ActionResult UnblockProgram(string? programPath)
    {
        if (string.IsNullOrWhiteSpace(programPath))
            return ActionResult.Fail("Не удалось определить путь к программе.");

        string name = $"{RulePrefix} - {System.IO.Path.GetFileName(programPath)}";
        Netsh($"advfirewall firewall delete rule name=\"{name} (out)\"");
        Netsh($"advfirewall firewall delete rule name=\"{name} (in)\"");
        ActionLog.Action($"Снята блокировка в брандмауэре: {programPath}");
        return ActionResult.Success($"Правила блокировки для {System.IO.Path.GetFileName(programPath)} удалены (если существовали).");
    }

    private static ActionResult Netsh(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = ConsoleText.Oem,
                StandardErrorEncoding = ConsoleText.Oem,
            };
            using var p = Process.Start(psi)!;
            string err = p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            return p.ExitCode == 0
                ? ActionResult.Success("ok")
                : ActionResult.Fail(string.IsNullOrWhiteSpace(err) ? $"netsh exit {p.ExitCode}" : err.Trim());
        }
        catch (Exception ex)
        {
            return ActionResult.Fail(ex.Message);
        }
    }
}
