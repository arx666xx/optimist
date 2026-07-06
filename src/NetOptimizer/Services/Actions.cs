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
    public static ActionResult Kill(int pid)
    {
        if (pid <= 4) return ActionResult.Fail("Системный процесс завершить нельзя.");
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return ActionResult.Success($"Процесс {p.ProcessName} (PID {pid}) завершён.");
        }
        catch (Exception ex)
        {
            return ActionResult.Fail($"Не удалось завершить PID {pid}: {ex.Message}");
        }
    }

    public static ActionResult Suspend(int pid) => SuspendResume(pid, suspend: true);
    public static ActionResult Resume(int pid) => SuspendResume(pid, suspend: false);

    private static ActionResult SuspendResume(int pid, bool suspend)
    {
        if (pid <= 4) return ActionResult.Fail("Системный процесс трогать нельзя.");
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
        try
        {
            using var p = Process.GetProcessById(pid);
            p.PriorityClass = priority;
            return ActionResult.Success($"Приоритет {p.ProcessName} → {priority}.");
        }
        catch (Exception ex)
        {
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
            return ActionResult.Success($"Соединение {c.LocalEndpoint} → {c.RemoteEndpoint} закрыто.");
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
            return ActionResult.Success($"Программа заблокирована в брандмауэре:\n{programPath}\n\nСнять блокировку можно кнопкой «Разблокировать» или в «Брандмауэре Защитника Windows».");
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
