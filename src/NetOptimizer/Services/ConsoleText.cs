using System.Runtime.InteropServices;
using System.Text;

namespace NetOptimizer.Services;

/// <summary>
/// Console utilities (ipconfig, netsh, shutdown) write their output in the OEM
/// code page — 866 on a Russian Windows, 437 on an English one — not in UTF-8.
/// Reading the redirected stream as UTF-8 turned every Cyrillic line into
/// "����" in the repair log. The real OEM code page is resolved once here and
/// used for both stdout and stderr.
/// </summary>
internal static class ConsoleText
{
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static readonly Lazy<Encoding> Lazy = new(() =>
    {
        try
        {
            // .NET Core ships only a handful of encodings by default; 866 and
            // friends live in the CodePages provider.
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            return Encoding.UTF8;
        }
    });

    /// <summary>Encoding the Windows console uses for program output.</summary>
    public static Encoding Oem => Lazy.Value;
}
