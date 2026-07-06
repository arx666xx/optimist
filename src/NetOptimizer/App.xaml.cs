using System.Windows;
using System.Windows.Threading;

namespace NetOptimizer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "Произошла ошибка:\n\n" + e.Exception.Message,
            "NetOptimizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
