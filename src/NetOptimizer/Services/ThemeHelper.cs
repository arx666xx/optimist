using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NetOptimizer.Services;

public static class ThemeHelper
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Makes the native window title bar dark (Windows 10 20H1+/11).</summary>
    public static void EnableDarkTitleBar(Window window)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            int enabled = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        }
        catch { /* older Windows — ignore */ }
    }
}
