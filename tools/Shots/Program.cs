using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BasaltUI;
using BasaltUI.Controls;
using BasaltUI.Core;
using BasaltUI.Shell;
using BasaltUI.ViewModels;

namespace Shots;

/// <summary>
/// The app's windows, drawn without a screen, written to PNG.
///
/// The problem this solves: the app can be built and reasoned about in a place where nobody can look at it.
/// Every window here is the real thing — the shipping assembly, the real theme, real styles, real bindings —
/// so a design pass can be argued over as images instead of as descriptions of what probably appeared. The
/// tool also states what the pixels say, because the model driving it cannot see the pictures it makes.
/// </summary>
internal static class Program
{
    // A window that was never shown has no non-client area, so arranging it at its full Width/Height draws the
    // client a little roomier than it really is. These are Windows 11's defaults at 100% scaling.
    private const double ChromeW = 16;
    private const double ChromeH = 39;

    // 144 dpi: identical layout, more pixels, so small text and 1px borders can actually be judged.
    private const double Zoom = 1.5;

    [STAThread]
    private static int Main(string[] args)
    {
        var light = args.Length > 0 && args[0].Equals("--light", StringComparison.OrdinalIgnoreCase);
        if (light)
            args = args[1..];
        var dir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "basalt-shots");
        Directory.CreateDirectory(dir);

        // Every run is a fresh install: whatever session memory a previous run left in the home folder would
        // otherwise rehydrate as lines in the history window and the frames would differ between runs.
        try { File.Delete(BasaltUI.Core.AppPaths.ThroughputFile); } catch { /* nothing to lose */ }

        var app = new ShotsApp();

        // WPF would otherwise end the Application the moment one of these windows closes, and the next
        // `new MainWindow(...)` then fails inside LoadComponent with "The Application object is being shut
        // down". This tool drives its windows by hand, so only it decides when the app is over.
        app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

