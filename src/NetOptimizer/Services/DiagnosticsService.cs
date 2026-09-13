using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetOptimizer.Services;

public enum DiagStatus { Running, Ok, Warning, Failed, Skipped }

/// <summary>
/// One line of the diagnostic ladder. Rows appear as "…" the moment a check
/// starts and fill in when it finishes, so notification is needed.
/// </summary>
public sealed class DiagStep : INotifyPropertyChanged
{
    public string Name { get; init; } = "";

    private DiagStatus _status = DiagStatus.Running;
    public DiagStatus Status
    {
        get => _status;
        set { if (_status != value) { _status = value; OnChanged(); OnChanged(nameof(Icon)); } }
    }

    private string _detail = "";
    public string Detail
    {
        get => _detail;
        set { if (_detail != value) { _detail = value; OnChanged(); } }
    }

    private string _advice = "";
    public string Advice
    {
        get => _advice;
        set { if (_advice != value) { _advice = value; OnChanged(); } }
    }

    public string Icon => Status switch
    {
        DiagStatus.Ok => "✓",
        DiagStatus.Warning => "!",
        DiagStatus.Failed => "✕",
        DiagStatus.Skipped => "–",
        _ => "…"
    };

    public override string ToString() =>
        $"{Icon}  {Name}: {Detail}" + (Advice.Length > 0 ? $"\n     → {Advice}" : "");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>
/// Answers "почему не работает интернет" by walking outwards from the machine:
/// adapter → gateway → public IP → DNS → HTTPS. The first step that fails names
/// the culprit, which is what a person actually wants to know.
///
/// Then it checks the specific faults that VPN clients leave behind and that are
/// painful to find by hand: a wrong MTU, a stale system proxy, broken IPv6, and
/// left-over virtual adapters.
/// </summary>
public static class DiagnosticsService
{
    private const string ProbeHostV4 = "1.1.1.1";
    private const string ProbeHostV6 = "2606:4700:4700::1111";
    private const string ProbeDomain = "www.msftconnecttest.com";
    private const string ProbeUrl = "http://www.msftconnecttest.com/connecttest.txt";

    public static async Task<List<DiagStep>> RunAsync(IProgress<DiagStep>? progress = null,
        CancellationToken ct = default)
    {
        var steps = new List<DiagStep>();

        var iface = await Report(steps, progress, "Сетевой адаптер", CheckAdapter, ct);
        var gateway = GetGateway();

        await Report(steps, progress, "Шлюз (роутер)", () => CheckGateway(gateway), ct);
        await Report(steps, progress, $"Внешний узел {ProbeHostV4}", () => CheckPing(ProbeHostV4), ct);
        await Report(steps, progress, "DNS-серверы", CheckDnsServers, ct);
        await Report(steps, progress, "Разрешение имён", CheckResolve, ct);
        await Report(steps, progress, "HTTPS-соединение", CheckHttp, ct);
        await Report(steps, progress, "Размер пакета (MTU)", () => CheckMtu(gateway), ct);
        await Report(steps, progress, "Системный прокси", CheckProxy, ct);
        await Report(steps, progress, "IPv6", CheckIpv6, ct);
        await Report(steps, progress, "Виртуальные адаптеры (VPN)", CheckVirtualAdapters, ct);

        _ = iface;
        return steps;
    }

    private static async Task<DiagStep> Report(List<DiagStep> steps, IProgress<DiagStep>? progress,
        string name, Func<DiagStep> check, CancellationToken ct)
    {
        var running = new DiagStep { Name = name, Status = DiagStatus.Running, Detail = "проверка…" };
        steps.Add(running);
        progress?.Report(running);

        DiagStep result;
        try
        {
            result = await Task.Run(check, ct);
        }
        catch (Exception ex)
        {
            result = new DiagStep { Name = name, Status = DiagStatus.Failed, Detail = ex.Message };
        }

        running.Status = result.Status;
        running.Detail = result.Detail;
        running.Advice = result.Advice;
        progress?.Report(running);
        return running;
    }

    private static DiagStep Ok(string detail) => new() { Status = DiagStatus.Ok, Detail = detail };
    private static DiagStep Warn(string detail, string advice = "") =>
        new() { Status = DiagStatus.Warning, Detail = detail, Advice = advice };
    private static DiagStep Bad(string detail, string advice = "") =>
        new() { Status = DiagStatus.Failed, Detail = detail, Advice = advice };
    private static DiagStep Skip(string detail) => new() { Status = DiagStatus.Skipped, Detail = detail };

    // ---------------- Steps ----------------

    private static DiagStep CheckAdapter()
    {
        var up = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                     && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .ToList();

        if (up.Count == 0)
            return Bad("нет активных сетевых адаптеров",
                "Проверьте кабель или подключение к Wi-Fi — компьютер вообще не подключён к сети.");

        var main = up.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Count > 0) ?? up[0];
        var ip = main.GetIPProperties().UnicastAddresses
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address;

