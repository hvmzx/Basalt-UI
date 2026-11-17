using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using BasaltUI.Core;
using BasaltUI.ViewModels;

namespace BasaltUI;

/// <summary>
/// The app itself: one basalt controller, one poller, one main window, one tray icon. Everything else is a view.
/// </summary>
public partial class App : Application
{
    private static Mutex? singleLock;

    // Whether this process is the copy that owns the name. Only the owner may release it: the handle is still
    // real in a second copy, and ReleaseMutex on a handle this thread never acquired throws
    // "Object synchronization method was called from an unsynchronized block of code."
    private static bool ownsSingleLock;
    public static new App Current => (App)Application.Current;

    public AppSettings Settings { get; }
    public BasaltClient Client { get; }
    public StatusPoller Poller { get; }
    public BasaltProcess Server { get; } = new();
    public MainViewModel Vm { get; }
    public ConfigDocument? Config { get; private set; }
    public DownloadCenter Downloads { get; }

    MainWindow? window;
    Shell.TrayIcon? tray;
    bool exiting;

    public App()
    {
        // A failure that happens while the window is being put up would otherwise be a Windows error dialog
        // and nothing on disk: the log line is the only way to find out afterwards what stopped it. The app
        // carries on rather than dying over a control it cannot draw.
        DispatcherUnhandledException += (_, a) =>
        {
            a.Handled = true;
            AppPaths.Log("unhandled: " + a.Exception);
            TellOnce(a.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            AppPaths.Log("fatal: " + (a.ExceptionObject is Exception f ? f.ToString() : a.ExceptionObject?.ToString()));

        Settings = AppSettings.Load();
        Client = new BasaltClient();
        Poller = new StatusPoller(Client);
        Downloads = new DownloadCenter(Settings);
        Vm = new MainViewModel(Settings, Server, Client, Poller, () => Config, ReloadConfig, AppPaths.Log);
    }

    // ---- theme ------------------------------------------------------------------------------------------
    /// <summary>Which token file is merged. Windows' own apps setting decides, as the system apps do.</summary>
    public static bool IsDarkTheme { get; private set; } = true;

    /// <summary>
    /// Follows Windows: Settings → Personalisation → Colours → "Choose your mode" for apps. Read before any
    /// window is built, and never re-read — a restart picks up a change, which is what most settings-bearing
    /// apps do and what keeps the tray, the balloon and the windows in agreement.
    /// </summary>
    private void PickTheme()
    {
        IsDarkTheme = true;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light)
                IsDarkTheme = light == 0;
        }
        catch
        {
            // an unreadable registry is not worth a dialog; dark is the default
        }

        Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri(IsDarkTheme
                ? "pack://application:,,,/Themes/Dark.xaml"
                : "pack://application:,,,/Themes/Light.xaml"),
        };
    }

    // ---- startup ------------------------------------------------------------------------------------------
    protected override void OnStartup(StartupEventArgs e)
    {
        PickTheme();

        singleLock = new Mutex(true, @"Local\BasaltUI.SingleInstance", out ownsSingleLock);
        if (!ownsSingleLock)
        {
            // Another copy is the one that owns the name and the port. Bring its window forward instead of
            // opening a second tray icon beside it.
            if (!WakeOtherInstance())
            {
                AppPaths.Log("another copy was already running, with no window on screen to raise");
                MessageBox.Show("Basalt UI is already running — its icon is by the clock, in the tray.\n\n" +
                                "Use that one rather than a second copy.",
                                "Basalt UI", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            Shutdown();
            return;
        }

        base.OnStartup(e);

        Server.ConsoleLine += line => Vm.Append(line, LogSource.Server);
        Server.Notice += line => Vm.Append(line, LogSource.App);
        Vm.Notice += ShowNotice;
        Vm.SettingsOpened += ShowSettings;
        Vm.TakeOverRequested += TakeOverForeign;
        Poller.StatusChanged += OnPollerStatus;
        Poller.ServerLogLines += lines =>
        {
            // a server started elsewhere has no console we can read, so its own log buffer fills the same pane
            foreach (var line in lines)
                Vm.Append(line, LogSource.Server);
            Vm.ExplainServerTrouble(lines);
        };

        ReloadConfig();
        Vm.ReapplyPolling();
        Net.Configure(Settings.ProxyUrl);

        // What was on its way when the app last went away goes back on its way now — the user never
        // asked it to stop, so the app does not treat a restart as one either.
        Downloads.LoadSaved();

        // Where the app keeps its own files is the one thing that has to go right for it to remember
        // anything, and for a long time it could fail with nothing on screen to show for it.
        if (AppPaths.Trouble is { } trouble)
            Vm.Append("app data: cannot write in the usual place — " + trouble + "; settings will not survive a restart",
                      LogSource.Error);

        AppPaths.Log($"started: pid {Environment.ProcessId}, exe {Environment.ProcessPath}, files in {AppPaths.Root}");

        window = new MainWindow(this);

        var minimised = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) ||
                        Settings.StartMinimized;
        if (Settings.WindowMaximised && !minimised)
            window.WindowState = WindowState.Maximized;
        else if (minimised)
            window.WindowState = WindowState.Minimized;
        window.Show();

        // The tray icon goes up here, not earlier in OnStartup: an icon registered before the message
        // pump is running is one Windows 11 sometimes silently drops, and the app with no icon is one
        // only Task Manager can close. A few beats in, and again after that, the registration is
        // re-asserted — the standard recovery, and a no-op once the icon is properly in place.
        tray = new Shell.TrayIcon(Vm, ShowFromTray, ShowSettings, Quit);
        if (AppPaths.Trouble is not null)
            tray.Balloon("Basalt UI", "It cannot write where its settings should live — the log pane says why.");
        var reassert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        var reasserted = 0;
        reassert.Tick += (_, _) =>
        {
            tray.EnsureRegistered();
            if (++reasserted >= 3)
                reassert.Stop();
        };
        reassert.Start();

        // One beat right away, even if we did not start anything: a server may already be answering on the
        // port from a terminal, or from an app window that was closed to the tray long ago.
        _ = FirstBeatAsync();

        if (Settings.StartServerOnLaunch && !Server.IsAlive)
            _ = Vm.StartAsync();

        Poller.Start(Math.Clamp(Settings.PollMs, 250, 60_000));
    }

    private async Task FirstBeatAsync()
    {
        await Poller.BeatAsync();
        var status = Poller.Current;
        if (status is { Reachable: true } && !Server.IsAlive)
        {
            var pids = Array.Empty<int>();
            try
            {
                pids = NativeMethods.ListenersOnPort((ushort)(Config?.Port ?? 8080));
            }
            catch (Exception ex)
            {
                AppPaths.Log("port scan: " + ex.Message);
            }
            Vm.SetForeign(pids);
        }
    }

    /// <summary>The user agreed to control a server they started elsewhere: watch it, but never force-kill it.</summary>
    private void TakeOverForeign()
    {
        var pid = Vm.ForeignPids.FirstOrDefault();
        if (pid == 0)
            return;
        Vm.AdoptExternal(pid);
        // the banner's question has been answered; leaving it up reads as if the takeover never happened
        Vm.SetForeign([]);
        Vm.Append($"app: watching the server on port {Config?.Port ?? 8080} (pid {pid}) that Basalt UI did not start", LogSource.App);
    }

    private bool webUiShown;

    /// <summary>
    /// The one browser this app opens by itself: a model we started has finished loading and the user asked for
    /// the web UI on ready. A server that was already up when the app launched is not ours to browse to.
    /// </summary>
    private void OnPollerStatus(ServerStatus status)
    {
        if (webUiShown || !Settings.OpenWebUiOnReady || !status.Reachable || !status.Loaded || !Server.StartedByUs)
            return;
        webUiShown = true;
        Dispatcher.InvokeAsync(() => Vm.OpenUrl(Vm.Url + "/"));
    }

    // ---- config ------------------------------------------------------------------------------------------
    public void ReloadConfig()
    {
        try
        {
            Config = File.Exists(Settings.ConfigPath) ? ConfigDocument.Load(Settings.ConfigPath) : null;
            if (Config is null)
                Vm?.Append("no config file at " + Settings.ConfigPath + " — pick one in Settings", LogSource.App);
        }
        catch (Exception ex)
        {
            Config = null;
            Vm?.Append("could not read " + Settings.ConfigPath + ": " + ex.Message, LogSource.Error);
        }
    }

    /// <summary>After the editor wrote the file: re-read it and re-point polling at whatever it now says.</summary>
    public void AfterConfigSaved()
    {
        ReloadConfig();
        Vm.ReapplyPolling();
        Vm.Append("config saved — " + Path.GetFileName(Settings.ConfigPath), LogSource.App);
    }

    public void UseConfigFile(string path)
    {
        Settings.ConfigPath = path;
        Settings.Save();
        ReloadConfig();
        Vm.ReapplyPolling();
    }

    // ---- windows and tray -------------------------------------------------------------------------------
    public void ShowFromTray()
    {
        if (window is null)
            return;
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        if (!window.IsVisible)
            window.Show();
        window.Activate();
    }

    public void ShowSettings()
    {
        ShowFromTray();
        window?.OpenSettings();
    }

    /// <summary>The Hugging Face browser: the same kind of window Settings is, and the same rule about owners.</summary>
    public void ShowModelsBrowser()
    {
        ShowFromTray();
        new ModelsBrowserWindow(this) { Owner = window }.ShowDialog();
    }

    /// <summary>
    /// The engine's dashboard inside the app. One window serves both the page and /health; opening it
    /// again just brings the one on screen forward and points it at the new address.
    /// </summary>
    private WebUiWindow? webUi;

    public void ShowWebUi(string url)
    {
        ShowFromTray();
        if (webUi is not { IsLoaded: true })
            webUi = new WebUiWindow(this, url) { Owner = window };
        else
            webUi.Navigate(url);
        webUi.Show();
        webUi.Activate();
    }

    private void ShowNotice(string text)
    {
        Dispatcher.InvokeAsync(() =>
        {
            Vm.Append("app: " + text, LogSource.App);
            tray?.Balloon("Basalt UI", text);
            AppPaths.Log("notice: " + text);
        });
    }

    /// <summary>Tray → Quit. Honours “stop the server when the app exits”, asking first when a server is up.</summary>
    public void Quit()
    {
        if (exiting)
            return;

        var stopServer = Settings.StopServerOnAppExit && Server.IsAlive;
        if (stopServer)
        {
            var answer = MessageBox.Show(window,
                "Basalt is still serving models.\n\nStop it, or leave it running and just close this window?",
                "Quit Basalt UI", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
            stopServer = answer == MessageBoxResult.Yes;
        }

        exiting = true;
        tray?.Hide();
        Poller.Stop();

        if (!stopServer)
        {
            Shutdown();
            return;
        }
        _ = StopThenShutDownAsync();
    }

    private async Task StopThenShutDownAsync()
    {
        try
        {
            await Server.StopAsync(Math.Clamp(Settings.StopGraceSeconds, 5, 600));
        }
        catch (Exception ex)
        {
            AppPaths.Log("stop on exit: " + ex.Message);
        }
        Dispatcher.Invoke(Shutdown);
    }

    /// <summary>The X button: hide to the tray unless this is a real exit.</summary>
    public bool WindowMayClose() => exiting || !Settings.MinimiseToTrayOnClose;

    public void Notify(string text) => ShowNotice(text);

    public void SetAutostart(bool on)
    {
        var error = Autostart.Set(on, Settings.StartMinimized);
        if (error is not null)
        {
            ShowNotice("autostart: " + error);
            return;
        }
        Settings.RunAtLogon = on;
        Settings.Save();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (window is not null && window.WindowState != WindowState.Minimized)
        {
            Settings.WindowLeft = window.Left;
            Settings.WindowTop = window.Top;
            Settings.WindowWidth = window.Width;
            Settings.WindowHeight = window.Height;
            Settings.WindowMaximised = window.WindowState == WindowState.Maximized;
        }
        Settings.Save();

        // The session under way is history once the app is gone, and the file carries the open session's
        // running counts so the next run continues the count instead of starting it over.
        Vm.CloseSessions();

        Downloads.StopForExit();
        Poller.Dispose();
        tray?.Dispose();
        Server.Dispose();
        if (ownsSingleLock)
        {
            try
            {
                singleLock?.ReleaseMutex();
            }
            catch (Exception ex)
            {
                // Worth a line in the log, never worth taking the exit down over: the handle goes with the
                // process either way.
                AppPaths.Log("single-instance lock: " + ex.Message);
            }
        }

        singleLock?.Dispose();
        base.OnExit(e);
    }

    // ---- complaints -----------------------------------------------------------------------------------------
    readonly Dictionary<string, int> shouted = new();
    int dialogs;

    /// <summary>
    /// A failure that fires on every layout pass should be said once, not screamed continuously: the first
    /// occurrence of each kind gets a dialog (three at most per run), and every one of them goes in the log.
    /// </summary>
    private void TellOnce(string message)
    {
        var key = message.Trim();
        var seen = shouted.TryAdd(key, 1) ? 1 : ++shouted[key];
        if (seen > 1 || dialogs >= 3)
            return;

        dialogs++;
        var where = AppPaths.Trouble is null
            ? "The whole message is in\n" + AppPaths.LogFile
            : "It cannot write its own files either, so the log may be empty:\n" + AppPaths.Trouble;

        MessageBox.Show($"Basalt UI hit a problem and skipped it.\n\n{message}\n\n" + where,
                        "Basalt UI", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    // ---- single instance ---------------------------------------------------------------------------------
    /// <summary>Clicking the shortcut again should show the window that is already there.</summary>
    private static bool WakeOtherInstance()
    {
        try
        {
            var mine = Environment.ProcessId;
            foreach (var other in Process.GetProcessesByName("BasaltUI"))
            {
                if (other.Id == mine)
                    continue;
                var hwnd = FirstTopWindow(other.Id);
                if (hwnd != IntPtr.Zero)
                {
                    ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("wake other copy: " + ex.Message);
        }

        return false;
    }

    private static IntPtr FirstTopWindow(int pid)
    {
        var found = IntPtr.Zero;
        var callback = new EnumWindowsProc((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != pid || !IsWindowVisible(hwnd) || GetWindowTextLengthW(hwnd) == 0)
                return true;
            found = hwnd;
            return false;
        });
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    private const int SW_RESTORE = 9;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr l);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
