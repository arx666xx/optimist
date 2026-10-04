using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using NetOptimizer.Models;
using NetOptimizer.Services;

namespace NetOptimizer;

public partial class BoostWindow : Window
{
    public sealed class ProcItem
    {
        /// <summary>Representative PID (the busiest one); 0 = no priority app.</summary>
        public int Pid { get; init; }
        public string Name { get; init; } = "";
        public string Title { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public ImageSource? Icon { get; init; }
    }

    private readonly IReadOnlyList<ConnectionInfo> _connections;
    private readonly ObservableCollection<ProcItem> _procs = new();
    private readonly ICollectionView _procView;
    private readonly DispatcherTimer _ramTimer;
    private bool _running;

    /// <param name="preselectName">Process name to select up front (from the app details panel).</param>
    public BoostWindow(IReadOnlyList<ConnectionInfo> connections, string? preselectName = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeHelper.SetTitleBar(this, ThemeService.Current == ThemeService.Dark);

        _connections = connections;

        _procs.Add(new ProcItem
        {
            Pid = 0,
            Title = "Без приоритетного приложения",
            Subtitle = "Только освободить память и разгрузить фон"
        });

        // One entry per program, not per PID — a browser is one choice, not thirty.
        var apps = connections
            .Where(c => c.Pid > 4 && !ProcessGuard.IsProtected(c.ProcessName))
            .GroupBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                int busiest = g.GroupBy(c => c.Pid).OrderByDescending(x => x.Count()).First().Key;
                int pids = g.Select(c => c.Pid).Distinct().Count();
                return new
                {
                    Count = g.Count(),
                    Item = new ProcItem
                    {
                        Pid = busiest,
                        Name = first.ProcessName,
                        Title = first.Description.Length > 0 ? first.Description : first.ProcessName,
                        Subtitle = pids > 1
                            ? $"{first.ProcessName}.exe · {AppGroup.Plural(pids, "процесс", "процесса", "процессов")}"
                            : $"{first.ProcessName}.exe",
                        Icon = first.Icon
                    }
                };
            })
            .OrderByDescending(x => x.Count)
            .Select(x => x.Item);

        foreach (var a in apps) _procs.Add(a);

        ProcList.ItemsSource = _procs;
        _procView = CollectionViewSource.GetDefaultView(_procs);
        _procView.Filter = Filter;

        ProcList.SelectedItem = preselectName == null
            ? _procs[0]
            : _procs.FirstOrDefault(p => p.Name.Equals(preselectName, StringComparison.OrdinalIgnoreCase)) ?? _procs[0];
        ProcList.ScrollIntoView(ProcList.SelectedItem);

        ShowRam(MemoryService.GetStatus());
        _ramTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _ramTimer.Tick += (_, _) => { if (!_running) ShowRam(MemoryService.GetStatus()); };
        _ramTimer.Start();
        Closed += (_, _) => _ramTimer.Stop();

        BtnRestore.IsEnabled = BoostService.HasChanges;
    }

    private void ShowRam(MemorySnapshot m)
    {
        RamPercent.Text = $"{m.LoadPercent}%";
        RamUsed.Text = "занято " + m.UsedText;
        RamBar.Value = m.LoadPercent;
    }

    private bool Filter(object obj)
    {
        if (obj is not ProcItem p) return false;
        if (p.Pid == 0) return true;
        string q = Search.Text?.Trim() ?? "";
        return q.Length == 0
            || p.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => _procView?.Refresh();

    private async void Boost_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;

        var sel = ProcList.SelectedItem as ProcItem;
        int? target = sel is { Pid: > 0 } ? sel.Pid : null;

        var options = new BoostOptions(
            TargetPid: target,
            FreeMemory: FreeMemory.IsChecked == true,
            PurgeCache: PurgeCache.IsChecked == true,
            LowerOthers: LowerOthers.IsChecked == true,
            CloseOthersConnections: CloseOthers.IsChecked == true);

        if (!options.FreeMemory && !options.PurgeCache && !options.LowerOthers
            && !options.CloseOthersConnections && target == null)
        {
            LogBox.Text = "Ничего не выбрано: отметьте хотя бы одно действие или приложение.";
            return;
        }

        // Only the network part is disruptive enough to ask about.
        if (options.CloseOthersConnections && MessageBox.Show(
                "Закрыть сетевые соединения фоновых программ?\n\n" +
                "Они переподключатся сами, но идущие загрузки и звонки в них прервутся.",
                "Ускорение", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _running = true;
        BtnBoost.IsEnabled = false;
        BtnBoost.Content = "Ускоряю…";
        LogBox.Text = "Применяю…";

        try
        {
            var r = await Task.Run(() => BoostService.Apply(options, _connections));

            ShowRam(r.After);
            LogBox.Text = string.Join("\n", r.Notes);

            if (options.FreeMemory || options.PurgeCache)
            {
                long freed = (long)r.Before.UsedBytes - (long)r.After.UsedBytes;
                ResultMain.Text = $"{r.Before.LoadPercent}% → {r.After.LoadPercent}%";
                ResultSub.Text = freed > 0
                    ? $"освобождено {UsageStats.FormatBytes(freed)}"
                    : "память уже была свободна";
                ResultPanel.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            LogBox.Text = "Ускорение не выполнено: " + ex.Message;
            ActionLog.Error("Ускорение прервано", ex);
        }
        finally
        {
            _running = false;
            BtnBoost.IsEnabled = true;
            BtnBoost.Content = "Ускорить";
            BtnRestore.IsEnabled = BoostService.HasChanges;
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Text = await Task.Run(BoostService.RestorePriorities);
        BtnRestore.IsEnabled = BoostService.HasChanges;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
