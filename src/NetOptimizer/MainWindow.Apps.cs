using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using NetOptimizer.Models;
using NetOptimizer.Services;

namespace NetOptimizer;

/// <summary>
/// The "Приложения" mode of the network tab: one row per program with a details
/// panel, instead of a wall of raw socket rows. The raw table is still one click
/// away ("Соединения") for people who want it.
/// </summary>
public partial class MainWindow
{
    private enum Scope { Online, All, Suspicious }

    private readonly ObservableCollection<AppGroup> _apps = new();
    private readonly ObservableCollection<RemoteItem> _remotes = new();
    private ListCollectionView _appView = null!;
    private Scope _scope = Scope.Online;
    private bool _rawMode;

    /// <summary>Reverse-DNS answers survive refreshes and app switches.</summary>
    private static readonly ConcurrentDictionary<string, string> HostCache = new();
    private static readonly ConcurrentDictionary<string, string> CountryCache = new();

    private void InitApps()
    {
        AppList.ItemsSource = _apps;
        _appView = (ListCollectionView)CollectionViewSource.GetDefaultView(_apps);
        _appView.Filter = AppFilter;

        // Busiest on top; total only grows, so rows do not jump around every tick.
        _appView.SortDescriptions.Add(new SortDescription(nameof(AppGroup.TotalBytes), ListSortDirection.Descending));
        _appView.SortDescriptions.Add(new SortDescription(nameof(AppGroup.Title), ListSortDirection.Ascending));
        _appView.IsLiveSorting = true;
        _appView.LiveSortingProperties.Add(nameof(AppGroup.TotalBytes));
        _appView.IsLiveFiltering = true;
        _appView.LiveFilteringProperties.Add(nameof(AppGroup.IsOnline));
        _appView.LiveFilteringProperties.Add(nameof(AppGroup.Suspicious));

        RemoteList.ItemsSource = _remotes;
    }

    private AppGroup? SelectedApp => AppList.SelectedItem as AppGroup;

    // ---------------- Building the list ----------------

    private void RebuildApps()
    {
        var groups = _items
            .Where(c => c.Pid > 0)
            .GroupBy(AppGroup.KeyOf)
            .ToDictionary(g => g.Key, g => g.ToList());

        var existing = _apps.ToDictionary(a => a.Key);

        foreach (var (key, conns) in groups)
        {
            if (!existing.TryGetValue(key, out var app))
            {
                app = new AppGroup(key);
                app.Update(conns, _traffic);
                _apps.Add(app);
            }
            else app.Update(conns, _traffic);
        }

        for (int i = _apps.Count - 1; i >= 0; i--)
            if (!groups.ContainsKey(_apps[i].Key))
                _apps.RemoveAt(i);

        if (SelectedApp != null) UpdateDetails(SelectedApp, selectionChanged: false);
        UpdateAppsEmpty();
    }

    private bool AppFilter(object obj)
    {
        if (obj is not AppGroup a) return false;

        if (_scope == Scope.Online && !a.IsOnline) return false;
        if (_scope == Scope.Suspicious && !a.Suspicious) return false;

        string q = FilterBox.Text?.Trim() ?? "";
        if (q.Length == 0) return true;

        return a.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.ProcessName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.ServiceNames.Contains(q, StringComparison.OrdinalIgnoreCase)
            || a.Connections.Any(c => c.RemoteAddress.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || c.RemoteHost.Contains(q, StringComparison.OrdinalIgnoreCase)
                                   || c.RemotePort.ToString() == q
                                   || c.LocalPort.ToString() == q);
    }

    private void RefreshFilters()
    {
        _appView?.Refresh();
        _view?.Refresh();
        UpdateAppsEmpty();
        UpdateCount();
    }

    private void UpdateAppsEmpty()
    {
        bool empty = _appView.IsEmpty;
        AppsEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        AppsEmpty.Text = (FilterBox.Text?.Trim().Length ?? 0) > 0
            ? "Ничего не найдено."
            : _scope switch
            {
                Scope.Suspicious => "Подозрительных программ не найдено. Это хорошо.",
                Scope.Online => "Сейчас ни одно приложение не выходит в интернет.",
                _ => "Сетевых программ не найдено."
            };
    }

