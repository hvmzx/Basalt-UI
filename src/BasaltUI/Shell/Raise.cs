using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace BasaltUI.Shell;

/// <summary>
/// Browsers answer "yes, your page is open" by quietly putting it in a window behind the one you were
/// looking at — which reads as the click having done nothing at all. This watches the screen for a few
/// seconds after a launch and pulls whatever window appeared in that gap to the front: the browser's,
/// almost certainly, but anything else that surfaced gets the same courtesy.
/// </summary>
public static class Raise
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const int SW_RESTORE = 9;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;

    /// <summary>Every visible top-level window that is a real window, not a tooltip or a splash.</summary>
    internal static HashSet<long> Snapshot()
    {
        var seen = new HashSet<long>();
        EnumWindows((h, _) =>
        {
            if (IsWindowVisible(h) && (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0)
                seen.Add(h.ToInt64());
            return true;
        }, IntPtr.Zero);
        return seen;
    }

    /// <summary>Call before the launch with the snapshot; watches for about six seconds afterwards.</summary>
    public static void AfterLaunch(HashSet<long> before)
    {
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 12; attempt++)   // cold-starting browsers are slow to appear
            {
                await Task.Delay(500);

                IntPtr found = IntPtr.Zero;
                EnumWindows((h, _) =>
                {
                    if (IsWindowVisible(h)
                        && (GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) == 0
                        && !before.Contains(h.ToInt64()))
                    {
                        found = h;
                        return false;
                    }
                    return true;
                }, IntPtr.Zero);

                if (found != IntPtr.Zero)
                {
                    if (IsIconic(found))
                        ShowWindow(found, SW_RESTORE);
                    SetForegroundWindow(found);
                    return;
                }
            }
        });
    }
}
