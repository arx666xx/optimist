using System.Windows;
using System.Windows.Threading;
using NetOptimizer.Services;

namespace NetOptimizer;

public partial class RebootWindow : Window
{
    private int _remaining;
    private DispatcherTimer? _timer;

    public RebootWindow(int delaySeconds)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeHelper.SetTitleBar(this, ThemeService.Current == ThemeService.Dark);

        _remaining = delaySeconds;

        if (delaySeconds <= 0)
        {
            TitleText.Text = "Требуется перезагрузка";
            MsgText.Text = "Чтобы изменения вступили в силу, перезагрузите компьютер.";
            BtnReboot.Content = "Перезагрузить";
        }
        else
        {
            TitleText.Text = "Автоматическая перезагрузка";
            UpdateMessage();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) =>
            {
                _remaining--;
                if (_remaining <= 0)
                {
                    _timer!.Stop();
                    DoReboot();
                }
                else UpdateMessage();
            };
            _timer.Start();
        }
    }

    private void UpdateMessage()
    {
        var t = TimeSpan.FromSeconds(_remaining);
        MsgText.Text = $"Компьютер перезагрузится через {t:mm\\:ss}.\nНажмите «Отмена», чтобы остаться, или перезагрузите сейчас.";
    }

    private void DoReboot()
    {
        NetworkRepair.Reboot();
        Close();
    }

    private void Reboot_Click(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        DoReboot();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        Close();
    }
}
