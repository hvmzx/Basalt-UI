using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using BasaltUI.Core;
using BasaltUI.ViewModels;
using Microsoft.Win32;

namespace BasaltUI;

public partial class MainWindow : Window
{
    private readonly App app;
    private MainViewModel Vm => app.Vm;

    private FlowDocument logDocument = null!;
    private ScrollViewer? logScroller;
    private bool followTail = true;
    private bool toldAboutTray;

    public MainWindow(App app)
    {
        this.app = app;
        InitializeComponent();
        DataContext = Vm;

        ApplySavedBounds();
        SourceInitialized += (_, _) =>
        {
            ApplySavedBounds();
            Shell.Win11.Round(this, App.IsDarkTheme);
        };

        // The console: a read-only document — select and copy anything, edit nothing. Lines are appended
        // as they arrive so the document mirrors Vm.Log without re-measuring the whole history each time.
        logDocument = new FlowDocument
        {
            PagePadding = new Thickness(0),
            ColumnWidth = double.PositiveInfinity,          // one column, as wide as the pane
            FontFamily = (FontFamily)TryFindResource("FontMono")!,
            FontSize = 12,
        };
        LogView.Document = logDocument;
        LogView.Loaded += (_, _) =>
        {
            logScroller = FindScrollViewer(LogView);
            if (logScroller is not null)
                logScroller.ScrollChanged += (_, _) =>
                    followTail = logScroller.ScrollableHeight - logScroller.VerticalOffset < 24;
        };
        foreach (var line in Vm.Log)
            logDocument.Blocks.Add(LineFor(line));

        Vm.Log.CollectionChanged += OnLogChanged;
        Vm.ModelsBrowserRequested += app.ShowModelsBrowser;
        Vm.WebUiRequested += app.ShowWebUi;

        // The downloads centre link in the footer shows whether anything is on its way.
        app.Downloads.ListChanged += RefreshDownloadsLink;
        app.Downloads.ItemChanged += _ => RefreshDownloadsLink();
        RefreshDownloadsLink();
        ModelsList.ContextMenuOpening += ModelRowMenu_Opening;

        SetRequestsCollapsed(Vm.Settings.RecentRequestsCollapsed);
    }

    /// <summary>The footer's centre word: the count when something is coming down.</summary>
    private void RefreshDownloadsLink()
    {
        var active = app.Downloads.ActiveCount;
        DownloadsLink.Text = active == 0 ? "Downloads" : $"Downloads · {active} on the way";
        if (page == Page.Downloads)
            DownloadsPanel.RefreshSummary();
    }

    private void Downloads_Click(object sender, RoutedEventArgs e) => OpenDownloads();

    // ---- geometry ------------------------------------------------------------------------------------------
    private void ApplySavedBounds()
    {
        var s = app.Settings;
        if (s.WindowLeft is not { } savedLeft || s.WindowTop is not { } savedTop)
            return;

        // a monitor that used to exist is the usual reason for an off-screen window
        var left = Math.Max(SystemParameters.VirtualScreenLeft, Math.Min(savedLeft,
                       SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 120));
        var top = Math.Max(SystemParameters.VirtualScreenTop, Math.Min(savedTop,
                      SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80));

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = Math.Clamp(s.WindowWidth, MinWidth, SystemParameters.VirtualScreenWidth);
        Height = Math.Clamp(s.WindowHeight, MinHeight, SystemParameters.VirtualScreenHeight);
    }

    // ---- pages: the dashboard, and settings / downloads / release notes over it -------------------------------
    private enum Page { Main, Settings, Downloads, Notes }

    private Page page = Page.Main;

