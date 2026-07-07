using System.Windows;

namespace NetOptimizer.Services;

public static class ThemeService
{
    public const string Dark = "dark";
    public const string Light = "light";

    public static string Current { get; private set; } = Dark;

    /// <summary>Swaps the merged theme dictionary; DynamicResource brushes update live.</summary>
    public static void Apply(string theme)
    {
        Current = theme == Light ? Light : Dark;
        string file = Current == Light ? "Light" : "Dark";

        var dict = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{file}.xaml", UriKind.Absolute)
        };

        var merged = Application.Current.Resources.MergedDictionaries;
        if (merged.Count == 0) merged.Add(dict);
        else merged[0] = dict;

        // Update native title bars of all open windows.
        foreach (Window w in Application.Current.Windows)
            ThemeHelper.SetTitleBar(w, Current == Dark);
    }
}
