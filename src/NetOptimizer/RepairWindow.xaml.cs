using System.Windows;
using NetOptimizer.Services;

namespace NetOptimizer;

public partial class RepairWindow : Window
{
    public RepairWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeHelper.EnableDarkTitleBar(this);
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
            ? "Готово. Для применения изменений перезагрузите компьютер."
            : "Готово.";

        if (needsReboot)
            MessageBox.Show("Операция выполнена.\n\nЧтобы изменения вступили в силу, перезагрузите компьютер.",
                "NetOptimizer", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