    private void ShowPage(Page which)
    {
        page = which;
        if (which == Page.Settings)
            SettingsPanel.OnShown();
        if (which == Page.Downloads)
            DownloadsPanel.OnShown();
        SettingsPanel.Visibility = which == Page.Settings ? Visibility.Visible : Visibility.Collapsed;
        DownloadsPanel.Visibility = which == Page.Downloads ? Visibility.Visible : Visibility.Collapsed;
        NotesPage.Visibility = which == Page.Notes ? Visibility.Visible : Visibility.Collapsed;
        PageMain.Visibility = which == Page.Main ? Visibility.Visible : Visibility.Collapsed;
        MainTitle.Visibility = which == Page.Main ? Visibility.Visible : Visibility.Collapsed;
        MainActions.Visibility = which == Page.Main ? Visibility.Visible : Visibility.Collapsed;
        PageTitle.Visibility = which == Page.Main ? Visibility.Collapsed : Visibility.Visible;
        if (which != Page.Main)
            PageTitleText.Text = which switch
            {
                Page.Settings => "Settings",
                Page.Downloads => "Downloads",
                _ => "Release notes",
            };
    }

    /// <summary>The window is one window: settings is a page over the dashboard, and the arrow takes you back.</summary>
    public void OpenSettings() => ShowPage(Page.Settings);

    /// <summary>The downloads centre, the same kind of page: the arrow takes you back.</summary>
    public void OpenDownloads() => ShowPage(Page.Downloads);

    /// <summary>The release notes, the same kind of page: read in the app, the arrow takes you back.</summary>
    public void OpenNotes(BasaltRelease release)
    {
        NotesPage.Show(app, release);
        ShowPage(Page.Notes);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (page == Page.Settings && !SettingsPanel.TryLeave())
            return;
        ShowPage(Page.Main);
    }

