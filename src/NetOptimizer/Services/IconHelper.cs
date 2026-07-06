using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace NetOptimizer.Services;

/// <summary>
/// Extracts small icons from executable files and caches them (keyed by path).
/// Returned images are frozen, so they are safe to build on a background thread.
/// </summary>
public static class IconHelper
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new();

    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        return Cache.GetOrAdd(path, Extract);
    }

    private static ImageSource? Extract(string path)
    {
        try
        {
            var info = new NativeMethods.SHFILEINFO();
            IntPtr res = NativeMethods.SHGetFileInfo(
                path, 0, ref info,
                (uint)System.Runtime.InteropServices.Marshal.SizeOf(info),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_SMALLICON);

            if (info.hIcon == IntPtr.Zero)
                return null;

            try
            {
                var img = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                img.Freeze();
                return img;
            }
            finally
            {
                NativeMethods.DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }
}
