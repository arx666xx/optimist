using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using NetOptimizer.Models;
using NetOptimizer.Services;

namespace NetOptimizer;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ConnectionInfo> _items = new();
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _timer;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();

        ConnGrid.ItemsSource = _items;
        _view = CollectionViewSource.GetDefaultView(_items);
        _view.Filter = FilterPredicate;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();

        Loaded += (_, _) =>
        {
            Refresh();
            _timer.Start();
        };
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
            _busy = false;
            _view.Refresh();
            StatusText.Text = $"Обновлено: {DateTime.Now:HH:mm:ss}";
            CountText.Text = $"Показано: {_view.Cast<object>().Count()} из {_items.Count}";
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Reconcile(List<ConnectionInfo> incoming)
    {
        var existing = _items.ToDictionary(x => x.Key);
        var seen = new HashSet<string>();

        foreach (var c in incoming)
        {
            seen.Add(c.Key);
            if (existing.TryGetValue(c.Key, out var e))
            {
                e.State = c.State;           // update mutable fields in place
                e.Suspicious = c.Suspicious; // (keeps selection + resolved DNS)
            }
            else
            {
                _items.Add(c);
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
}
