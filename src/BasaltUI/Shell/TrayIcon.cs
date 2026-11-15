using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BasaltUI.Core;
using BasaltUI.ViewModels;
using WF = System.Windows.Forms;

namespace BasaltUI.Shell;

/// <summary>
/// The system-tray icon and its menu: start, stop, restart, load or unload the model. The icon itself
/// is WinForms' NotifyIcon, which is part of the desktop runtime, so the app needs no extra packages —
/// but the menu that opens on a right-click is a WPF ContextMenu in the app's own style (a rounded,
/// shadowed sheet with pill items), not the system-drawn strip Windows has shipped since 1995.
/// The menu is rebuilt every time it opens, so each item's enabled state is read fresh off the view model.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon icon;
    private readonly MainViewModel vm;
    private readonly Action showWindow;
    private readonly Action openSettings;
    private readonly Action quit;

    public TrayIcon(MainViewModel vm, Action showWindow, Action openSettings, Action quit)
    {
        this.vm = vm;
        this.showWindow = showWindow;
        this.openSettings = openSettings;
        this.quit = quit;

        icon = new WF.NotifyIcon
        {
            Icon = IconFactory.Tray(RunState.Stopped),
            Visible = true,
            Text = "Basalt — stopped",
        };
        AppPaths.Log("tray: NotifyIcon created, Visible=true");
        icon.DoubleClick += (_, _) => showWindow();
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left)
                showWindow();
        };
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Right)
                ShowMenu();
        };

        vm.PropertyChanged += (_, _) => Refresh();
        Application.Current?.Dispatcher.InvokeAsync(Refresh);
    }

    // ---- the menu ------------------------------------------------------------------------------------------

    private void ShowMenu()
    {
        var d = Application.Current?.Dispatcher;
        if (d is null)
            return;
        if (!d.CheckAccess())
        {
            d.InvokeAsync(ShowMenu);
            return;
        }

        // A WPF ContextMenu only dismisses on an outside click while its placement target is the active
        // window, and there is no real window behind a tray click — so a zero-sized transparent helper
        // window is shown, activated, and closed again the moment the menu goes away.
        var cursor = WF.Cursor.Position;
        var helper = new Window
        {
            Width = 0,
            Height = 0,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = true,
            Left = cursor.X,
            Top = cursor.Y,
        };
        var menu = BuildMenu();
        menu.Closed += (_, _) => helper.Close();
        helper.Loaded += (_, _) =>
        {
            menu.PlacementTarget = helper;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = cursor.X;
            menu.VerticalOffset = cursor.Y;
            menu.IsOpen = true;
        };
        helper.Show();
    }

    private ContextMenu BuildMenu()
    {
        var connected = vm.IsConnected;
        var menu = new ContextMenu();

        menu.Items.Add(Item("Open Basalt UI", "\uE80F", bold: true, true, showWindow));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("Start basalt", "\uE768", false, !vm.IsRunning, () => _ = vm.StartAsync()));
        menu.Items.Add(Item("Restart", "\uE72C", false, vm.IsRunning, () => _ = vm.RestartAsync()));
        menu.Items.Add(Item("Stop basalt", "\uE71A", false, vm.IsRunning, () => _ = vm.StopAsync()));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("Load model", "\uE896", false, connected && !vm.Status.Loaded,
                            () => _ = vm.ControlAsync("/load", null, "load")));
        menu.Items.Add(Item("Unload model", "\uE898", false, connected && vm.Status.Loaded,
                            () => _ = vm.ControlAsync("/unload", null, "unload")));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("Open web UI", "\uE774", false, connected, () => vm.OpenUrl(vm.Url + "/")));
        menu.Items.Add(Item("Open stats JSON", "\uE943", false, connected, () => vm.OpenUrl(vm.Url + "/metrics")));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("Settings…", "\uE713", false, true, openSettings));
        menu.Items.Add(Separator());
        menu.Items.Add(Item("Quit", "\uE7E8", false, true, quit));
        return menu;
    }

    private static MenuItem Item(string text, string glyph, bool bold, bool enabled, Action action)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };

        if (glyph.Length > 0)
            header.Children.Add(new TextBlock
            {
                FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["FontIcon"],
                FontSize = 12.5,
                Text = glyph,
                Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextSecondary"],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            });

        header.Children.Add(new TextBlock
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        });

        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    private static Separator Separator() => new()
    {
        Style = Application.Current?.TryFindResource("MenuSeparator") as Style,
    };

    // ---- the icon itself -----------------------------------------------------------------------------------

    private RunState shownState = (RunState)(-1);

    private void Refresh()
    {
        // The poller raises PropertyChanged from its own thread, and WinForms wants this on ours.
        var d = Application.Current?.Dispatcher;
        if (d is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.InvokeAsync(Refresh);
            return;
        }

        var state = vm.IsConnected ? RunState.Running : vm.IsRunning ? RunState.Starting : RunState.Stopped;
        if (vm.Status.Loading)
            state = RunState.Starting;
        if (state != shownState)
        {
            shownState = state;
            icon.Icon = IconFactory.Tray(state);   // the same handle per state; no churn for the shell to digest
        }
        icon.Text = Shorten($"Basalt — {vm.RunStateText}" +
                            (string.IsNullOrWhiteSpace(vm.Status.ModelName) ? "" : $" · {vm.Status.ModelName}"));
    }

    /// <summary>The tray tooltip is capped by Windows at 63 characters.</summary>
    private static string Shorten(string text) => text.Length <= 63 ? text : text[..60] + "…";

    public void Balloon(string title, string body)
    {
        try
        {
            icon.ShowBalloonTip(3500, title, body, WF.ToolTipIcon.Info);
        }
        catch
        {
            // a balloon is never worth a crash
        }
    }

    public void Hide() => icon.Visible = false;

    /// <summary>
    /// Ask the shell again, in case the first registration was dropped — Windows 11 sometimes loses an
    /// icon that was added while its process was still starting up. Toggling Visible re-adds it; when
    /// the icon was never lost, the shell just accepts a second add with nothing to show for it.
    /// </summary>
    public void EnsureRegistered()
    {
        try
        {
            icon.Visible = false;
            icon.Visible = true;
            AppPaths.Log("tray: re-asserted");
        }
        catch
        {
            // the tray is worth less than the app; never a crash over it
        }
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
