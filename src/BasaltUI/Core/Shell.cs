using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace BasaltUI.Core;

/// <summary>HKCU “run at logon”, so the tray can be there when Windows starts.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BasaltUI";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static string? Set(bool on, bool startMinimised)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ??
                             Registry.CurrentUser.CreateSubKey(RunKey);
            if (!on)
            {
                if (key.GetValue(ValueName) is not null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                return null;
            }
            var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe))
                return "could not work out this app's own path";
            key.SetValue(ValueName, "\"" + exe + "\"" + (startMinimised ? " --minimized" : ""));
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }
}

/// <summary>
/// The tray and title-bar icon, drawn here rather than shipped: basalt's columns, so the icon is recognisable
/// at 16 px and needs no binary asset in the repository.
/// </summary>
public static class IconFactory
{
    private static Icon? tray;
    private static Icon? trayBusy;
    private static Icon? trayDown;

    private static Icon Build(Color accent, Color dim)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var bg = new SolidBrush(Color.FromArgb(235, 28, 32, 40));
        g.FillPolygon(bg, [
            new Point(9, 2), new Point(23, 2), new Point(30, 16), new Point(23, 30), new Point(9, 30), new Point(2, 16),
        ]);

        const int baseY = 24;
        int[] tops = [14, 9, 5, 9, 14];                      // column heights, tallest in the middle
        var x = 8;
        for (var i = 0; i < tops.Length; i++)
        {
            using var brush = new SolidBrush(i % 2 == 0 ? accent : dim);
            g.FillRectangle(brush, x, baseY - tops[i], 3, tops[i]);
            x += 4;
        }
        using var slab = new SolidBrush(Color.FromArgb(230, 226, 230, 235));
        g.FillRectangle(slab, 6, baseY + 1, 21, 3);

        return Icon.FromHandle(bmp.GetHicon());
    }

    public static Icon Tray(RunState state)
    {
        tray ??= Build(Color.FromArgb(80, 220, 200), Color.FromArgb(48, 150, 140));
        trayBusy ??= Build(Color.FromArgb(255, 196, 77), Color.FromArgb(200, 140, 60));
        trayDown ??= Build(Color.FromArgb(160, 170, 182), Color.FromArgb(112, 122, 134));

        return state switch
        {
            RunState.Running => tray,
            RunState.Starting or RunState.Stopping => trayBusy,
            _ => trayDown,
        };
    }
}
