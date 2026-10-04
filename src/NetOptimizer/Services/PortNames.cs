namespace NetOptimizer.Services;

/// <summary>
/// Turns a remote port number into words. "443" means nothing to most people;
/// "Веб (HTTPS)" does.
/// </summary>
public static class PortNames
{
    private static readonly Dictionary<int, string> Known = new()
    {
        [20] = "Передача файлов (FTP)",
        [21] = "Передача файлов (FTP)",
        [22] = "Удалённый доступ (SSH)",
        [25] = "Почта (SMTP)",
        [53] = "DNS",
        [80] = "Веб (HTTP)",
        [110] = "Почта (POP3)",
        [123] = "Синхронизация времени",
        [143] = "Почта (IMAP)",
        [443] = "Веб (HTTPS)",
        [445] = "Общие папки Windows",
        [465] = "Почта (SMTP)",
        [587] = "Почта (SMTP)",
        [853] = "Защищённый DNS",
        [993] = "Почта (IMAP)",
        [995] = "Почта (POP3)",
        [1194] = "VPN (OpenVPN)",
        [1883] = "Умный дом (MQTT)",
        [3074] = "Игры Xbox Live",
        [3389] = "Удалённый рабочий стол",
        [3478] = "Голос и видео (STUN)",
        [5222] = "Мессенджер (XMPP)",
        [5228] = "Уведомления Google",
        [5223] = "Уведомления Apple",
        [5938] = "TeamViewer",
        [7680] = "Доставка обновлений Windows",
        [8080] = "Веб (прокси)",
        [8443] = "Веб (HTTPS)",
        [27015] = "Игры Steam",
        [27036] = "Steam (локальная сеть)",
        [51820] = "VPN (WireGuard)",
    };

    public static string Describe(int port)
    {
        if (Known.TryGetValue(port, out var name)) return name;
        if (port is >= 27000 and <= 27100) return "Игры Steam";
        if (port is >= 50000 and <= 65535) return "Прямое соединение (звонки, игры, P2P)";
        return $"Порт {port}";
    }
}