        // A remote or headless session gives WPF a graphics tier that will not rasterize a render target
        // through the GPU: the render comes back holding only the root's own background, everything below it
        // missing. The software renderer does not care what the display driver is.
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        // `Application.Run` installs this on the thread it runs on; this tool pumps the dispatcher by hand, so
        // without it an `await` inside the app resumes on a pool thread and the app's own writes to its controls
        // throw "a different thread owns it" — an artefact of the tool that looks exactly like an app bug, and
        // cost one round of chasing one. Same context WPF would have installed, on the same thread.
        System.Threading.SynchronizationContext.SetSynchronizationContext(
            new System.Windows.Threading.DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

        // What the generated entry point would have done: merge the token file (dark unless --light, the same
        // choice App makes from the Windows setting) and the shared style file. Merged here, because from a
        // tool assembly the app's own pack uris are relative to the wrong assembly.
        LoadTheme(app, light);

        var written = 0;
        using var maps = new StreamWriter(Path.Combine(dir, "maps.txt"));
        // Nothing seeded yet: the honest version of what the window looks like on a machine where basalt has
        // never been started, which is where every empty state has to earn its keep. Same App instance, because
        // a second Application would move Application.Current and take the merged theme with it.
        written += Shoot(new MainWindow(app), Path.Combine(dir, "00-cold.png"), maps);

        Seed(app.Vm);

        // The stats chips in the header must be checkable in words too: the bound text, straight from the
        // view model the dashboard reads it from.
        Console.WriteLine($"  stats chips: “{app.Vm.DecodeStatsText}”   “{app.Vm.PrefillStatsText}”");

        // Whether the capture path itself can paint anything this machine will not give a shown window.
        ProbePipeline();

        written += Shoot(new MainWindow(app), Path.Combine(dir, "01-main.png"), maps,
                         ("header", 20, 15, 780, 70),
                         ("buttons", 1020, 15, 620, 60),
                         ("metrics", 30, 185, 930, 280),
                         ("log", 20, 790, 1060, 130));

        // The request table unfolded: the card's own chevron did the work — the card starts folded by
        // default, so the same window is taken once with its eye open. The requests card is the last card
        // down the left column and sits below the fold, so the column is scrolled by arithmetic first.
        var expanded = new MainWindow(app);
        expanded.SetRequestsCollapsed(false);
        AfterLayout = window =>
        {
            var body = Field<System.Windows.Controls.Grid>(window, "RequestsBody");
            var scroll = body is null ? null : Ancestor<ScrollViewer>(body);
            if (body is not null && scroll is not null)
            {
                scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
                Console.WriteLine($"  left column scrolled to the bottom ({scroll.ScrollableHeight:F0} px) to reach the requests card");
            }
            else
                Console.WriteLine("  requests card or its scroll viewer not found");
        };
        written += Shoot(expanded, Path.Combine(dir, "01b-request-rows.png"), maps,
                         ("requests", 30, 330, 960, 400));
        AfterLayout = null;

        // Same window, left column scrolled down to the RAM bar. The dashboard's memory figures are the ones
        // being argued over, and the RAM bar sits under four GPU bars, so at this height it lives below the
        // fold — a screenshot of the unscrolled window cannot show it, and it is not enough to say so. The
        // helper reads the two bars' own text as well, so the figures appear as words in the log too.
        AfterLayout = ShowMachineBars;
        AfterSettled = window =>
        {
            var scroll = FindBar(window, "RAM") is { } bar ? Ancestor<ScrollViewer>(bar) : null;
            Console.WriteLine($"  left column once laid out: offset {scroll?.VerticalOffset:F0} of " +
                              $"{scroll?.ScrollableHeight:F0} scrollable, viewport {scroll?.ViewportHeight:F0}");
        };
        written += Shoot(new MainWindow(app), Path.Combine(dir, "07-machine.png"), maps,
                         ("memory", 40, 90, 940, 300));
        AfterLayout = null;
        AfterSettled = null;

        // settings is a page over the dashboard now, so the capture is the same window with the page up
        var settingsHost = new MainWindow(app);
        settingsHost.OpenSettings();
        written += Shoot(settingsHost, Path.Combine(dir, "02-settings.png"), maps);

        // The Configs tab and its Models sub-tab: the load preview there answers asynchronously, so the
        // capture waits for the card to appear or time out.
        var settingsPanel = Field<SettingsPage>(settingsHost, "SettingsPanel");
        var tabs = Field<System.Windows.Controls.TabControl>(settingsPanel, "Tabs");
        var configTabs = Field<System.Windows.Controls.TabControl>(settingsPanel, "ConfigTabs");
        if (settingsPanel is not null && tabs is not null && configTabs is not null)
        {
            tabs.SelectedIndex = 1;                     // Configs
            configTabs.SelectedIndex = 0;               // its Models sub-tab
            WaitUntil(() => true, 0);
            written += Shoot(settingsHost, Path.Combine(dir, "02a-settings-configs.png"), maps);

            WaitUntil(() => Field<System.Windows.Controls.Border>(settingsPanel, "ConfigPreviewCard")
                              ?.Visibility == Visibility.Visible, 8000);
            var card = Field<System.Windows.Controls.Border>(settingsPanel, "ConfigPreviewCard");
            var verdict = Field<System.Windows.Controls.TextBlock>(settingsPanel, "ConfigPreviewVerdict");
            Console.WriteLine($"  preview card: {card?.Visibility}, verdict: \"{verdict?.Text}\"");

            // what the preview itself says, computed here rather than through the page's async path
            try
            {
                var probe = LoadPreview.ForFile(@"C:\models\Qwen3.8-FN-UD-IQ3_XXS.basalt",
                    new LoadPreview.Options("x.basalt", 0, "int8", 262144, 1, "dynamic", true, 0, @"C:\models"));
                Console.WriteLine($"  probe: ok={probe.Ok} verdict={probe.Verdict}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  probe failed: {ex.GetType().Name}: {ex.Message}");
            }

            // and the page's own method, this time awaited rather than fired and forgotten: the page calls it as
            // `_ = UpdateLoadPreview()`, so an exception inside it vanishes and the card's absence is the only
            // symptom. Here the task's own state is said out loud, the dispatcher is pumped until it finishes,
            // and whatever the page thinks the model path is gets printed beside it.
            try
            {
                var page = settingsPanel.GetType();
                const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic
                                                              | System.Reflection.BindingFlags.Instance;
                var selected = page.GetField("entry", Private)?.GetValue(settingsPanel)
                    as System.Text.Json.Nodes.JsonObject;
                Console.WriteLine($"  page entry: {(selected is null ? "none selected" : selected.ToJsonString())}");

                var task = (System.Threading.Tasks.Task)page.GetMethod("UpdateLoadPreview", Private)!
                    .Invoke(settingsPanel, null)!;
                WaitUntil(() => task.IsCompleted, 10000);
                Console.WriteLine($"  preview task: {task.Status}, card={card?.Visibility} \"{verdict?.Text}\"");
                if (task.Exception?.GetBaseException() is Exception bad)
                    Console.WriteLine($"  preview threw: {bad.GetType().Name}: {bad.Message} @ {bad.TargetSite?.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  invoke failed: {ex.GetType().Name}: {ex.Message}");
            }
            written += Shoot(settingsHost, Path.Combine(dir, "02c-settings-models.png"), maps,
                             ("preview", 40, 700, 920, 240));
        }

        // the models browser: the official packs arrive over the network after the window opens, so give the
        // fetch the same patience a person would — up to twenty seconds, then shoot whatever is there
        var browserShot = new ModelsBrowserWindow(app);
        browserShot.Show();                          // Loaded fires the search; the wait has something to wait on
        Pump();
        WaitUntil(() => Field<System.Windows.Controls.ListBox>(browserShot, "ReposList")?.HasItems == true, 60000);
        Console.WriteLine($"  browser wait done: repos={Field<System.Windows.Controls.ListBox>(browserShot, "ReposList")?.HasItems}, " +
                          $"foot=\"{Field<System.Windows.Controls.TextBlock>(browserShot, "SearchFoot")?.Text}\"");
        written += Shoot(browserShot, Path.Combine(dir, "03-models.png"), maps);

        // The search box hint is the one empty state the pre-filled query hides, so empty the box the way the
        // user would and crop the row it sits in. The field is a x:Name member of the window, hence reflection.
        var hint = new ModelsBrowserWindow(app);
        (typeof(ModelsBrowserWindow)
            .GetField("QueryBox", System.Reflection.BindingFlags.NonPublic
                                 | System.Reflection.BindingFlags.Instance)?.GetValue(hint)
            as System.Windows.Controls.TextBox)?.Clear();
        written += Shoot(hint, Path.Combine(dir, "04-models-hint.png"), maps,
                         ("search", 14, 10, 996, 82));

        // the downloads centre: synthetic transfers so the row states and the bars can be judged —
        // one on its way with real figures, one done, one failed. The centre reads whatever the
        // DownloadCenter holds, so the seeds go straight into its list.
        var center = app.Downloads;
        center.Items.Add(new DownloadItem("jesdga/Qwen3.8-27B-Basalt",
            "https://huggingface.co/jesdga/Qwen3.8-27B-Basalt/resolve/main/Qwen3.8-27B-QUASAR-NVFP4.basalt",
            @"C:\models\Qwen3.8-27B-QUASAR-NVFP4.basalt", 19_700_000_000, "", DateTime.Now.AddMinutes(-14))
        {
            State = "downloading",
            DoneBytes = 2_100_000_000,
            BytesPerSecond = 13_600_000,
        });
        center.Items.Add(new DownloadItem("jesdga/Qwen3.8-8B-Basalt",
            "https://huggingface.co/jesdga/Qwen3.8-8B-Basalt/resolve/main/Qwen3.8-8B.basalt",
            @"C:\models\Qwen3.8-8B.basalt", 8_100_000_000, "", DateTime.Now.AddHours(-3))
        {
            State = "finished",
            DoneBytes = 8_100_000_000,
        });
        center.Items.Add(new DownloadItem("other/tiny-pack",
            "https://huggingface.co/other/tiny-pack/resolve/main/tiny.basalt",
            @"C:\models\tiny.basalt", 1_200_000_000, "", DateTime.Now.AddHours(-5))
        {
            State = "failed",
            Error = "the server would not say how big the file is",
        });
        var downloadsHost = new MainWindow(app);
        downloadsHost.OpenDownloads();
        written += Shoot(downloadsHost, Path.Combine(dir, "08-downloads.png"), maps,
                         ("active", 40, 130, 940, 190),
                         ("history", 40, 330, 940, 220));
        center.Items.Clear();                       // the seeds must not leak into the next shots


        // the app's own question box: built through the private constructor with reflection, shown
        // non-modally for the capture, because ShowDialog would sit here forever waiting for a click
        try
        {
            var dialog = (Dialog)typeof(Dialog)
                .GetConstructor(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                                null, [typeof(Window), typeof(string), typeof(string), typeof(string), typeof(string)], null)!
                .Invoke([null!, "Update basalt",
                         "Basalt is going to be terminated so 0.9.9 can be installed: the server stops, the new files go in, and then you choose whether to start it again.\n\nContinue?",
                         "Continue", "Cancel"]);
            dialog.ShowInTaskbar = false;
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = -32000; dialog.Top = -32000;
            dialog.Show();
            Pump();
            dialog.UpdateLayout();
            written += Shoot(dialog, Path.Combine(dir, "05-dialog.png"), maps);
            dialog.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  dialog shot failed: {ex.GetType().Name}: {ex.Message}");
        }
        maps.Flush();
        Console.WriteLine($"  maps: {Path.Combine(dir, "maps.txt")}");

        foreach (var file in Directory.GetFiles(dir, "*.png"))
            Console.WriteLine($"  {Path.GetFileName(file)}  {new FileInfo(file).Length / 1024} KB");

        // a release's notes, as the app now shows them: a page over the dashboard with the shared header,
        // so the capture also proves the arrow and title switch — with a release exercising the renderer
        var notesWindow = new MainWindow(app);
        notesWindow.OpenNotes(new BasaltRelease(
            "v0.3.3", "Basalt 0.3.3", UpdateChecker.ReleasePage,
            "# What changed\n\n" +
            "- adaptive swaps **now deferring** less: `34747` into the VRAM tier\n" +
            "- elastic KV backs `151552` cells in 10.6 ms\n" +
            "- see [the full page](https://example.com) for everything\n\n" +
            "## Fixes\n\n" +
            "- a grammar check ran `50 windows` with 1 masked resample\n" +
            "plain paragraph after the bullets, wrapped twice over to be sure the hang is right.\n",
            "2026-11-12", []));
        written += Shoot(notesWindow, Path.Combine(dir, "06-notes.png"), maps);

        // One model's history: the window that answers "how fast has it run?" The seeded beat left the
        // view model's recorder with an open session for the loaded model and, under it, the ≈ line the
        // engine's three-hour totals owed the app for the time it was away. Both lines are its data.
        try
        {
            var histRow = new HomeModelRow("qwen3.8-flash-next", "loaded · 262,144 ctx · vision",
                                           Live: true, Loaded: true, Key: "qwen3.8-flash-next");
            var histData = app.Vm.HistoryFor(histRow);
            Console.WriteLine($"  history sessions for {histRow.Key}: {histData.Count}");
            foreach (var s in histData)
                Console.WriteLine($"    since={s.Since:o} req={s.Requests} decode=“{s.DecodeRange}” prefill=“{s.PrefillRange}” now={s.Current} gap={s.Gap}");
            var history = new ModelHistoryWindow(histRow, app.Vm);
            // What the window's own Refresh() put in the list is what the image shows; print it, because a
            // frame that looks right must be verifiable in words. The private Line prints itself.
            var rowsField = history.GetType().GetField("RowsList",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var rowsSource = (rowsField?.GetValue(history) as System.Windows.Controls.ItemsControl)
                ?.ItemsSource as System.Collections.IEnumerable;
            var builtRows = rowsSource?.Cast<object>().ToList() ?? [];
            Console.WriteLine($"  rows the window built: {builtRows.Count}");
            foreach (var built in builtRows)
                Console.WriteLine($"    {built}");
            written += Shoot(history, Path.Combine(dir, "09-history.png"), maps,
                             ("rows", 20, 56, 700, 190));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  history shot failed: {ex.GetType().Name}: {ex.Message}");
        }

        // These windows were shown to give them a render target and then hidden; one of them may otherwise
        // keep the process company. The images are written and flushed by now.
        Environment.Exit(written >= 9 ? 0 : 1);
        return written >= 9 ? 0 : 1;
    }

    /// <summary>
    /// The app's theme: the compiled resources when WPF will hand them over, otherwise the very same .xaml
    /// files read from disk next to the exe. Either way it is the real palette, never a copy kept for the
    /// tool's comfort. Tokens first (dark or light), then the shared styles, which is the order App uses.
    /// </summary>
    private static void LoadTheme(Application app, bool light)
    {
        var tokens = light ? "Light.xaml" : "Dark.xaml";
        var dark = light ? "Dark" : "Light";
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/BasaltUI;component/Themes/{tokens}")
            });
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/BasaltUI;component/Themes/Theme.xaml")
            });
            Console.WriteLine($"  theme: compiled resource of BasaltUI ({(light ? "light" : "dark")} tokens)");
            return;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  theme: compiled resource unavailable ({ex.GetType().Name}), reading the files");
        }

        var baseDir = Path.Combine(AppContext.BaseDirectory, "theme");
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(
            File.ReadAllText(Path.Combine(baseDir, tokens))));
        app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(
            File.ReadAllText(Path.Combine(baseDir, "Theme.xaml"))));
        Console.WriteLine($"  theme: {baseDir} ({tokens} tokens)");
    }

    /// <summary>
    /// The dashboard's own memory bars, read out of the live window rather than off a screenshot, and the left
    /// column scrolled down to the RAM bar. The memory figures are what the picture is being asked to settle,
    /// and one of them sits under four other bars, so this says plainly what the window holds and then makes it
    /// visible. Everything here runs after the layout pass and before the pixels, when the bars have values.
    /// </summary>
    private static void ShowMachineBars(Window window)
    {
        var ram = FindBar(window, "RAM");
        var vram = FindBar(window, "VRAM");
        var scroll = ram is null ? null : Ancestor<ScrollViewer>(ram);

        Console.WriteLine($"  vram bar: \"{vram?.ValueText}\" at {vram?.Fraction:F3}, " +
                          $"colour {(vram is null ? "-" : vram.Calm ? "Calm" : "threshold")}");
        Console.WriteLine($"  ram bar: \"{ram?.ValueText}\" at {ram?.Fraction:F3}");
        if (scroll is not null)
            Console.WriteLine($"  left column: extent {scroll.ExtentHeight:F0} > viewport {scroll.ViewportHeight:F0} " +
                              $"by {scroll.ScrollableHeight:F0}, offset was {scroll.VerticalOffset:F0}");

        // BringIntoView from a window parked off every desktop leaves the offset where it was, so the scroll is
        // done by arithmetic the log can show: where the bar lies in the scroll viewer's own coordinates, less a
        // little room to breathe above it, clamped to what can actually be scrolled.
        if (ram is not null && scroll is not null)
        {
            var top = ram.TransformToVisual(scroll).Transform(new Point(0, 0)).Y + scroll.VerticalOffset;
            var where = Math.Clamp(top - 24, 0, scroll.ScrollableHeight);
            scroll.ScrollToVerticalOffset(where);
            Console.WriteLine($"  asked to scroll to {where:F0}, the bar lying at {top:F0}, to put the RAM row in the picture");
        }
    }

    private static Bar? FindBar(DependencyObject root, string caption)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Bar { Caption: var text } bar && string.Equals(text, caption, StringComparison.Ordinal))
                return bar;
            if (FindBar(child, caption) is Bar found)
                return found;
        }

        return null;
    }

    private static T? Ancestor<T>(DependencyObject from) where T : DependencyObject
    {
        for (var node = VisualTreeHelper.GetParent(from); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T hit)
                return hit;
        }

        return null;
    }

    // ---- one window, laid out and captured -----------------------------------------------------------------
    /// <summary>
    /// Optional work for <see cref="Shoot"/> to do once the window is shown and laid out but before it is
    /// drawn, for the changes that only take effect on the next pass — scrolling a bar into view, say.
    /// Set it, take the shot, clear it; one window at a time.
    /// </summary>
    private static Action<Window>? AfterLayout { get; set; }

    /// <summary>
    /// The second half of <see cref="AfterLayout"/>, run once the layout pass that work asked for has really
    /// happened: a scroll viewer will not report the offset it was told to take until then, so a number read in
    /// the first half is a number about the past.
    /// </summary>
    private static Action<Window>? AfterSettled { get; set; }

    private static int Shoot(Window window, string file, TextWriter maps,
                             params (string Name, int X, int Y, int W, int H)[] crops)
    {
        var name = Path.GetFileName(file);
        try
        {
            var w = DoubleOr(window.Width, 1120) - ChromeW;
            var h = DoubleOr(window.Height, 790) - ChromeH;
            var size = new Size(Math.Max(480, w), Math.Max(320, h));

            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.ShowInTaskbar = false;
            window.Left = -32000;
            window.Top = -32000;
            window.Width = size.Width;
            window.Height = size.Height;

            // A window that has never been shown has no render target: measuring and arranging it goes
            // through, and RenderTargetBitmap then hands back a transparent rectangle. So it is taken far
            // outside every desktop, given a turn at the dispatcher so that it paints, and drawn from there.
            window.Show();
            Pump();
            window.UpdateLayout();
            Probe(name, window);

            AfterLayout?.Invoke(window);
            window.UpdateLayout();
            Pump();
            AfterSettled?.Invoke(window);

            var (best, bestLook) = Capture(window, size, name);

            // A VisualBrush does not lay its target out on the spot; it queues that pass on the dispatcher.
            // Drained here, while the window is still shown, it is harmless. Left in the queue, it fires during
            // some later UpdateLayout against a window that is gone, and WPF has no answer for that.
            window.UpdateLayout();
            Pump();

            window.Hide();
            Describe(name, bestLook);
            Console.WriteLine("  brightest pixel per cell, left edge to right:");
            Map(name, best, 72, Console.Out, 0, 0, 1, 0.12);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(best));
            using (var stream = File.Create(file))
                encoder.Save(stream);

            Console.WriteLine($"  shot {name} {best.PixelWidth}x{best.PixelHeight}");

            // The picture in text, for a reader that cannot open one.
            Map(name, best, 140, maps);
            maps.Flush();
            foreach (var (cropName, cx, cy, cw, ch) in crops)
                Crop(best, Path.Combine(Path.GetDirectoryName(file) ?? ".",
                                        $"{Path.GetFileNameWithoutExtension(name)}-{cropName}.png"),
                     cx, cy, cw, ch);

            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  shot {name} failed: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Draws the same target three ways. Which one carries pixels says what this machine's WPF will and will
    /// not give up: an unshown window gives nothing, a shown window usually gives its content, and the
    /// VisualBrush route forces a fresh render pass when the direct one comes back flat.
    /// </summary>
    private static (RenderTargetBitmap Best, Look Look) Capture(Window window, Size size, string name)
    {
        var best = Draw(window, size);
        var bestLook = Scan(best);
        Console.WriteLine($"  {name}: window {bestLook.Brighter} lit");

        if (window.Content is Visual content)
        {
            var inner = Draw(content, size);
            var innerLook = Scan(inner);
            Console.WriteLine($"  {name}: content {innerLook.Brighter} lit");
            if (innerLook.Brighter > bestLook.Brighter)
            {
                best = inner;
                bestLook = innerLook;
            }
        }

        // The brush variant goes on the window's content, never on the Window itself: WPF measures a Window
        // only from its own hwnd root, and a VisualBrush asking for that measure trips an internal invariant
        // that ends the process with "Unrecoverable system error" — no exception to catch, no image written.
        if (window.Content is Visual body)
        {
            var brushed = DrawBrushed(body, size);
            var brushedLook = Scan(brushed);
            Console.WriteLine($"  {name}: visualbrush {brushedLook.Brighter} lit");
            if (brushedLook.Brighter > bestLook.Brighter)
            {
                best = brushed;
                bestLook = brushedLook;
            }
        }

        return (best, bestLook);
    }

    /// <summary>Paints a visual through a VisualBrush, which re-renders it rather than asking for what is already composed.</summary>
    private static RenderTargetBitmap DrawBrushed(Visual target, Size size)
    {
        var drawn = new DrawingVisual();
        using (var context = drawn.RenderOpen())
        {
            var brush = new VisualBrush(target)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                TileMode = TileMode.None
            };
            context.DrawRectangle(brush, null, new Rect(size));
        }

        return Draw(drawn, size);
    }

    /// <summary>
    /// A region of a capture, enlarged. Full-window images arrive at a model too small to read a button's
    /// padding or a pill's lettering from, which is exactly what a design pass turns on.
    /// </summary>
    private static void Crop(RenderTargetBitmap bmp, string file, int x, int y, int w, int h, double scale = 2)
    {
        var left = Math.Max(0, Math.Min(x, bmp.PixelWidth - 1));
        var top = Math.Max(0, Math.Min(y, bmp.PixelHeight - 1));
        var clip = new Int32Rect(left, top,
                                 Math.Min(w, bmp.PixelWidth - left),
                                 Math.Min(h, bmp.PixelHeight - top));
        var cropped = new CroppedBitmap(bmp, clip);
        var grown = new TransformedBitmap(cropped, new ScaleTransform(scale, scale));

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(grown));
        using var stream = File.Create(file);
        encoder.Save(stream);
        Console.WriteLine($"  crop {Path.GetFileName(file)} {grown.PixelWidth}x{grown.PixelHeight}");
    }

    /// <summary>
    /// Four hand-built visuals put through the same pipeline, as controls: which of these comes back empty
    /// says whether the fault is the capture code, text rendering, or ordinary child elements — and a window's
    /// flat fill alone cannot tell them apart.
    /// </summary>
    private static void ProbePipeline()
    {
        var size = new Size(300, 120);

        var fill = new DrawingVisual();
        using (var context = fill.RenderOpen())
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x40, 0xD6, 0xC4)), null, new Rect(size));
        Micro("drawingvisual fill", fill, size);

        var text = new DrawingVisual();
        var line = new FormattedText(
            "hello", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            40, new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)), 1.0);
        using (var context = text.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16)), null, new Rect(size));
            context.DrawText(line, new Point(10, 30));
        }
        Micro("drawingvisual text", text, size);

        var panel = new Grid { Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x16)) };
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x40, 0xD6, 0xC4)),
            Width = 60,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top
        });
        panel.Children.Add(new TextBlock
        {
            Text = "hello",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)),
            FontSize = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        panel.Measure(size);
        panel.Arrange(new Rect(size));
        panel.UpdateLayout();
        Micro("panel fill and text", panel, size);
    }

    private static void Micro(string what, Visual target, Size size)
    {
        var look = Scan(Draw(target, size));
        Console.WriteLine($"  micro {what}: {look.Colours} colours, of {look.Total} px accent={look.Accent} ink={look.Ink}");
    }

    /// <summary>
    /// A short dump of what is actually in the visual tree. Said out loud because "the capture is flat" has
    /// more than one cause, and the tree says which one it is: never laid out, laid out with no children, or
    /// hidden.
    /// </summary>
    private static void Probe(string label, Window window)
    {
        var nodes = 0;
        var text = 0;
        var painted = 0;
        var seen = new List<string>();

        void Walk(Visual visual, int depth)
        {
            if (nodes++ > 400)
                return;

            var fe = visual as FrameworkElement;
            if (visual is TextBlock) text++;
            if (visual is System.Windows.Shapes.Shape or Border) painted++;

            if (seen.Count < 14 && depth <= 4)
            {
                var size = fe is null ? "" : $"{fe.ActualWidth:F0}x{fe.ActualHeight:F0}";
                var offset = VisualTreeHelper.GetOffset(visual);
                seen.Add($"{new string(' ', depth * 2)}{visual.GetType().Name} {size}" +
                         $" @({offset.X:F0},{offset.Y:F0}) vis={(visual as UIElement)?.IsVisible}");
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
                if (VisualTreeHelper.GetChild(visual, i) is Visual child)
                    Walk(child, depth + 1);
        }

        Walk(window, 0);
        Console.WriteLine($"  {label} tree: loaded={window.IsLoaded} actual={window.ActualWidth:F0}x{window.ActualHeight:F0} " +
                          $"content={window.Content?.GetType().Name ?? "none"} descendants={nodes} text={text} shapes={painted}");
        foreach (var line in seen)
            Console.WriteLine("    " + line);
    }

    private static RenderTargetBitmap Draw(Visual target, Size size)
    {
        var bmp = new RenderTargetBitmap((int)(size.Width * Zoom), (int)(size.Height * Zoom),
                                         96 * Zoom, 96 * Zoom, PixelFormats.Pbgra32);
        bmp.Render(target);
        return bmp;
    }

    /// <summary>
    /// Runs the dispatcher until it goes idle once, which is what lets a freshly shown window paint.
    /// </summary>
    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
                                                 new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>A private instance field of a window or control, reached the same way the tool reaches QueryBox.</summary>
    private static T? Field<T>(object? host, string name) where T : class =>
        host is null ? null
        : host.GetType().GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
             ?.GetValue(host) as T;

    /// <summary>
    /// Pumps the dispatcher while a condition waits to become true. Sleep happens off the pump, so every
    /// pumped frame gets a turn to run the asynchronous work that feeds the condition.
    /// </summary>
    private static void WaitUntil(Func<bool> done, int timeoutMs)
    {
        for (var waited = 0; waited < timeoutMs; waited += 100)
        {
            Pump();
            if (done())
                return;
            System.Threading.Thread.Sleep(100);
        }
        Pump();
    }

    // ---- what came out --------------------------------------------------------------------------------------
    /// <summary>Counts that say whether a capture holds a drawn interface, without anyone having to open it.</summary>
    private sealed record Look(int Colours, int Ground, int Brighter, int Ink, int Faint, int Accent, int Total);

    private static Look Scan(BitmapSource bmp)
    {
        var stride = bmp.PixelWidth * 4;
        var pixels = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(pixels, stride, 0);

        var colours = new HashSet<int>();
        int ground = 0, accent = 0, ink = 0, lit = 0, brighter = 0, total = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var b = pixels[i];
            var g = pixels[i + 1];
            var r = pixels[i + 2];
            if (pixels[i + 3] == 0)
                continue;

            total++;
            // 5 bits a channel: enough to tell a themed window from a flat fill or a stray theme.
            colours.Add((r >> 3 << 10) | (g >> 3 << 5) | (b >> 3));

            if (Near(r, g, b, 0x10, 0x12, 0x16, 10)) ground++;   // Window
            if (Near(r, g, b, 0x40, 0xD6, 0xC4, 40)) accent++;    // Accent teal
            if (Near(r, g, b, 0xE8, 0xEA, 0xED, 30)) ink++;       // TextPrimary
            if (Near(r, g, b, 0x9A, 0xA3, 0xAF, 30)) lit++;       // TextSecondary
            if (r + g + b > 0x16 * 3) brighter++;                 // anything brighter than the ground
        }

        return new Look(colours.Count, ground, brighter, ink, lit, accent, Math.Max(1, total));
    }

    private static void Describe(string label, Look look) =>
        Console.WriteLine($"  {label}: {look.Colours} colours, ground {(100.0 * look.Ground / look.Total):F0}%, " +
                          $"lit {(100.0 * look.Brighter / look.Total):F1}%, ink {(100.0 * look.Ink / look.Total):F1}%, " +
                          $"faint {(100.0 * look.Faint / look.Total):F1}%, accent {(100.0 * look.Accent / look.Total):F2}%");

    private static bool Near(byte r, byte g, byte b, int cr, int cg, int cb, int tolerance) =>
        Math.Abs(r - cr) <= tolerance && Math.Abs(g - cg) <= tolerance && Math.Abs(b - cb) <= tolerance;

    // ---- a picture in text -----------------------------------------------------------------------------------
    // Because the model driving this tool has no way to open an image, a capture is also described as a grid of
    // characters: every cell reports the brightest thing inside it. Layout, density, alignment and where the
    // accent colour went are all readable that way. What it cannot show is the shape of a glyph or the quality of
    // antialiasing — those are for a person, which is why the pictures are written too.
    private static void Map(string label, RenderTargetBitmap bmp, int cols, TextWriter sink,
                            double x0 = 0, double y0 = 0, double x1 = -1, double y1 = -1)
    {
        var stride = bmp.PixelWidth * 4;
        var pixels = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(pixels, stride, 0);

        var left = (int)(x0 * bmp.PixelWidth);
        var right = (int)((x1 < 0 ? 1 : x1) * bmp.PixelWidth);
        var top = (int)(y0 * bmp.PixelHeight);
        var bottom = (int)((y1 < 0 ? 1 : y1) * bmp.PixelHeight);
        var cell = Math.Max(1, (right - left) / cols);
        var rows = Math.Max(1, (bottom - top) / (cell * 2));

        sink.WriteLine($"map {label} — {cols}x{rows} cells over px {left},{top}..{right},{bottom}");
        sink.WriteLine("     . ground  , surface  + surface hi  : control  c hover  | line  # accent  T ink" +
                       "  t secondary  f faint  W warn  E bad  G good  ? off palette");
        for (var ry = 0; ry < rows; ry++)
        {
            var line = new StringBuilder(cols);
            for (var rx = 0; rx < cols; rx++)
            {
                var x0c = left + rx * cell;
                var y0c = top + ry * cell * 2;
                var bestLum = -1;
                var best = ' ';
                for (var y = y0c; y < Math.Min(bottom, y0c + cell * 2); y++)
                {
                    for (var x = x0c; x < Math.Min(right, x0c + cell); x++)
                    {
                        var i = y * stride + x * 4;
                        if (pixels[i + 3] == 0)
                            continue;

                        var b = pixels[i];
                        var g = pixels[i + 1];
                        var r = pixels[i + 2];
                        if (r + g + b <= bestLum)
                            continue;

                        bestLum = r + g + b;
                        best = Bucket(r, g, b);
                    }
                }

                line.Append(best);
            }

            sink.WriteLine("  " + line);
        }
    }

    private static char Bucket(byte r, byte g, byte b)
    {
        if (Near(r, g, b, 0x40, 0xD6, 0xC4, 45)) return '#';   // Accent
        if (Near(r, g, b, 0xE8, 0xEA, 0xED, 30)) return 'T';   // TextPrimary
        if (Near(r, g, b, 0x9A, 0xA3, 0xAF, 30)) return 't';   // TextSecondary
        if (Near(r, g, b, 0x6B, 0x72, 0x80, 30)) return 'f';   // TextFaint
        if (Near(r, g, b, 0xE8, 0xB5, 0x4A, 40)) return 'W';   // Warn
        if (Near(r, g, b, 0xE4, 0x57, 0x4C, 40)) return 'E';   // Bad
        if (Near(r, g, b, 0x5F, 0xBF, 0x7F, 40)) return 'G';   // Good
        if (Near(r, g, b, 0x10, 0x12, 0x16, 8)) return '.';    // Window
        if (Near(r, g, b, 0x16, 0x18, 0x1D, 6)) return ',';    // Surface
        if (Near(r, g, b, 0x1C, 0x1F, 0x25, 6)) return '+';    // SurfaceHi
        if (Near(r, g, b, 0x22, 0x26, 0x2E, 6)) return ':';    // ControlBg
        if (Near(r, g, b, 0x2B, 0x30, 0x39, 8)) return 'c';    // ControlHover
        if (Near(r, g, b, 0x2A, 0x2F, 0x38, 6)) return '|';    // Line
        return '?';                                           // something not in the palette
    }

    private static double DoubleOr(double value, double whenUnset) =>
        double.IsNaN(value) || value <= 0 ? whenUnset : value;

    // ---- the app, minus the parts that would collide with a real one ----------------------------------------
    /// <summary>
    /// Showing a window starts the real app's start-up, and that start-up is written for one copy on the
    /// machine: it takes the single-instance name, and when a copy already has it, it raises that copy's window
    /// — the user's, on their screen — and shuts itself down, which is the end of every capture after the first.
    /// It also puts a tray icon in the notification area for the length of the run.
    /// The tool wants the settings, the view model and the palette, and nothing that touches the desktop, so it
    /// overrides that start-up and keeps only what the windows read.
    /// </summary>
    private sealed class ShotsApp : App
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            ReloadConfig();
            Vm.ReapplyPolling();
            Net.Configure(Settings.ProxyUrl);

            // The machine this runs on has no way to GitHub, and a beat would otherwise start the quiet update
            // check and let its failure land in the middle of a picture, at whichever speed the network decides.
            Settings.CheckForUpdates = false;
            // base.OnStartup is deliberately not called: no mutex, no tray icon, no window, no poller. The
            // reading the panels need is handed over in Seed instead, so the images are the same on any machine.
        }
    }

    // ---- deliberate filler ----------------------------------------------------------------------------------
    // Only there so that panes which hold a table, a log, or a line of numbers do not photograph as empty
    // rectangles. The reading below is invented: this machine has no basalt to ask, and every metric panel draws
    // the poller's last reading, so without one the whole right-hand side is a flat line and an empty box.
    // It travels as a real ServerStatus because a beat rebuilds the model list, the request list and the
    // warnings from it — anything added first is cleared by the same call.
    private static void Seed(MainViewModel vm)
    {
        vm.Append("watching 127.0.0.1:8080 — basalt 0.4.0, one model loaded, ctx 262144, RTX 5090", LogSource.App);
        vm.Append("registered Qwen3.8-Flash-Next UD-IQ3_XXS · 96 shards · 145.0 GB · 00:41 to load", LogSource.Server);
        vm.Append("kv cache int8 · 262144 tokens · 78.4 GB of 80.0 GB used on device 0", LogSource.Server);
        vm.Append("POST /v1/chat/completions 200 · 6.42s · 312 tokens out · 48.6 tok/s · 97.8% cache hit", LogSource.Server);
        vm.Append("GET /stats · 1 in flight · queue empty · ttft 83ms", LogSource.Server);
        vm.Append("error: model C:\\models\\bge-small-en.basalt is not a basalt file — ignored", LogSource.Error);
        vm.Append("POST /v1/embeddings 400 · model 'bge-small-en' is not loaded", LogSource.Server);

        var models = new[]
        {
            new ModelRow("qwen3.8-flash-next", "loaded", false, 262144, true),
            new ModelRow("qwen3.8-flash-next-coding", "asleep", true, 131072, false),
            new ModelRow("deepresearch-reranker", "loading", false, 8192, false),
        };
        var requests = new[]
        {
            new RequestRow("1042", "/v1/chat/completions", 6.42, 6.42, "eos",
                           1831, 1792, 312, 48.6, 0.978, 0.083, 0.11, null, 418, 297, 5.1),
            new RequestRow("1043", "/v1/completions", 1.08, 1.08, "length",
                           6042, 6016, 256, 52.1, 0.996, 0.061, 0.09, 0.02, 301, 244, 3.2),
            new RequestRow("1044", "/v1/chat/completions", 0.31, 0.31, "stop",
                           288, 0, 34, 41.2, 0.0, 0.074, 0.13, null, 40, 22, 24.0),
        };
        var gpu = new GpuRow(0, "RTX 5090", 62, Gib(78), Gib(80), 61, 412, 570, 5, 11.2, 3.4);

        var reading = new ServerStatus
        {
            Reachable = true,
            SeenAt = DateTime.Now,
            State = "ready",
            ModelName = "qwen3.8-flash-next",
            ModelPath = @"C:\models\qwen3.8-flash-next.basalt",
            Loaded = true,
            MaxContext = 262144,
            Images = true,
            EngineVersion = "0.4.0",
            BuildInfo = "basalt 0.4.0 · cuda 12.9",
            Quant = "UD-IQ3_XXS",
            Kv = "int8",
            GpuIds = "0",
            ExpertSlots = 6,
            ExpertTotal = 8,
            ExpertCacheMib = 12_288,
            VramFreeMib = 1_536,
            ArenaMib = 74_112,
            PoolWorkers = 4,
            LiveState = "decoding",
            SlotsTotal = 4,
            SlotsActive = 1,
            QueueDepth = 0,
            TokS = 48.6,
            PrefillTokS = 712.0,
            ElapsedS = 3.9,
            LivePromptTokens = 1831,
            LiveGenerated = 214,
            Queue = [new RequestQueueRow("/v1/chat/completions", "qwen3.8-flash-next", 1831, 0.083)],
            CpuPercent = 17.5,
            RamUsed = Gib(41),
            RamTotal = Gib(64),
            CpuName = "Ryzen 9 9950X 16-Core",
            Cores = 16,
            Threads = 32,
            Driver = "572.16",
            Cuda = "12.9",
            Gpus = [gpu],
            Spill = "312 MiB at 1.9 GB/s",
            Requests = requests,
            Models = models,
            // The engine's own session totals — three hours of running, most of it unseen: they are what
            // the throughput stats row and the history window's ≈ line draw from.
            Totals = new MetricsTotals(
                Since: (long)(DateTime.UtcNow.AddHours(-3) - DateTime.UnixEpoch).TotalSeconds,
                Requests: 24, Prompt: 509_038, Reused: 432_858, Written: 12_494,
                PromptMs: 14_669, DecodeMs: 97_030),
            Best = new MetricsBest(PrefillTokS: 8_975.8, DecodeTokS: 172.4),
            TokSHistory = Curve(38, 58, 60),
            PrefillHistory = Curve(240, 980, 60),
            SlotHistories = [Curve(20, 52, 60), Curve(0, 0, 60), Curve(0, 0, 60), Curve(0, 0, 60)],
            GpuUtilHistory = Curve(35, 88, 60),
            GpuPowerHistory = Curve(300, 505, 60),
            GpuTempHistory = Curve(54, 66, 60),
        };

        // The poller's own door into the screen, taken from outside: a beat rebuilds the model list, the request
        // table and the warnings, so seeding those by hand first would only be undone. Reflection rather than a
        // seam in production code whose only caller is a screenshot tool.
        typeof(MainViewModel)
            .GetMethod("OnStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, [reading]);
        Pump();                             // a beat arrives on the dispatcher, as it does in the app

        // This one comes from the config file rather than from a beat, and warnings are rebuilt by every beat.
        vm.Warnings.Add("server: host 0.0.0.0 answers to other machines too — give basalt an api key");
    }

    private static long Gib(long gibibytes) => gibibytes * 1024 * 1024 * 1024;

    /// <summary>A line that wanders between two values. Sixty samples, because that is what the engine keeps,
    /// and the shape of a sparkline is what the eye actually judges in the metrics panel.</summary>
    private static double[] Curve(double low, double high, int count)
    {
        var random = new Random(7);                          // fixed seed: the same picture on every machine
        var values = new double[count];
        var value = low + (high - low) * 0.45;
        for (var i = 0; i < count; i++)
        {
            value += (random.NextDouble() - 0.45) * (high - low) * 0.16;
            value = Math.Clamp(value, low, high);
            values[i] = Math.Round(value, 1);
        }

        return values;
    }
}

