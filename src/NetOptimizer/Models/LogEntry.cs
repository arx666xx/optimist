using System.Text.RegularExpressions;

namespace NetOptimizer.Models;

public class LogEntry
{
    public DateTime Time { get; init; }
    public string Level { get; init; } = "";
    public string Source { get; init; } = "";
    public long EventId { get; init; }

    /// <summary>Full event text, line breaks kept, for the details panel.</summary>
    public string Message { get; init; } = "";

    // Full date AND time so problems can be pinpointed.
    public string TimeText => Time.ToString("dd.MM.yyyy  HH:mm:ss");

    /// <summary>
    /// What the table shows: a known event explained in plain words, otherwise
    /// the first sentence of the message instead of a 400-character wall.
    /// </summary>
    public string Summary
    {
        get
        {
            string? hint = Hint;
            if (hint != null)
            {
                // "Служба не смогла запуститься" is useless without saying which one.
                var m = Quoted.Match(Message);
                return m.Success ? $"{hint}: {m.Groups[1].Value}" : hint;
            }

            string first = Message.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                  .Select(l => l.Trim())
                                  .FirstOrDefault(l => l.Length > 0) ?? "";
            return first.Length > 160 ? first.Substring(0, 160) + "…" : first;
        }
    }

    /// <summary>Plain-language meaning of the events people actually go looking for.</summary>
    public string? Hint => Find()?.Text;

    /// <summary>Known noise Windows logs all day long; hidden by default.</summary>
    public bool Harmless => Find()?.Harmless == true;

    private Known? Find()
    {
        foreach (var k in KnownEvents)
            if (k.Id == EventId && Source.Contains(k.Source, StringComparison.OrdinalIgnoreCase))
                return k;
        return null;
    }

    /// <summary>First short name in quotes: «Служба "Windows Update"…» → Windows Update.</summary>
    private static readonly Regex Quoted = new("[\"«“]([^\"»”\r\n]{2,60})[\"»”]", RegexOptions.Compiled);

    private sealed record Known(string Source, long Id, string Text, bool Harmless = false);

    // Matched by a fragment of the source name: the classic EventLog API reports
    // "DCOM" where Event Viewer shows "Microsoft-Windows-DistributedCOM".
    private static readonly Known[] KnownEvents =
    {
        new("Kernel-Power", 41, "Компьютер выключился аварийно (пропало питание, зависание или синий экран)"),
        new("EventLog", 6008, "Предыдущее выключение было неожиданным"),
        new("EventLog", 6005, "Windows запущена"),
        new("EventLog", 6006, "Windows корректно завершила работу"),
        new("User32", 1074, "Перезагрузку или выключение запустила программа или пользователь"),
        new("WER-SystemErrorReporting", 1001, "Был синий экран (BSOD) — подробности ниже"),
        new("BugCheck", 1001, "Был синий экран (BSOD) — подробности ниже"),
        new("Application Error", 1000, "Программа аварийно закрылась"),
        new("Application Hang", 1002, "Программа зависла и была закрыта"),
        new("Windows Error Reporting", 1001, "Windows собрала отчёт об ошибке программы"),
        new("Service Control Manager", 7031, "Служба неожиданно завершилась и будет перезапущена"),
        new("Service Control Manager", 7034, "Служба неожиданно завершилась"),
        new("Service Control Manager", 7000, "Служба не смогла запуститься"),
        new("Service Control Manager", 7009, "Служба не ответила вовремя при запуске"),
        new("Service Control Manager", 7023, "Служба завершилась с ошибкой"),
        new("DCOM", 10016, "Ошибка прав DCOM — безвредна, Windows пишет её постоянно", Harmless: true),
        new("DCOM", 10010, "Компонент Windows не ответил вовремя — обычно безвредно", Harmless: true),
        new("Security-Auditing", 4625, "Неудачная попытка входа в систему"),
        new("Security-Auditing", 4624, "Успешный вход в систему"),
        new("Security-Auditing", 4740, "Учётная запись заблокирована после неудачных входов"),
        new("FilterManager", 11, "Старый драйвер-фильтр диска — безвредно", Harmless: true),
        new("Tcpip", 4199, "В сети обнаружен конфликт IP-адресов"),
        new("DNS-Client", 1014, "Не удалось получить ответ DNS — сайт или сервер не открывался"),
        new("Dhcp-Client", 1001, "Не удалось получить IP-адрес от роутера"),
        new("disk", 7, "Повреждённый участок на диске — стоит проверить диск"),
        new("disk", 51, "Ошибка при обращении к диску"),
        new("Ntfs", 55, "Повреждение файловой системы — запустите проверку диска"),
        new("volmgr", 161, "Не удалось сохранить дамп памяти при сбое"),
        new("WindowsUpdateClient", 20, "Не удалось установить обновление Windows"),
        new("WindowsUpdateClient", 19, "Обновление Windows установлено"),
    };
}
