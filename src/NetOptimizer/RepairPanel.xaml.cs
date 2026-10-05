using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NetOptimizer.Services;

namespace NetOptimizer;

/// <summary>
/// "Ремонт сети" page, hosted in the main window's slide-in panel. The reboot
/// countdown that used to pop up as its own window is a banner here.
/// </summary>
public partial class RepairPanel : UserControl
{
    private readonly DispatcherTimer _rebootTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remaining;

    /// <summary>Raised when the panel is closed while an automatic reboot was counting down.</summary>
    public event Action? RebootCancelledByClose;

    public RepairPanel()
    {
        InitializeComponent();
        _rebootTimer.Tick += (_, _) => RebootTick();

        // Closing the panel must never leave an invisible countdown running —
        // the PC would restart with nothing on screen saying why.
        Unloaded += (_, _) =>
        {
            if (!_rebootTimer.IsEnabled) return;
            _rebootTimer.Stop();
            RebootCancelledByClose?.Invoke();
        };
    }

    private async void RunAction(string confirmText, bool needsReboot, Func<string> action)
    {
        if (!string.IsNullOrEmpty(confirmText))
        {
            var res = MessageBox.Show(confirmText, "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;
        }

        SetBusy(true);
        Hint.Text = "Выполняется…";
        string output = await Task.Run(action);

        LogBox.AppendText(output.TrimEnd() + "\n\n");
        LogBox.ScrollToEnd();

        SetBusy(false);
        Hint.Text = needsReboot
            ? "Готово. Требуется перезагрузка."
            : "Готово.";

        if (needsReboot) StartReboot(SettingsService.LoadRebootDelaySeconds());
    }

    private void SetBusy(bool busy)
    {
        BtnSteamFix.IsEnabled = !busy;
        BtnFlush.IsEnabled = !busy;
        BtnRenew.IsEnabled = !busy;
        BtnWinsock.IsEnabled = !busy;
        BtnTcpip.IsEnabled = !busy;
        BtnFirewall.IsEnabled = !busy;
        BtnFull.IsEnabled = !busy;
    }

    // ---------------- Reboot banner ----------------

    private void StartReboot(int delaySeconds)
    {
        RebootBanner.Visibility = Visibility.Visible;
        _remaining = delaySeconds;

        if (delaySeconds <= 0)
        {
            RebootTitle.Text = "Требуется перезагрузка";
            RebootText.Text = "Чтобы изменения вступили в силу, перезагрузите компьютер.";
            return;
        }

        RebootTitle.Text = "Автоматическая перезагрузка";
        UpdateRebootText();
        _rebootTimer.Start();
    }

    private void RebootTick()
    {
        _remaining--;
        if (_remaining > 0)
        {
            UpdateRebootText();
            return;
        }
        _rebootTimer.Stop();
        NetworkRepair.Reboot();
    }

    private void UpdateRebootText()
    {
        var t = TimeSpan.FromSeconds(_remaining);
        RebootText.Text = $"Компьютер перезагрузится через {t:mm\\:ss}. Сохраните открытые файлы или нажмите «Отмена».";
    }

    private void RebootNow_Click(object sender, RoutedEventArgs e)
    {
        _rebootTimer.Stop();
        NetworkRepair.Reboot();
    }

    private void RebootCancel_Click(object sender, RoutedEventArgs e)
    {
        _rebootTimer.Stop();
        RebootBanner.Visibility = Visibility.Collapsed;
        Hint.Text = "Перезагрузка отменена. Изменения вступят в силу после следующей перезагрузки.";
    }

    // ---------------- Actions ----------------

    private void SteamFix_Click(object sender, RoutedEventArgs e)
        => RunAction(
            "Сбросить каталог Winsock?\n\nЭто чинит ситуацию, когда интернет работает, но игры/Steam выдают ошибки после VPN. Потребуется перезагрузка.",
            needsReboot: true, NetworkRepair.ResetWinsock);

    private void Flush_Click(object sender, RoutedEventArgs e)
        => RunAction("", needsReboot: false, NetworkRepair.FlushDns);

    private void Renew_Click(object sender, RoutedEventArgs e)
        => RunAction(
            "Обновить IP-адрес? Соединение прервётся на несколько секунд.",
            needsReboot: false, NetworkRepair.ReleaseRenew);

    private void Winsock_Click(object sender, RoutedEventArgs e)
        => RunAction("Сбросить каталог Winsock? Потребуется перезагрузка.",
            needsReboot: true, NetworkRepair.ResetWinsock);

    private void TcpIp_Click(object sender, RoutedEventArgs e)
        => RunAction("Сбросить стек TCP/IP? Потребуется перезагрузка.",
            needsReboot: true, NetworkRepair.ResetTcpIp);

    private void Firewall_Click(object sender, RoutedEventArgs e)
        => RunAction(
            "Сбросить брандмауэр Windows к настройкам по умолчанию?\n\nВнимание: это удалит все пользовательские правила, включая созданные этим приложением блокировки.",
            needsReboot: false, NetworkRepair.ResetFirewall);

    private void Full_Click(object sender, RoutedEventArgs e)
        => RunAction(
            "Выполнить ПОЛНЫЙ сброс сети?\n\nБудут выполнены: очистка DNS, сброс Winsock, сброс TCP/IP, обновление IP. Соединение ненадолго прервётся, потребуется перезагрузка.",
            needsReboot: true, NetworkRepair.FullReset);

    private void Reboot_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show("Перезагрузить компьютер сейчас? Сохраните открытые файлы.",
            "Перезагрузка", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res == MessageBoxResult.Yes)
            NetworkRepair.Reboot();
    }
}
