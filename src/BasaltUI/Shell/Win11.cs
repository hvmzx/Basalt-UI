using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BasaltUI.Shell;

/// <summary>
/// Two DWM window attributes the modern look needs once the standard title bar is gone: rounded corners
/// (Windows 11 gives them to windows with frames, not to frameless ones) and the dark frame colour, so
/// the resize border and any residual system chrome do not glow white over a dark app.
/// </summary>
public static class Win11
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Silent by design: an older Windows simply keeps square corners, and nobody is told.</summary>
    public static void Round(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var darkMode = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
            var corner = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch
        {
            // no DWM, no rounding — the window is still a window
        }
    }
}
