using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NetOptimizer.Services;

public static class ThemeHelper
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    /// <summary>Sets the native window title bar to dark or light (Windows 10 20H1+/11).</summary>
    public static void SetTitleBar(Window window, bool dark)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            int value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch { /* older Windows — ignore */ }
    }
}
