using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using NetOptimizer.Models;
using NetOptimizer.Services;

namespace NetOptimizer;

public partial class BoostWindow : Window
{
    public sealed class ProcItem
    {
        public int Pid { get; init; }
        public string Name { get; init; } = "";
        public ImageSource? Icon { get; init; }
        public int Count { get; init; }
        public string Display => $"{Name}   ·   PID {Pid}   ·   соединений: {Count}";
    }

    private readonly IReadOnlyList<ConnectionInfo> _connections;
    private readonly ObservableCollection<ProcItem> _procs = new();
    private readonly ICollectionView _procView;

    public BoostWindow(IReadOnlyList<ConnectionInfo> connections)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeHelper.SetTitleBar(this, ThemeService.Current == ThemeService.Dark);

        _connections = connections;

        foreach (var g in connections.GroupBy(c => c.Pid))
        {
            var first = g.First();
            _procs.Add(new ProcItem
            {
                Pid = g.Key,
                Name = first.ProcessName,
                Icon = first.Icon,
                Count = g.Count()
            });
        }

        // Sort by connection count desc so the busiest apps are on top.
        var sorted = _procs.OrderByDescending(p => p.Count).ToList();
        _procs.Clear();
        foreach (var p in sorted) _procs.Add(p);

        ProcList.ItemsSource = _procs;
        _procView = CollectionViewSource.GetDefaultView(_procs);
        _procView.Filter = Filter;
    }

    private bool Filter(object obj)
    {
        if (obj is not ProcItem p) return false;
        string q = Search.Text?.Trim() ?? "";
        return q.Length == 0
            || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || p.Pid.ToString().Contains(q);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => _procView?.Refresh();

    private void Boost_Click(object sender, RoutedEventArgs e)
    {
        if (ProcList.SelectedItem is not ProcItem sel)
        {
            MessageBox.Show("Сначала выберите приложение в списке.", "Ускорение",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool lower = LowerOthers.IsChecked == true;
        bool close = CloseOthers.IsChecked == true;

        string warn = $"Ускорить «{sel.Name}» (PID {sel.Pid})?\n\n" +
                      (lower ? "• остальным приложениям будет понижен приоритет\n" : "") +
                      (close ? "• их фоновые соединения будут закрыты (сеть у других приложений может прерваться)\n" : "");
        if (MessageBox.Show(warn, "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        BtnBoost.IsEnabled = false;
        LogBox.Text = "Применяю…";

        int pid = sel.Pid;
        Task.Run(() => BoostService.Apply(pid, _connections, lower, close))
            .ContinueWith(t =>
            {
                var r = t.Result;
                LogBox.Text = string.Join("\n", r.Notes);
                BtnBoost.IsEnabled = true;
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Task.Run(() => BoostService.ResetPriorities(_connections))
            .ContinueWith(t => LogBox.Text = t.Result,
                TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