    private void UpdateCount()
    {
        if (_appView == null) return;
        CountText.Text = _rawMode
            ? $"Соединений: {_view.Cast<object>().Count()} из {_items.Count}"
            : $"Приложений: {_appView.Count} из {_apps.Count}";
    }

    // ---------------- Toolbar ----------------

    private void Scope_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not string s) return;

        _scope = s switch { "all" => Scope.All, "suspicious" => Scope.Suspicious, _ => Scope.Online };
        foreach (var child in ScopePanel.Children)
            if (child is Button pb)
                pb.Tag = ReferenceEquals(pb, b) ? "active" : "inactive";

        RefreshFilters();
    }

    private void ModeApps_Click(object sender, RoutedEventArgs e) => SetRawMode(false);
    private void ModeRaw_Click(object sender, RoutedEventArgs e) => SetRawMode(true);

    private void SetRawMode(bool raw)
    {
        _rawMode = raw;
        AppsPane.Visibility = raw ? Visibility.Collapsed : Visibility.Visible;
        RawPane.Visibility = raw ? Visibility.Visible : Visibility.Collapsed;
        RawTools.Visibility = raw ? Visibility.Visible : Visibility.Collapsed;
        BtnModeApps.Tag = raw ? "inactive" : "active";
        BtnModeRaw.Tag = raw ? "active" : "inactive";
        if (raw) _view.Refresh();
        UpdateCount();
    }

    // ---------------- Details panel ----------------

    private void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var app = SelectedApp;
        DetailsPanel.DataContext = app;
        DetailsPanel.Visibility = app != null ? Visibility.Visible : Visibility.Collapsed;
        DetailsEmpty.Visibility = app != null ? Visibility.Collapsed : Visibility.Visible;
        if (app != null) UpdateDetails(app, selectionChanged: true);
    }

    private void AppList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedApp != null) ShowConnectionsOf(SelectedApp);
    }

    private void UpdateDetails(AppGroup app, bool selectionChanged)
    {
        DetailServices.Text = app.ServiceNames.Length > 0 ? "Службы: " + app.ServiceNames : "";
        DetailServices.Visibility = app.ServiceNames.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailPath.Visibility = string.IsNullOrEmpty(app.ProcessPath) ? Visibility.Collapsed : Visibility.Visible;
        BtnAppTrust.Content = SettingsService.IsTrusted(app.ProcessName) ? "Не доверять" : "Доверять";

        // Servers: one line per remote address, most connections first.
        var remotes = app.Connections
            .Where(AppGroup.IsExternal)
            .GroupBy(c => c.RemoteAddress)
            .Select(g => new
            {
                Address = g.Key,
                Port = g.GroupBy(c => c.RemotePort).OrderByDescending(x => x.Count()).First().Key,
                Count = g.Count()
            })
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Address, StringComparer.Ordinal)
            .Take(30)
            .ToList();

        bool same = !selectionChanged
                    && remotes.Count == _remotes.Count
                    && remotes.Zip(_remotes).All(p => p.First.Address == p.Second.Address
                                                   && p.First.Count == p.Second.Count
                                                   && p.First.Port == p.Second.Port);
        if (!same)
        {
            _remotes.Clear();
            foreach (var r in remotes)
            {
                var item = new RemoteItem { Address = r.Address, Port = r.Port, Count = r.Count };
                if (HostCache.TryGetValue(r.Address, out var host)) item.Host = host;
                if (CountryCache.TryGetValue(r.Address, out var country)) item.Country = country;
                _remotes.Add(item);
            }
            ResolveHosts(_remotes.Where(r => !HostCache.ContainsKey(r.Address)).ToList());
        }

        RemoteEmpty.Visibility = _remotes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BtnCountries.Visibility = _remotes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        var ports = app.Connections
            .Where(c => c.State == "LISTENING")
            .Select(c => c.LocalPort)
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        ListeningText.Text = ports.Count == 0
            ? ""
            : "Принимает входящие подключения на " +
              (ports.Count == 1 ? $"порту {ports[0]}" : $"портах {string.Join(", ", ports.Take(8))}{(ports.Count > 8 ? "…" : "")}");
        ListeningText.Visibility = ports.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Reverse DNS for the details panel — a few parallel lookups, cached.</summary>
    private void ResolveHosts(List<RemoteItem> items)
    {
        foreach (var item in items.Take(12))
        {
            string addr = item.Address;
            var target = item;
            Task.Run(() =>
            {
                try { return Dns.GetHostEntry(addr).HostName; }
                catch { return ""; }
            }).ContinueWith(t =>
            {
                // An unresolvable address is remembered too, so it is not retried every tick.
                string host = t.Result == addr ? "" : t.Result;
                HostCache[addr] = host;
                target.Host = host;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }

    private async void AppCountries_Click(object sender, RoutedEventArgs e)
    {
        BtnCountries.IsEnabled = false;
        try
        {
            var lookups = _remotes.Where(r => r.Country.Length == 0).Select(async r =>
            {
                try
                {
                    r.Country = await GeoIpService.LookupAsync(r.Address);
                    CountryCache[r.Address] = r.Country;
                }
                catch { /* lookup failed */ }
            });
            await Task.WhenAll(lookups);
        }
        finally { BtnCountries.IsEnabled = true; }
    }

    // ---------------- Actions on the selected application ----------------

    private List<(int Pid, string ProcessName)> AppProcesses(AppGroup app)
        => app.Pids.Select(pid => (pid, app.ProcessName)).ToList();

    private void AppBoost_Click(object sender, RoutedEventArgs e)
    {
        var app = SelectedApp;
        if (app != null && ProcessGuard.IsProtected(app.ProcessName))
        {
            MessageBox.Show($"«{app.Title}» — системный компонент Windows, его ускорять не нужно.\n\n" +
                            "Откроется общее ускорение без приоритетного приложения.",
                "Ускорение", MessageBoxButton.OK, MessageBoxImage.Information);
            app = null;
        }
        OpenBoost(app?.ProcessName);
    }

    private void AppShowConnections_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp != null) ShowConnectionsOf(SelectedApp);
    }

    private void ShowConnectionsOf(AppGroup app)
    {
        FilterBox.Text = app.ProcessName;
        SetRawMode(true);
    }

    private void AppKill_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is { } app) KillProcesses(AppProcesses(app));
    }

    private void AppSuspend_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is { } app) SuspendProcesses(AppProcesses(app));
    }

    private void AppResume_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        int done = app.Pids.Count(pid => ProcessActions.Resume(pid).Ok);
        StatusText.Text = $"«{app.Title}»: возобновлено процессов — {done}.";
    }

    private void AppBlock_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        if (string.IsNullOrEmpty(app.ProcessPath))
        {
            MessageBox.Show("Не удалось определить файл программы — заблокировать её нельзя.",
                "NetOptimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (ProcessGuard.IsProtected(app.ProcessName) &&
            MessageBox.Show($"«{app.Title}» — часть Windows. Если запретить ей интернет, могут перестать " +
                            "работать обновления, синхронизация времени или сеть целиком.\n\nВсё равно запретить?",
                "Внимание", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (MessageBox.Show($"Запретить «{app.Title}» доступ в интернет?\n\n{app.ProcessPath}\n\n" +
                            "Вернуть доступ можно кнопкой «Разрешить снова».",
                "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        Report(FirewallService.BlockProgram(app.ProcessPath));
    }

    private void AppUnblock_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is { ProcessPath: { Length: > 0 } path }) Report(FirewallService.UnblockProgram(path));
    }

    private void AppOpenLocation_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectedApp?.ProcessPath;
        if (path == null || !File.Exists(path))
        {
            StatusText.Text = "Путь к файлу недоступен.";
            return;
        }
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void AppTrust_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedApp is not { } app) return;
        if (SettingsService.IsTrusted(app.ProcessName))
        {
            SettingsService.RemoveTrusted(app.ProcessName);
            StatusText.Text = $"«{app.Title}» больше не в доверенных.";
        }
        else
        {
            SettingsService.AddTrusted(app.ProcessName);
            StatusText.Text = $"«{app.Title}» добавлено в доверенные — не будет помечаться подозрительным.";
        }
        Refresh();
    }
}