    // ---- the window's own chrome buttons ---------------------------------------------------------------------
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaxButton is not null)
            MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    // ---- closing ------------------------------------------------------------------------------------------
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!app.WindowMayClose())
        {
            e.Cancel = true;
            Hide();
            if (!toldAboutTray)
            {
                toldAboutTray = true;
                Vm.Append("app: hidden to the tray — right-click the icon to stop basalt or quit", LogSource.App);
            }
            return;
        }
        base.OnClosing(e);
    }

    // ---- the log pane --------------------------------------------------------------------------------------
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems is null)
                    return;
                foreach (LogLine line in e.NewItems)
                    logDocument.Blocks.Add(LineFor(line));
                break;

            case NotifyCollectionChangedAction.Remove:
                if (e.OldItems is null)
                    return;
                for (var i = 0; i < e.OldItems.Count; i++)
                {
                    if (logDocument.Blocks.FirstBlock is { } first)
                        logDocument.Blocks.Remove(first);
                }
                break;

            default:                                    // a filter change resets the collection
                logDocument.Blocks.Clear();
                foreach (var line in Vm.Log)
                    logDocument.Blocks.Add(LineFor(line));
                break;
        }

        if (followTail)
            Dispatcher.BeginInvoke(() => logScroller?.ScrollToEnd());
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                return sv;
            if (FindScrollViewer(child) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>One console line: the faint mono time, then the text in the colour of its source.</summary>
    private static Paragraph LineFor(LogLine line)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
        var time = new Run(line.At.ToString("HH:mm:ss") + "  ") { FontSize = 11 };
        time.SetResourceReference(Run.ForegroundProperty, "TextFaint");
        paragraph.Inlines.Add(time);
        var text = new Run(line.Text);
        text.SetResourceReference(Run.ForegroundProperty,
            line.Source == LogSource.Error ? "Bad" :
            line.Source == LogSource.App ? "Accent" : "TextSecondary");
        paragraph.Inlines.Add(text);
        return paragraph;
    }

    // ---- buttons ------------------------------------------------------------------------------------------
    private void TakeOver_Click(object sender, RoutedEventArgs e) => Vm.TakeOverForeign();

    private void IgnoreForeign_Click(object sender, RoutedEventArgs e) => Vm.IgnoreForeign();

    private void SwitchModel_Click(object sender, RoutedEventArgs e) => SwitchSelected();

    private void ModelDouble_Click(object sender, RoutedEventArgs e) => SwitchSelected();

    private void SwitchSelected()
    {
        // A switch is an HTTP call to a server that has to be answering; with none there is nothing to
        // tell, and the button beside this list is already showing that.
        if (ModelsList.SelectedItem is not HomeModelRow row)
            return;
        if (!row.Live)
        {
            _ = Vm.OpenLocalModel(row.Id);   // a folder file: switch it in, or start basalt when the config already points at it
            return;
        }
        if (!Vm.IsConnected)
            return;
        if (row.Loaded)
            return;
        _ = Vm.SwitchModelAsync(row.Id);
    }

    // ---- the version dot ---------------------------------------------------------------------------------------
    private async void UpdateDot_Click(object sender, RoutedEventArgs e)
    {
        // Green: the click is a manual check. Yellow: the click is the update itself.
        if (Vm.UpdateAvailable)
        {
            Update_Click(sender, e);
            return;
        }
        await Vm.CheckForUpdateAsync(manual: true);
    }

    // ---- a newer basalt ----------------------------------------------------------------------------------------
    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        // Whether a server has to be stopped, and whose it is, decides the wording. The plan is looked
        // at again inside the update itself; this pass is only for putting the question honestly.
        var plan = Vm.PeekUpdatePlan();
        var tag = Vm.Latest?.Tag ?? "the new version";

        if (plan.Blocked)
        {
            Shell.Dialog.Info(this, "Update basalt", plan.Question(tag));
            return;
        }

        if (plan.Answers)
        {
            var what = plan.AskFirst
                ? $"The server answering on port {plan.Port} (pid {plan.Pid}) was not started by Basalt UI, and Windows cannot replace its files while it runs.\n\n" +
                  $"Basalt will be asked to stop politely (the Ctrl+C it listens for), {tag} will be put in place, and then you choose whether to start it again from here.\n\nContinue?"
                : $"Basalt is going to be terminated so {tag} can be installed: the server stops, the new files go in, and then you choose whether to start it again.\n\nContinue?";
            if (!Shell.Dialog.Confirm(this, "Update basalt", what))
                return;
        }

        UpdateButton.IsEnabled = false;
        DismissButton.IsEnabled = false;
        var wasRunning = plan.Answers;
        try
        {
            var error = await Vm.UpdateBasaltAsync();
            if (error.Length > 0)
            {
                app.Notify("could not update basalt: " + error);
                return;
            }
        }
        finally
        {
            UpdateButton.IsEnabled = true;
            DismissButton.IsEnabled = true;
        }

        // The update is done and the server, if there was one, is off. Starting again is the user's call.
        if (wasRunning)
        {
            if (Shell.Dialog.Confirm(this, "Update installed", $"{tag} is installed. Start basalt again now?",
                                     confirm: "Start again", cancel: "Not now"))
                _ = Vm.StartAsync();
        }
        else
            app.Notify($"{tag} is installed — press Start when you want it running");
    }

    /// <summary>The notes, as a page over the dashboard; the GitHub page stays reachable from inside it.</summary>
    private void UpdateNotes_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Latest is { } release)
            OpenNotes(release);
        else
            Vm.OpenReleasePage();   // the notes have not arrived yet; the page is still worth a try
    }

    private void DismissUpdate_Click(object sender, RoutedEventArgs e) => Vm.UpdateDismiss();

    // ---- models on Hugging Face ---------------------------------------------------------------------------------
    private void BrowseModels_Click(object sender, RoutedEventArgs e) => Vm.OpenModelsBrowser();

    // ---- the request table folds away, and the fold is remembered ---------------------------------------
    /// <summary>
    /// Fold the recent-requests table away — the card's one line stays — or bring it back. The chevron
    /// always points the way it is about to go, and the choice is the app's memory: the next run opens
    /// with the same eye it closed with.
    /// </summary>
    public void SetRequestsCollapsed(bool collapsed)
    {
        if (RequestsBody is null)
            return;
        RequestsBody.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        RequestsToggleGlyph.Text = collapsed ? "\uE70D" : "\uE70E";
    }

    private void RequestsToggle_Click(object sender, RoutedEventArgs e)
    {
        SetRequestsCollapsed(RequestsBody.Visibility != Visibility.Collapsed);
        Vm.Settings.RecentRequestsCollapsed = RequestsBody.Visibility == Visibility.Collapsed;
        Vm.Settings.Save();
    }

    /// <summary>
    /// One model's history: every session of basalt the app watched it run, newest first. The window asks
    /// the view model, which asks the recorder, so the list always says what was actually seen — and the
    /// time the app was away, counted from the engine's own totals, stands as its own ≈ line.
    /// </summary>
    private void History_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { Tag: HomeModelRow row })
            return;
        new ModelHistoryWindow(row, Vm) { Owner = this }.ShowDialog();
    }

    /// <summary>Right-click on a model row: its named config, or the entry itself. The menu is built
    /// here, at open time, because its shape depends on the row: which config it wears, and whether a
    /// file stands behind it at all.</summary>
    private void ModelRowMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        var row = RowOfMenu(e.OriginalSource);
        if (row is null)
        {
            ModelsList.ContextMenu = null;
            return;
        }
        ModelsList.ContextMenu = ModelRowMenu(row);
    }

    private static HomeModelRow? RowOfMenu(object? source)
    {
        DependencyObject? d = source as DependencyObject;
        while (d is not null)
        {
            if (d is FrameworkElement { DataContext: HomeModelRow row })
                return row;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private ContextMenu ModelRowMenu(HomeModelRow row)
    {
        var menu = new ContextMenu();
        var worn = Vm.Profiles.AssignedTo(row.Id);
        var active = Vm.Profiles.Active;

        if (worn is not null)
        {
            menu.Items.Add(Item($"Edit config “{worn}”…", () => { ShowPage(Page.Settings); SettingsPanel.RevealProfile(worn); },
                                tip: "The Configs tab of Settings, on this config's whole document — its models, server and engine. Saving it rewrites the file when it is the active one."));
            menu.Items.Add(Item($"Detach “{worn}”", () => Vm.AttachProfile(row, null),
                                tip: "Only the link goes — the file is untouched."));
        }

        var attach = new MenuItem
        {
            Header = worn is null ? "Attach a config…" : "Attach a different config…",
            ToolTip = "Attaching applies it: the file is rewritten from the config's document, this model first in line",
        };
        foreach (var name in Vm.Profiles.Names.Where(n => !n.Equals(worn, StringComparison.OrdinalIgnoreCase)))
            attach.Items.Add(Item(name.Equals(active, StringComparison.OrdinalIgnoreCase)
                                      ? "● " + name + "   (in the file now)"
                                      : name, () => Vm.AttachProfile(row, name)));
        if (worn is not null)
            attach.Items.Add(Item("(no config)", () => Vm.AttachProfile(row, null)));
        if (attach.Items.Count == 0)
            attach.Items.Add(Item("(no configs yet)", () => { }, enabled: false,
                                  "Make the first one with “New config for this model…” below"));
        attach.Items.Add(new Separator());
        attach.Items.Add(Item("New config for this model…", () =>
        {
            var name = Vm.NewProfileFor(row);
            ShowPage(Page.Settings);
            SettingsPanel.RevealProfile(name);
        }, tip: "Starts as the config the machine runs now, with this model first in line — a place to diverge from. Attaching is what applies it."));
        menu.Items.Add(attach);

        if (worn is null)
            menu.Items.Add(Item("Edit this entry's own fields…", () => { ShowPage(Page.Settings); SettingsPanel.RevealModel(row); },
                                tip: "The Models tab of Settings, on this entry: gpu, kv, context, vision — with the load preview right below"));
        menu.Items.Add(Item("Show in folder", () => ShowInFolder(row), row.HasFile,
                            tip: "The file this row stands for, selected in the models folder"));
        return menu;
    }

    private static MenuItem Item(string header, Action click, bool enabled = true, string? tip = null)
    {
        var item = new System.Windows.Controls.MenuItem
        { Header = header, IsEnabled = enabled, ToolTip = tip };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Right-click on a model row that stands for a file: the folder with the file selected,
    /// the same place the config entry points at.</summary>
    private void ShowInFolder(HomeModelRow row)
    {
        var file = Vm.LocalFileOf(row);
        if (file is null || !File.Exists(file))
            return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"/select,\"{file}\"",
            UseShellExecute = true,
        });
    }

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the console",
            Filter = "Text file|*.txt|All files|*.*",
            FileName = $"basalt-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        if (dialog.ShowDialog(this) != true)
            return;

        var error = Vm.SaveLogTo(dialog.FileName);
        if (error.Length > 0)
            app.Notify("could not save the log: " + error);
        else
            app.Notify("log saved to " + dialog.FileName);
    }
}