        string speed = main.Speed > 0 ? $", {main.Speed / 1_000_000} Мбит/с" : "";
        return Ok($"{main.Name} — {main.Description}, IP {ip?.ToString() ?? "нет IPv4"}{speed}");
    }

    private static IPAddress? GetGateway() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .Select(g => g.Address)
            .FirstOrDefault(a => a != null && a.AddressFamily == AddressFamily.InterNetwork
                              && !a.Equals(IPAddress.Any));

    private static DiagStep CheckGateway(IPAddress? gateway)
    {
        if (gateway == null)
            return Bad("шлюз не назначен",
                "Компьютер не получил адрес маршрутизатора. Помогает «Обновить IP-адрес» на вкладке ремонта.");

        var r = Measure(gateway.ToString(), 5);
        if (r.received == 0)
            return Bad($"{gateway} не отвечает",
                "Роутер не отвечает — проблема внутри вашей сети: кабель, Wi-Fi или сам роутер.");

        string detail = $"{gateway} — {r.avg:0} мс, потери {r.loss:0}%, джиттер {r.jitter:0} мс";
        if (r.loss >= 5)
            return Warn(detail, "Потери до роутера означают плохой Wi-Fi или повреждённый кабель, а не проблему провайдера.");
        if (r.jitter > 15)
            return Warn(detail, "Высокий джиттер до роутера — обычно перегруженный Wi-Fi. В играх это ощущается как рывки.");
        return Ok(detail);
    }

    private static DiagStep CheckPing(string host)
    {
        var r = Measure(host, 5);
        if (r.received == 0)
            return Bad($"{host} не отвечает",
                "Роутер отвечает, а внешний узел — нет. Проблема у провайдера или в настройках маршрутизатора.");

        string detail = $"{r.avg:0} мс, потери {r.loss:0}%, джиттер {r.jitter:0} мс";
        if (r.loss >= 5)
            return Warn(detail, "Потери до внешнего узла при живом роутере — почти всегда сторона провайдера.");
        if (r.avg > 120)
            return Warn(detail, "Высокая задержка до внешнего узла. Проверьте, не включён ли VPN или прокси.");
        return Ok(detail);
    }

    private static DiagStep CheckDnsServers()
    {
        var servers = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().DnsAddresses)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .ToList();

        if (servers.Count == 0)
            return Bad("DNS-серверы не назначены",
                "Без DNS сайты не открываются по именам. Помогает «Обновить IP-адрес» или ручная установка 1.1.1.1.");

        var alive = new List<string>();
        var dead = new List<string>();
        foreach (var s in servers)
        {
            var r = Measure(s.ToString(), 2);
            if (r.received > 0) alive.Add($"{s} ({r.avg:0} мс)"); else dead.Add(s.ToString());
        }

        if (alive.Count == 0)
            return Bad($"ни один DNS-сервер не отвечает: {string.Join(", ", dead)}",
                "Пропишите публичный DNS 1.1.1.1 или 8.8.8.8 в свойствах адаптера.");
        if (dead.Count > 0)
            return Warn($"отвечают: {string.Join(", ", alive)}; молчат: {string.Join(", ", dead)}",
                "Неотвечающий DNS в списке добавляет задержку к каждому запросу — уберите его из настроек адаптера.");

