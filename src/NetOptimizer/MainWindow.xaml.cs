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
    private readonly DispatcherTimer _flushTimer;
    private readonly TrafficMonitor _traffic = new();
    private UpdateService.UpdateInfo? _pendingUpdate;
    private readonly ObservableCollection<DiagStep> _diagSteps = new();
    private string _logName = "System";
    private bool _logLoaded;
    private bool _busy;

    /// <summary>0 = current session, otherwise number of days of stored history.</summary>
    private int _statsDays;
    private bool _diagRunning;

    private const int SparkSamples = 60;

    public MainWindow()
    {
        InitializeComponent();

        ConnGrid.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterPredicate;

        DiagList.ItemsSource = _diagSteps;
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
            UpdateService.CleanupLeftovers();
            UsageStats.Prune();
            _ = CheckForUpdatesAsync();
        };

        // Traffic counters are only useful if they survive the app closing.
        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _flushTimer.Tick += (_, _) => UsageStats.Flush();
        _flushTimer.Start();

        Closed += (_, _) =>
        {
            _flushTimer.Stop();
            UsageStats.Flush();
            _traffic.Dispose();
        };
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
        UpdateVersionText.Text = $"Установленная версия: {UpdateService.Format(UpdateService.CurrentVersion)}";
        BtnUpdateApp.Content = _pendingUpdate != null
            ? $"🔄 Обновить до {UpdateService.Format(_pendingUpdate.Version)}"
            : "🔄 Обновить приложение";
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

            var (totalDown, totalUp) = _traffic.GetTotal(c.Pid);
            c.TotalDown = totalDown;
            c.TotalUp = totalUp;

            // Several rows can share a PID; Record only counts the growth, so
            // the repeated calls within one tick add nothing.
            UsageStats.Record(c.Pid, c.ProcessName, totalDown, totalUp);
        }

        if (StatsView.Visibility == Visibility.Visible && _statsDays == 0)
            RebuildStats();
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

    private enum ViewId { Connections, Stats, Diagnostics, Log }

    private void ShowConnections_Click(object sender, RoutedEventArgs e) => SwitchView(ViewId.Connections);

    private void ShowStats_Click(object sender, RoutedEventArgs e)
    {
        SwitchView(ViewId.Stats);
        RebuildStats();
    }

    private void ShowDiag_Click(object sender, RoutedEventArgs e) => SwitchView(ViewId.Diagnostics);

    private void ShowLog_Click(object sender, RoutedEventArgs e)
    {
        SwitchView(ViewId.Log);
        if (!_logLoaded) LoadLog();
    }

    private void SwitchView(ViewId view)
    {
        ConnectionsView.Visibility = view == ViewId.Connections ? Visibility.Visible : Visibility.Collapsed;
        StatsView.Visibility = view == ViewId.Stats ? Visibility.Visible : Visibility.Collapsed;
        DiagView.Visibility = view == ViewId.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
        LogView.Visibility = view == ViewId.Log ? Visibility.Visible : Visibility.Collapsed;

        BtnViewConn.Tag = view == ViewId.Connections ? "active" : "inactive";
        BtnViewStats.Tag = view == ViewId.Stats ? "active" : "inactive";
        BtnViewDiag.Tag = view == ViewId.Diagnostics ? "active" : "inactive";
        BtnViewLog.Tag = view == ViewId.Log ? "active" : "inactive";
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

    private bool _updating;

    private async void Update_Click(object sender, RoutedEventArgs e) => await RunUpdateAsync();

    /// <summary>Settings panel: check, download, verify and install in one click.</summary>
    private async void UpdateApp_Click(object sender, RoutedEventArgs e) => await RunUpdateAsync();

    private async Task RunUpdateAsync()
    {
        if (_updating) return;
        _updating = true;
        BtnUpdate.IsEnabled = false;
        BtnUpdateApp.IsEnabled = false;

        try
        {
            // 1. Find out whether there is anything to install.
            if (_pendingUpdate == null)
            {
                SetUpdateStatus("Проверка обновлений…");
                _pendingUpdate = await UpdateService.CheckAsync();

                if (_pendingUpdate == null)
                {
                    SetUpdateStatus($"У вас последняя версия ({UpdateService.Format(UpdateService.CurrentVersion)}).");
                    return;
                }
            }

            var info = _pendingUpdate!;
            BtnUpdate.Content = $"⬇ Обновить до {UpdateService.Format(info.Version)}";
            BtnUpdateApp.Content = $"🔄 Обновить до {UpdateService.Format(info.Version)}";

            // 2. Ask before replacing the program.
            string notes = string.IsNullOrWhiteSpace(info.Notes)
                ? ""
                : "\n\nЧто нового:\n" + Trim(info.Notes, 600);

            if (MessageBox.Show(
                    $"Доступна версия {UpdateService.Format(info.Version)} (установлена {UpdateService.Format(UpdateService.CurrentVersion)}).\n\n" +
                    "Программа скачает её из GitHub, проверит контрольную сумму, заменит старый файл " +
                    "и перезапустится. Ненужные файлы будут удалены." + notes,
                    "Обновление NetOptimizer", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                SetUpdateStatus($"Доступна версия {UpdateService.Format(info.Version)}. Обновление отложено.");
                return;
            }

            // 3. Download with progress, verify, swap, restart.
            UpdateProgress.Value = 0;
            UpdateProgress.Visibility = Visibility.Visible;
            SetUpdateStatus("Загрузка обновления…");

            var progress = new Progress<double>(p =>
            {
                UpdateProgress.Value = p;
                SetUpdateStatus($"Загрузка обновления… {p * 100:0}%");
            });

            await UpdateService.DownloadAndApplyAsync(info, progress);
            SetUpdateStatus("Установка обновления, программа перезапустится…");
        }
        catch (UpdateService.UpdateRejectedException ex)
        {
            SetUpdateStatus(ex.Message);
            MessageBox.Show(ex.Message, "Обновление отменено", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            string msg = "Не удалось обновиться: " + ex.Message;
            SetUpdateStatus(msg);
            MessageBox.Show(msg, "NetOptimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            UpdateProgress.Visibility = Visibility.Collapsed;
            BtnUpdate.IsEnabled = true;
            BtnUpdateApp.IsEnabled = true;
            _updating = false;
        }
    }

    private void SetUpdateStatus(string text)
    {
        UpdateStatusText.Text = text;
        StatusText.Text = text.Replace("\n", " ");
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";

    /// <summary>Silent background check on startup — only updates the button caption.</summary>
    private async Task CheckForUpdatesAsync()
    {
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info == null) return;

            _pendingUpdate = info;
            BtnUpdate.Content = $"⬇ Обновить до {UpdateService.Format(info.Version)}";
        }
        catch
        {
            // No network / no releases — the manual button reports the reason.
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
        var targets = SelectedMany
            .GroupBy(c => c.Pid)
            .Select(g => g.First())
            .ToList();
        if (targets.Count == 0) return;

        var allowed = new List<ConnectionInfo>();
        var blocked = new List<string>();
        var warnings = new List<string>();

        foreach (var c in targets)
        {
            switch (ProcessActions.CanTouch(c.Pid, c.ProcessName, out string reason))
            {
                case ProcessGuard.Verdict.Blocked:
                    blocked.Add(reason);
                    break;
                case ProcessGuard.Verdict.NeedsConfirmation:
                    warnings.Add(reason);
                    allowed.Add(c);
                    break;
                default:
                    allowed.Add(c);
                    break;
            }
        }

        if (blocked.Count > 0)
            MessageBox.Show(string.Join("\n\n", blocked.Distinct()),
                "Действие заблокировано", MessageBoxButton.OK, MessageBoxImage.Stop);

        if (allowed.Count == 0) return;

        string names = string.Join(", ", allowed.Select(c => $"{c.ProcessName} ({c.Pid})"));
        string question = $"Завершить процессы?\n\n{names}";
        if (warnings.Count > 0)
            question += "\n\n⚠ " + string.Join("\n⚠ ", warnings.Distinct());

        if (MessageBox.Show(question, "Подтверждение",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        foreach (var c in allowed)
            StatusText.Text = ProcessActions.Kill(c.Pid, c.ProcessName).Message;
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
            SuspendSelected();
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

    /// <summary>
    /// Space used to suspend every selected process without asking. With a hundred
    /// rows selected that could freeze half the machine on an accidental keypress.
    /// </summary>
    private void SuspendSelected()
    {
        var targets = SelectedMany.GroupBy(c => c.Pid).Select(g => g.First()).ToList();
        if (targets.Count == 0) return;

        var allowed = new List<ConnectionInfo>();
        var blocked = new List<string>();
        foreach (var c in targets)
        {
            if (ProcessActions.CanTouch(c.Pid, c.ProcessName, out string reason) == ProcessGuard.Verdict.Blocked)
                blocked.Add(reason);
            else
                allowed.Add(c);
        }

        if (blocked.Count > 0)
            MessageBox.Show(string.Join("\n\n", blocked.Distinct()),
                "Действие заблокировано", MessageBoxButton.OK, MessageBoxImage.Stop);
        if (allowed.Count == 0) return;

        string names = string.Join(", ", allowed.Select(c => $"{c.ProcessName} ({c.Pid})"));
        if (MessageBox.Show(
                $"Приостановить процессы?\n\n{names}\n\nПриостановленная программа перестаёт отвечать, " +
                "пока вы не возобновите её через контекстное меню.",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        int done = 0;
        foreach (var c in allowed)
            if (ProcessActions.Suspend(c.Pid).Ok) done++;

        StatusText.Text = $"Приостановлено процессов: {done} (возобновить — в контекстном меню).";
    }

    private void CloseSelectedConnections()
    {
        var rows = SelectedMany.Where(c => c.Protocol == "TCP" && !c.IsIPv6).ToList();
        if (rows.Count == 0) return;

        if (rows.Count > 1 && MessageBox.Show(
                $"Закрыть выбранные соединения ({rows.Count})?\n\n" +
                "Программы, которым они принадлежат, могут потерять связь и переподключиться.",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        int closed = 0;
        foreach (var c in rows)
            if (ProcessActions.CloseConnection(c).Ok) closed++;
        StatusText.Text = $"Закрыто соединений: {closed}";
        Refresh();
    }
    // ---------------- Statistics ----------------

    private void Period_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not string s || !int.TryParse(s, out int days))
            return;

        _statsDays = days;
        foreach (var child in PeriodPanel.Children)
            if (child is Button pb && pb.CommandParameter is string ps)
                pb.Tag = ps == s ? "active" : "inactive";

        RebuildStats();
    }

    private void RefreshStats_Click(object sender, RoutedEventArgs e) => RebuildStats();

    private void ClearStats_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "Удалить всю накопленную статистику трафика?\n\nИстория по дням будет стёрта безвозвратно.",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        UsageStats.Clear();
        RebuildStats();
    }

    private void RebuildStats()
    {
        List<UsageRow> all = _statsDays == 0 ? BuildSessionRows() : BuildPeriodRows(_statsDays);

        long down = all.Sum(r => r.Down);
        long up = all.Sum(r => r.Up);

        StatsTotalDown.Text = UsageStats.FormatBytes(down);
        StatsTotalUp.Text = UsageStats.FormatBytes(up);
        StatsTotalAll.Text = UsageStats.FormatBytes(down + up);

        StatsList.ItemsSource = UsageRow.Rank(all, 40);

        StatsStatus.Text = _statsDays switch
        {
            0 when !_traffic.Available => "Мониторинг трафика недоступен — запустите программу от администратора.",
            0 => $"Текущая сессия, с {_traffic.StartedAt:HH:mm} · приложений: {all.Count(r => r.Total > 0)}",
            1 => $"Сегодня · приложений: {all.Count(r => r.Total > 0)}",
            _ => $"За последние {_statsDays} дн. · приложений: {all.Count(r => r.Total > 0)}"
        };
    }

    /// <summary>Live totals since launch, grouped by process name across its PIDs.</summary>
    private List<UsageRow> BuildSessionRows()
    {
        var nameByPid = new Dictionary<int, string>();
        var iconByName = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in _items)
        {
            nameByPid[c.Pid] = c.ProcessName;
            if (!iconByName.ContainsKey(c.ProcessName)) iconByName[c.ProcessName] = c.Icon;
        }

        var acc = new Dictionary<string, (long down, long up, double[] history)>(StringComparer.OrdinalIgnoreCase);

        foreach (int pid in _traffic.KnownPids())
        {
            // A process may have exited since it last transferred data.
            string name = nameByPid.TryGetValue(pid, out var n) ? n : $"PID {pid}";

            var (d, u) = _traffic.GetTotal(pid);
            var hist = _traffic.GetHistory(pid);

            if (!acc.TryGetValue(name, out var e))
                e = (0, 0, new double[SparkSamples]);

            e.down += d;
            e.up += u;
            for (int i = 0; i < hist.Length && i < e.history.Length; i++)
                e.history[i] += hist[i];

            acc[name] = e;
        }

        return acc.Select(kv => new UsageRow
        {
            Name = kv.Key,
            Down = kv.Value.down,
            Up = kv.Value.up,
            Icon = iconByName.TryGetValue(kv.Key, out var ic) ? ic : null,
            Spark = UsageRow.BuildSpark(kv.Value.history)
        }).ToList();
    }

    /// <summary>Totals read back from the stored daily files.</summary>
    private List<UsageRow> BuildPeriodRows(int days)
    {
        var iconByName = new Dictionary<string, ImageSource?>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in _items)
            if (!iconByName.ContainsKey(c.ProcessName)) iconByName[c.ProcessName] = c.Icon;

        return UsageStats.GetRange(days).Select(x => new UsageRow
        {
            Name = x.Name,
            Down = x.Down,
            Up = x.Up,
            Icon = iconByName.TryGetValue(x.Name, out var ic) ? ic : null
        }).ToList();
    }

    // ---------------- Diagnostics ----------------

    private async void DiagRun_Click(object sender, RoutedEventArgs e)
    {
        if (_diagRunning) return;
        _diagRunning = true;
        BtnDiagRun.IsEnabled = false;
        BtnDiagRun.Content = "Проверяю…";
        _diagSteps.Clear();
        DiagVerdict.Text = "Идёт проверка сети…";

        try
        {
            // Progress is created on the UI thread, so its callbacks land there too.
            var progress = new Progress<DiagStep>(step =>
            {
                if (!_diagSteps.Contains(step)) _diagSteps.Add(step);
            });

            var steps = await DiagnosticsService.RunAsync(progress);
            DiagVerdict.Text = DiagnosticsService.Verdict(steps);
            ActionLog.Info("Диагностика сети: " + DiagVerdict.Text);
        }
        catch (Exception ex)
        {
            DiagVerdict.Text = "Проверка не завершилась: " + ex.Message;
            ActionLog.Error("Диагностика сети прервана", ex);
        }
        finally
        {
            BtnDiagRun.IsEnabled = true;
            BtnDiagRun.Content = "Проверить сеть";
            _diagRunning = false;
        }
    }

    private void DiagCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_diagSteps.Count == 0)
        {
            StatusText.Text = "Сначала запустите проверку.";
            return;
        }

        var sb = new StringBuilder();
        sb.AppendLine("NetOptimizer — диагностика сети");
        sb.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        sb.AppendLine(DiagVerdict.Text);
        sb.AppendLine();
        foreach (var step in _diagSteps) sb.AppendLine(step.ToString());

        try
        {
            Clipboard.SetText(sb.ToString());
            StatusText.Text = "Отчёт о диагностике скопирован.";
        }
        catch { /* clipboard busy */ }
    }
}
