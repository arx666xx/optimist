using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NetOptimizer.Models;
using NetOptimizer.Services;

namespace NetOptimizer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ConnectionInfo> _items = new();
    private readonly ObservableCollection<LogEntry> _logItems = new();
    private ICollectionView _view = null!;
    private ICollectionView _logView = null!;
    private readonly DispatcherTimer _timer;
    private readonly TrafficMonitor _traffic = new();
    private UpdateService.UpdateInfo? _pendingUpdate;
    private string _logName = "System";
    private bool _logLoaded;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();

        ConnGrid.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterPredicate;

        LogGrid.ItemsSource = _logItems;
        _logView = CollectionViewSource.GetDefaultView(_logItems);
        _logView.Filter = LogFilter;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();

        Loaded += (_, _) =>
        {
            _traffic.Start();
            if (!_traffic.Available)
                StatusText.Text = "Мониторинг трафика недоступен (запустите от администратора).";
            Refresh();
            _timer.Start();
            _ = CheckForUpdatesAsync(silent: true);
        };

        Closed += (_, _) => _traffic.Dispose();
        SourceInitialized += (_, _) => ThemeHelper.SetTitleBar(this, ThemeService.Current == ThemeService.Dark);
    }

    private bool _settingsOpen;

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsOpen) CloseSettings();
        else OpenSettings();
    }

    private void OpenSettings()
    {
        UpdateThemeButtons();
        UpdateTimerButtons();
        SettingsOverlay.Visibility = Visibility.Visible;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(-340, 0, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        SettingsSlide.BeginAnimation(TranslateTransform.XProperty, anim);
        _settingsOpen = true;
    }

    private void CloseSettings()
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(0, -340, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
        };
        anim.Completed += (_, _) => SettingsOverlay.Visibility = Visibility.Collapsed;
        SettingsSlide.BeginAnimation(TranslateTransform.XProperty, anim);
        _settingsOpen = false;
    }

    private void CloseSettings_Click(object sender, RoutedEventArgs e) => CloseSettings();
    private void SettingsScrim_Click(object sender, MouseButtonEventArgs e) => CloseSettings();

    private void UpdateThemeButtons()
    {
        bool dark = ThemeService.Current == ThemeService.Dark;
        BtnDark.Tag = dark ? "active" : "inactive";
        BtnLight.Tag = dark ? "inactive" : "active";
    }

    private void Dark_Click(object sender, RoutedEventArgs e) => SetTheme(ThemeService.Dark);
    private void Light_Click(object sender, RoutedEventArgs e) => SetTheme(ThemeService.Light);

    private void SetTheme(string theme)
    {
        ThemeService.Apply(theme);
        SettingsService.SaveTheme(theme);
        UpdateThemeButtons();
    }

    private void Timer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.CommandParameter is string s && int.TryParse(s, out var sec))
        {
            SettingsService.SaveRebootDelaySeconds(sec);
            UpdateTimerButtons();
        }
    }

    private void UpdateTimerButtons()
    {
        int cur = SettingsService.LoadRebootDelaySeconds();
        foreach (var child in TimerPanel.Children)
            if (child is Button b && b.CommandParameter is string s && int.TryParse(s, out var sec))
                b.Tag = sec == cur ? "active" : "inactive";
    }

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            "Удалить NetOptimizer с компьютера?\n\n" +
            "Будут удалены файл программы, вспомогательные файлы и настройки. " +
            "Приложение закроется. Действие необратимо.",
            "Удаление программы", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (res != MessageBoxResult.Yes) return;

        if (MessageBox.Show("Точно удалить? Отменить будет нельзя.", "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            SettingsService.Uninstall();
    }

    // Right-click selects the row under the cursor so context-menu actions have a target.
    private void ConnGrid_RightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? dep = e.OriginalSource as DependencyObject;
        while (dep != null && dep is not DataGridRow)
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is DataGridRow row && !row.IsSelected)
            ConnGrid.SelectedItem = row.Item;
    }

    // ---------------- Refresh ----------------

    private void Refresh()
    {
        if (_busy) return;
        _busy = true;
        StatusText.Text = "Обновление…";

        Task.Run(() =>
        {
            try { return NetstatService.GetConnections(); }
            catch { return new List<ConnectionInfo>(); }
        }).ContinueWith(t =>
        {
            Reconcile(t.Result);
            ApplyRates();
            _busy = false;
            _view.Refresh();
            StatusText.Text = $"Обновлено: {DateTime.Now:HH:mm:ss}";
            CountText.Text = $"Показано: {_view.Cast<object>().Count()} из {_items.Count}";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ApplyRates()
    {
        if (!_traffic.Available) return;
        foreach (var c in _items)
        {
            var (down, up) = _traffic.GetRate(c.Pid);
            c.DownloadRate = down;
            c.UploadRate = up;
        }
    }

    private void Reconcile(List<ConnectionInfo> incoming)
    {
        // Build lookup tolerant of duplicate keys (last wins) — ToDictionary would throw.
        var existing = new Dictionary<string, ConnectionInfo>();
        foreach (var it in _items)
            existing[it.Key] = it;

        var seen = new HashSet<string>();

        foreach (var c in incoming)
        {
            if (!seen.Add(c.Key)) continue; // ignore duplicate rows in this batch

            if (existing.TryGetValue(c.Key, out var e))
            {
                e.State = c.State;           // update mutable fields in place
                e.Suspicious = c.Suspicious; // (keeps selection + resolved DNS)
            }
            else
            {
                _items.Add(c);
                existing[c.Key] = c;
            }
        }

        for (int i = _items.Count - 1; i >= 0; i--)
            if (!seen.Contains(_items[i].Key))
                _items.RemoveAt(i);
    }

    // ---------------- Filtering ----------------

    private bool FilterPredicate(object obj)
    {
        if (obj is not ConnectionInfo c) return false;

        if (HideLoopback.IsChecked == true &&
            (c.LocalAddress is "127.0.0.1" or "::1" || c.RemoteAddress is "127.0.0.1" or "::1"))
            return false;

        if (OnlyActive.IsChecked == true &&
            !c.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase))
            return false;

        if (OnlySuspicious.IsChecked == true && !c.Suspicious)
            return false;

        string q = FilterBox.Text?.Trim() ?? "";
        if (q.Length == 0) return true;

        return c.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.LocalEndpoint.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.RemoteEndpoint.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.RemoteHost.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.State.Contains(q, StringComparison.OrdinalIgnoreCase)
            || c.Pid.ToString().Contains(q);
    }

    private void Filter_Changed(object sender, RoutedEventArgs e) => _view?.Refresh();

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e)
    {
        if (AutoRefresh.IsChecked == true) _timer.Start();
        else _timer.Stop();
    }

    private void GroupBy_Changed(object sender, RoutedEventArgs e)
    {
        _view.GroupDescriptions.Clear();
        if (GroupByProcess.IsChecked == true)
            _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ConnectionInfo.ProcessName)));
    }

    private void Repair_Click(object sender, RoutedEventArgs e)
    {
        var win = new RepairWindow { Owner = this };
        win.ShowDialog();
        Refresh();
    }

    private void Boost_Click(object sender, RoutedEventArgs e)
    {
        var win = new BoostWindow(_items.ToList()) { Owner = this };
        win.ShowDialog();
        Refresh();
    }

    // ---------------- View switching (tabs) ----------------

    private void ShowConnections_Click(object sender, RoutedEventArgs e) => SwitchView(showLog: false);

    private void ShowLog_Click(object sender, RoutedEventArgs e)
    {
        SwitchView(showLog: true);
        if (!_logLoaded) LoadLog();
    }

    private void SwitchView(bool showLog)
    {
        ConnectionsView.Visibility = showLog ? Visibility.Collapsed : Visibility.Visible;
        LogView.Visibility = showLog ? Visibility.Visible : Visibility.Collapsed;
        BtnViewConn.Tag = showLog ? "inactive" : "active";
        BtnViewLog.Tag = showLog ? "active" : "inactive";
    }

    // ---------------- Windows event log ----------------

    private void LogSource_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string s)
        {
            _logName = s;
            UpdateLogSourceButtons();
            LoadLog();
        }
    }

    private void UpdateLogSourceButtons()
    {
        BtnLogSystem.Tag = _logName == "System" ? "active" : "inactive";
        BtnLogApp.Tag = _logName == "Application" ? "active" : "inactive";
        BtnLogSecurity.Tag = _logName == "Security" ? "active" : "inactive";
    }

    private void RefreshLog_Click(object sender, RoutedEventArgs e) => LoadLog();
    private void LogOnlyErrors_Changed(object sender, RoutedEventArgs e) => LoadLog();
    private void LogSearch_TextChanged(object sender, TextChangedEventArgs e) => _logView?.Refresh();

    private void LoadLog()
    {
        _logLoaded = true;
        LogStatus.Text = "Чтение журнала…";
        bool onlyErrors = LogOnlyErrors.IsChecked == true;
        string logName = _logName;

        Task.Run(() => WindowsLogService.Read(logName, onlyErrors, 500))
            .ContinueWith(t =>
            {
                _logItems.Clear();
                foreach (var it in t.Result) _logItems.Add(it);
                _logView.Refresh();
                LogStatus.Text = $"Записей: {_logItems.Count} · журнал: {LogDisplayName(logName)} · обновлено {DateTime.Now:HH:mm:ss}";
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private static string LogDisplayName(string name) => name switch
    {
        "System" => "Система",
        "Application" => "Приложения",
        "Security" => "Безопасность",
        _ => name
    };

    private bool LogFilter(object obj)
    {
        if (obj is not LogEntry l) return false;
        string q = LogSearch.Text?.Trim() ?? "";
        if (q.Length == 0) return true;
        return l.Source.Contains(q, StringComparison.OrdinalIgnoreCase)
            || l.Message.Contains(q, StringComparison.OrdinalIgnoreCase)
            || l.Level.Contains(q, StringComparison.OrdinalIgnoreCase)
            || l.EventId.ToString().Contains(q);
    }

    // ---------------- Updates ----------------

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate != null)
        {
            var res = MessageBox.Show(
                $"Установить обновление до версии {_pendingUpdate.Version}?\n\n" +
                "Приложение закроется, обновится и запустится заново.",
                "Обновление", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res != MessageBoxResult.Yes) return;

            try
            {
                StatusText.Text = "Загрузка обновления…";
                BtnUpdate.IsEnabled = false;
                await UpdateService.DownloadAndApplyAsync(_pendingUpdate);
            }
            catch (Exception ex)
            {
                BtnUpdate.IsEnabled = true;
                MessageBox.Show("Не удалось обновиться: " + ex.Message,
                    "NetOptimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        else
        {
            await CheckForUpdatesAsync(silent: false);
        }
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info != null)
            {
                _pendingUpdate = info;
                BtnUpdate.Content = $"⬇ Обновить до {info.Version}";
                if (!silent)
                    MessageBox.Show($"Доступна новая версия {info.Version}. Нажмите кнопку обновления, чтобы установить.",
                        "Обновление", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (!silent)
            {
                MessageBox.Show($"У вас последняя версия ({UpdateService.CurrentVersion.ToString(3)}).",
                    "Обновление", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            if (!silent)
                MessageBox.Show("Не удалось проверить обновления: " + ex.Message +
                    "\n\nПроверьте, что в UpdateService указаны правильные логин и название репозитория, и что опубликован релиз.",
                    "NetOptimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------- Selection helpers ----------------

    private ConnectionInfo? Selected => ConnGrid.SelectedItem as ConnectionInfo;

    private List<ConnectionInfo> SelectedMany =>
        ConnGrid.SelectedItems.Cast<ConnectionInfo>().ToList();

    private void Report(ActionResult r)
    {
        StatusText.Text = r.Message.Replace("\n", " ");
        if (!r.Ok)
            MessageBox.Show(r.Message, "NetOptimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        Refresh();
    }

    // ---------------- Actions ----------------

    private void CloseConn_Click(object sender, RoutedEventArgs e)
    {
        var c = Selected;
        if (c == null) return;
        Report(ProcessActions.CloseConnection(c));
    }

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        var pids = SelectedMany.Select(c => c.Pid).Distinct().ToList();
        if (pids.Count == 0) return;
        var names = string.Join(", ", SelectedMany.Select(c => $"{c.ProcessName} ({c.Pid})").Distinct());
        if (MessageBox.Show($"Завершить процессы?\n\n{names}", "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        foreach (var pid in pids) StatusText.Text = ProcessActions.Kill(pid).Message;
        Refresh();
    }

    private void Suspend_Click(object sender, RoutedEventArgs e)
    {
        var c = Selected;
        if (c == null) return;
        Report(ProcessActions.Suspend(c.Pid));
    }

    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        var c = Selected;
        if (c == null) return;
        Report(ProcessActions.Resume(c.Pid));
    }

    private void Priority_Click(object sender, RoutedEventArgs e)
    {
        var c = Selected;
        if (c == null) return;
        if (sender is not MenuItem mi || mi.Tag is not string tag) return;
        if (!Enum.TryParse<ProcessPriorityClass>(tag, out var pc)) return;
        Report(ProcessActions.SetPriority(c.Pid, pc));
    }

    private void Block_Click(object sender, RoutedEventArgs e)
    {
        var c = Selected;
        if (c == null) return;
        if (MessageBox.Show($"Заблокировать в брандмауэре весь трафик программы?\n\n{c.ProcessPath}",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        Report(FirewallService.BlockProgram(c.ProcessPath));
    }

    private void Unblock_Click(object sender, RoutedEventArgs e)
    {
        var c = Selected;
        if (c == null) return;
        Report(FirewallService.UnblockProgram(c.ProcessPath));
    }

    private void Resolve_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in SelectedMany)
        {
            if (c.RemotePort == 0 || c.RemoteAddress is "*" or "0.0.0.0" or "::" || c.RemoteAddress.Length == 0)
                continue;
            string addr = c.RemoteAddress;
            var target = c;
            Task.Run(() =>
            {
                try { return Dns.GetHostEntry(addr).HostName; }
                catch { return "(не найдено)"; }
            }).ContinueWith(t =>
            {
                target.RemoteHost = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private void OpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var c = Selected;
        if (c?.ProcessPath == null || !File.Exists(c.ProcessPath))
        {
            StatusText.Text = "Путь к файлу недоступен.";
            return;
        }
        try { Process.Start("explorer.exe", $"/select,\"{c.ProcessPath}\""); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var c = Selected;
        if (c == null) return;
        try
        {
            Clipboard.SetText(
                $"{c.Protocol}\t{c.ProcessName} (PID {c.Pid})\t{c.LocalEndpoint}\t{c.RemoteEndpoint}\t{c.State}");
            StatusText.Text = "Строка скопирована.";
        }
        catch { /* clipboard busy */ }
    }

    // ---------------- Export ----------------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
            FileName = $"netoptimizer_{DateTime.Now:yyyyMMdd_HHmmss}"
        };
        if (dlg.ShowDialog(this) != true) return;

        var items = _view.Cast<ConnectionInfo>().ToList();
        try
        {
            if (dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                ExportService.ToJson(items, dlg.FileName);
            else
                ExportService.ToCsv(items, dlg.FileName);
            StatusText.Text = $"Экспортировано строк: {items.Count} → {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось экспортировать: " + ex.Message,
                "NetOptimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------------- GeoIP ----------------

    private async void GeoIp_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in SelectedMany)
        {
            if (c.RemotePort == 0 || string.IsNullOrEmpty(c.RemoteAddress) ||
                c.RemoteAddress is "*" or "0.0.0.0" or "::")
                continue;
            try { c.Country = await GeoIpService.LookupAsync(c.RemoteAddress); }
            catch { /* lookup failed */ }
        }
    }

    // ---------------- Whitelist ----------------

    private void Trust_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in SelectedMany.Select(c => c.ProcessName).Distinct())
            SettingsService.AddTrusted(n);
        StatusText.Text = "Добавлено в доверенные (не помечать подозрительными).";
        Refresh();
    }

    private void Untrust_Click(object sender, RoutedEventArgs e)
    {
        foreach (var n in SelectedMany.Select(c => c.ProcessName).Distinct())
            SettingsService.RemoveTrusted(n);
        StatusText.Text = "Убрано из доверенных.";
        Refresh();
    }

    // ---------------- Column customization ----------------

    private void Columns_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var col in ConnGrid.Columns)
        {
            string label = col.Header?.ToString() ?? "—";
            var item = new MenuItem { Header = Glyph(col) + label, StaysOpenOnClick = true };
            var c = col;
            var mi = item;
            item.Click += (_, _) =>
            {
                c.Visibility = c.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                mi.Header = Glyph(c) + label;
            };
            menu.Items.Add(item);
        }
        if (sender is UIElement el)
        {
            menu.PlacementTarget = el;
            menu.IsOpen = true;
        }
    }

    private static string Glyph(DataGridColumn c) =>
        c.Visibility == Visibility.Visible ? "☑  " : "☐  ";

    // ---------------- Keyboard shortcuts ----------------

    private void ConnGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            CopySelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            CloseSelectedConnections();
            e.Handled = true;
        }
        else if (e.Key == Key.Space)
        {
            foreach (var c in SelectedMany.Select(x => x.Pid).Distinct())
                ProcessActions.Suspend(c);
            StatusText.Text = "Выбранные процессы приостановлены (возобновить — в меню).";
            e.Handled = true;
        }
    }

    private void CopySelected()
    {
        var rows = SelectedMany;
        if (rows.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("Протокол\tПроцесс\tPID\tЛокальный\tУдалённый\tХост\tСтрана\tСостояние");
        foreach (var c in rows)
            sb.AppendLine($"{c.Protocol}\t{c.ProcessName}\t{c.Pid}\t{c.LocalEndpoint}\t{c.RemoteEndpoint}\t{c.RemoteHost}\t{c.Country}\t{c.State}");
        try
        {
            Clipboard.SetText(sb.ToString());
            StatusText.Text = $"Скопировано строк: {rows.Count}";
        }
        catch { /* clipboard busy */ }
    }

    private void CloseSelectedConnections()
    {
        var rows = SelectedMany.Where(c => c.Protocol == "TCP" && !c.IsIPv6).ToList();
        if (rows.Count == 0) return;
        int closed = 0;
        foreach (var c in rows)
            if (ProcessActions.CloseConnection(c).Ok) closed++;
        StatusText.Text = $"Закрыто соединений: {closed}";
        Refresh();
    }
}
