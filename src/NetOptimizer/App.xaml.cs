using System.Windows;
using System.Windows.Threading;
using NetOptimizer.Services;

namespace NetOptimizer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeService.Apply(SettingsService.LoadTheme());
        DispatcherUnhandledException += OnUnhandled;
        ActionLog.Info($"Запуск NetOptimizer {UpdateService.Format(UpdateService.CurrentVersion)}.");

        Exit += (_, _) =>
        {
            UsageStats.Flush();
            ActionLog.Info("Завершение работы.");
        };
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Swallowing a crash without a trace is how bugs stay invisible.
        ActionLog.Error("Необработанная ошибка интерфейса", e.Exception);

        MessageBox.Show(
            "Произошла ошибка:\n\n" + e.Exception.Message +
            "\n\nПодробности записаны в журнал:\n" + ActionLog.FilePath,
            "NetOptimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