        return Ok(string.Join(", ", alive));
    }

    private static DiagStep CheckResolve()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var addr = Dns.GetHostAddresses(ProbeDomain);
            sw.Stop();
            if (addr.Length == 0)
                return Bad("имя не разрешилось", "DNS не возвращает адреса — смените DNS-сервер на 1.1.1.1.");

            string detail = $"{ProbeDomain} → {addr[0]} за {sw.ElapsedMilliseconds} мс";
            if (sw.ElapsedMilliseconds > 300)
                return Warn(detail, "Медленный DNS заметно тормозит открытие сайтов. Публичный 1.1.1.1 обычно быстрее.");
            return Ok(detail);
        }
        catch (Exception ex)
        {
            return Bad("имена не разрешаются: " + ex.Message,
                "Внешние адреса пингуются, а имена не резолвятся — виноват DNS. Очистите кэш DNS на вкладке ремонта или смените сервер.");
        }
    }

    private static DiagStep CheckHttp()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var sw = Stopwatch.StartNew();
            var resp = http.GetAsync(ProbeUrl).GetAwaiter().GetResult();
            string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            sw.Stop();

            if (!resp.IsSuccessStatusCode)
                return Warn($"сервер ответил {(int)resp.StatusCode}",
                    "Соединение проходит, но ответ неожиданный — возможен перехват трафика прокси или фильтром провайдера.");

            if (!body.Contains("Microsoft Connect Test", StringComparison.OrdinalIgnoreCase))
                return Warn("ответ подменён",
                    "Ответ не тот, что должен быть. Обычно это captive portal (страница входа в Wi-Fi) или фильтрующий прокси.");

            return Ok($"ответ получен за {sw.ElapsedMilliseconds} мс");
        }
        catch (Exception ex)
        {
            return Bad("запрос не прошёл: " + ex.Message,
                "Имена разрешаются, а соединение не устанавливается — проверьте брандмауэр и антивирус.");
        }
    }

    /// <summary>
    /// Finds the largest packet that survives without fragmentation. A VPN that
    /// exited badly often leaves an MTU below 1500, and the symptom is maddening:
    /// light pages open, heavy ones hang forever.
    /// </summary>
    private static DiagStep CheckMtu(IPAddress? gateway)
    {
        string target = gateway?.ToString() ?? ProbeHostV4;

        int low = 1200, high = 1472, best = 0; // payload bytes; MTU = payload + 28
        if (!ProbeMtu(target, low)) return Skip("узел не отвечает на пакеты с запретом фрагментации");

        best = low;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (ProbeMtu(target, mid)) { best = mid; low = mid + 1; }
            else high = mid - 1;
        }

        int mtu = best + 28;
        string detail = $"{mtu} байт (проверено до {target})";

        if (mtu >= 1500) return Ok(detail);
        if (mtu >= 1492) return Ok(detail + " — норма для PPPoE");
        return Warn(detail,
            "MTU занижен — типичный след неудачно отключённого VPN. Симптом: лёгкие страницы открываются, тяжёлые виснут. " +
            "Лечится сбросом TCP/IP на вкладке ремонта.");
    }

    private static bool ProbeMtu(string host, int payload)
    {
        try
        {
            using var ping = new Ping();
            var reply = ping.Send(host, 1500, new byte[payload], new PingOptions(64, true));
            return reply.Status == IPStatus.Success;
        }
        catch { return false; }
    }

    private static DiagStep CheckProxy()
    {
        try
        {
            var proxy = HttpClient.DefaultProxy;
            var target = new Uri("https://www.microsoft.com");
            var via = proxy?.GetProxy(target);

            if (via == null || via.Equals(target))
                return Ok("не используется");

            return Warn($"весь трафик идёт через {via}",
                "Если вы не настраивали прокси сами — его прописал VPN или рекламное ПО и не убрал за собой. " +
                "Снимается в «Параметры → Сеть и Интернет → Прокси».");
        }
        catch
        {
            return Skip("не удалось определить");
        }
    }

    private static DiagStep CheckIpv6()
    {
        bool configured = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Any(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6
                   && !a.Address.IsIPv6LinkLocal);

        if (!configured) return Skip("не настроен — это нормально");

        var r = Measure(ProbeHostV6, 2);
        if (r.received > 0) return Ok($"работает, {r.avg:0} мс");

        return Warn("адрес выдан, но внешние узлы недоступны",
            "Windows пробует IPv6 первым и ждёт таймаут перед откатом на IPv4 — это добавляет задержку к каждому " +
            "новому соединению. Если провайдер IPv6 не даёт, его стоит отключить в свойствах адаптера.");
    }

    private static DiagStep CheckVirtualAdapters()
    {
        string[] markers = { "tap", "tun", "vpn", "wireguard", "wintun", "openvpn", "nordlynx", "proton", "hamachi" };

        var found = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Where(n => markers.Any(m => n.Description.Contains(m, StringComparison.OrdinalIgnoreCase)
                                      || n.Name.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .Select(n => n.Name)
            .ToList();

        if (found.Count == 0) return Ok("посторонних активных адаптеров нет");

        return Warn($"активны: {string.Join(", ", found)}",
            "Это адаптеры VPN. Если VPN выключен, а адаптер активен — он может перехватывать маршруты. " +
            "Отключите его в «Сетевых подключениях».");
    }

    // ---------------- Ping helper ----------------

    private static (int received, double avg, double loss, double jitter) Measure(string host, int count)
    {
        var times = new List<long>();
        try
        {
            using var ping = new Ping();
            for (int i = 0; i < count; i++)
            {
                try
                {
                    var reply = ping.Send(host, 1200);
                    if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
                }
                catch { /* one lost probe */ }
                Thread.Sleep(60);
            }
        }
        catch { /* host unusable */ }

        if (times.Count == 0) return (0, 0, 100, 0);

        double avg = times.Average();
        double loss = 100.0 * (count - times.Count) / count;

        double jitter = 0;
        for (int i = 1; i < times.Count; i++) jitter += Math.Abs(times[i] - times[i - 1]);
        if (times.Count > 1) jitter /= times.Count - 1;

        return (times.Count, avg, loss, jitter);
    }

    /// <summary>One-line verdict for the whole run, shown above the list.</summary>
    public static string Verdict(IEnumerable<DiagStep> steps)
    {
        var list = steps.ToList();
        var failed = list.FirstOrDefault(s => s.Status == DiagStatus.Failed);
        if (failed != null)
            return $"Проблема найдена: {failed.Name.ToLowerInvariant()} — {failed.Detail}";

        int warnings = list.Count(s => s.Status == DiagStatus.Warning);
        if (warnings > 0)
            return $"Сеть работает, но есть замечания ({warnings}). Разверните строки с «!».";

        return "Сеть исправна: все проверки пройдены.";
    }
}
