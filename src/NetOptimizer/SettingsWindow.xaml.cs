using System.Windows;
using NetOptimizer.Services;

namespace NetOptimizer;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeHelper.SetTitleBar(this, ThemeService.Current == ThemeService.Dark);
        UpdateThemeButtons();
    }

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

    private void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        var res = MessageBox.Show(
            "Удалить NetOptimizer с компьютера?\n\n" +
            "Будут удалены сам файл программы, вспомогательные файлы и настройки. " +
            "Приложение закроется. Это действие необратимо.",
            "Удаление программы", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (res != MessageBoxResult.Yes) return;

        var confirm = MessageBox.Show(
            "Точно удалить? Отменить будет нельзя.",
            "Подтверждение", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirm == MessageBoxResult.Yes)
            SettingsService.Uninstall();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
