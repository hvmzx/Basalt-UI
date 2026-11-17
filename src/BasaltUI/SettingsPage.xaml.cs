using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using BasaltUI.Core;
using Microsoft.Win32;

namespace BasaltUI;

/// <summary>
/// Editing basalt's own file: a curated form for the fields people actually set, everything the file
/// already carries shown beside them, and the raw JSON as the way out. It lives inside the main window
/// as a page — the back arrow is the main window's — so this class is a control, not a window, and it
/// asks its host window to own the dialogs it opens.
/// </summary>
public partial class SettingsPage : UserControl
{
    private static readonly string[] EntryCommon =
        ["model", "alias", "gpu", "kv", "max-context", "vision", "temperature", "top-p", "parallel"];

    private static readonly string[] ServerCommon =
        ["host", "port", "open", "lazy", "debug", "request-lines", "max-payload-mib", "allowed-host", "cors-origin",
         "image-dir", "image-url-host", "tokenizer"];

    private static readonly string[] EngineCommon =
        ["resident-experts", "expert-cache", "resident-budget-gib", "mmap-experts", "kv", "max-context", "parallel",
         "spec", "idle-sleep", "prefill"];

    private readonly App app;
    private readonly SettingsCatalog catalog = SettingsCatalog.Shared;
    private ConfigDocument doc = null!;
    private JsonObject? entry;
    private bool ready;
    private bool rebuilding;
    private bool committing;

    public SettingsPage()
    {
        app = (App)Application.Current;
        InitializeComponent();
        ShowTab();

        // one-time wiring: the per-show refresh lives in OnShown, which the main window calls
        ExeBox.LostFocus += (_, _) => CommitSettings();
        ConfigBox.LostFocus += (_, _) => CommitSettings();
        ModelsFolderBox.LostFocus += (_, _) => CommitSettings();
        ExtraBox.LostFocus += (_, _) => CommitSettings();
        KeyBox.LostFocus += (_, _) => CommitSettings();
        foreach (var box in new[] { StartOnLaunchCheck, StartMinimisedCheck, TrayOnCloseCheck, StopOnExitCheck, OpenWebCheck, AutostartCheck, CheckUpdatesCheck })
        {
            box.Checked += (_, _) => CommitSettings();
            box.Unchecked += (_, _) => CommitSettings();
        }
        foreach (var box in new[] { ConnectionsBox, MirrorBox, ProxyBox })
            box.LostFocus += (_, _) => CommitSettings();

        OnShown();
    }

    /// <summary>The window hosting this page, which owns every dialog the page opens.</summary>
    private Window W => Window.GetWindow(this)!;

    private AppSettings S => app.Settings;

    /// <summary>
    /// Everything the page shows is read again from disk, so a page left open behind other work comes
    /// back honest: settings as they are now, the config file as it is on disk now.
    /// </summary>
    public void OnShown()
    {
        ready = false;

        ReadBasaltTab();

        doc = OpenDoc(app.Settings.ConfigPath);
        RebuildEverything();
        ready = true;
    }

    // ---- the config file -----------------------------------------------------------------------------------
    private static string DefaultConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BasaltUI", "basalt_config.json");

    private ConfigDocument OpenDoc(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            path = DefaultConfigPath;
        try
        {
            return File.Exists(path)
                ? ConfigDocument.Load(path)
                : ConfigDocument.Blank(path, GuessModelPath());
        }
        catch (Exception e)
        {
            MessageBox.Show(W, "That file could not be read:\n\n" + e.Message +
                               "\n\nThe editor is showing a fresh starting point instead. Nothing has been written yet.",
                            "Basalt UI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return ConfigDocument.Blank(path, "");
        }
    }

    private string GuessModelPath()
    {
        try
        {
            var folder = S.ModelsFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return "";
            foreach (var pattern in new[] { "*.basalt", "*.gguf" })
            {
                var first = Directory.EnumerateFiles(folder, pattern).FirstOrDefault();
                if (first is not null)
                    return first;
            }
        }
        catch
        {
            // a guess is allowed to fail
        }
        return "";
    }

    private void RebuildEverything()
    {
        PathText.Text = doc.FilePath;
        BuildModelList();
        BuildScopeFields(ServerFields, doc.Server, "server", ServerCommon, AddServerFieldCombo);
        BuildScopeFields(EngineFields, doc.Engine, "engine", EngineCommon, AddEngineFieldCombo);
        ServerNote.Text = NoteForServer();
        ShowIssues(doc.Validate(catalog));
        UpdateDirty();
    }

    private string NoteForServer() => ConfigDocument.ThisPcOnly(doc.Host)
        ? $"http://{doc.Host}:{doc.Port} — reachable from this machine only."
        : $"http://{doc.Host}:{doc.Port} — this answers on the network, and basalt is asking you for an api key in the Basalt tab.";

    // ---- paths and behaviour tab ---------------------------------------------------------------------------
    private void ReadBasaltTab()
    {
        ExeBox.Text = S.BasaltExePath;
        ConfigBox.Text = S.ConfigPath;
        ModelsFolderBox.Text = S.ModelsFolder;
        ExtraBox.Text = S.ExtraArgs;
        KeyBox.Password = S.ApiKey;
        AutostartCheck.IsChecked = Autostart.IsEnabled();
        StartOnLaunchCheck.IsChecked = S.StartServerOnLaunch;
        StartMinimisedCheck.IsChecked = S.StartMinimized;
        TrayOnCloseCheck.IsChecked = S.MinimiseToTrayOnClose;
        StopOnExitCheck.IsChecked = S.StopServerOnAppExit;
        OpenWebCheck.IsChecked = S.OpenWebUiOnReady;
        GraceBox.Text = S.StopGraceSeconds.ToString();
        PollBox.Text = S.PollMs.ToString();
        ConnectionsBox.Text = S.DownloadConnections.ToString();
        MirrorBox.Text = S.HfMirror;
        ProxyBox.Text = S.ProxyUrl;
        CheckUpdatesCheck.IsChecked = S.CheckForUpdates;
        SayWhatTheUpdateClockSays();
        UpdatePreview();
    }

    /// <summary>
    /// The update block in this tab is the only place the whole story fits: what we are running, what is out
    /// there, when we last asked, and the one release the user told us to stop mentioning.
    /// </summary>
    private void SayWhatTheUpdateClockSays()
    {
        var vm = app.Vm;
        UpdateFoot.Text = vm.UpdateText;
        UpdateProblemText.Text = vm.UpdateProblem;
        DismissFoot.Text = S.DismissedUpdateTag.Length == 0
            ? vm.Latest is null ? "" : "last asked " + LastAskedText()
            : $"{S.DismissedUpdateTag} is the one you asked me to stop mentioning.";
        BringBackButton.Visibility = S.DismissedUpdateTag.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private string LastAskedText()
    {
        var at = S.LastUpdateCheckUtc;
        if (at == default)
            return "never";
        var age = DateTime.UtcNow - at;
        if (age.TotalMinutes < 2)
            return "just now";
        if (age.TotalMinutes < 90)
            return $"{(int)age.TotalMinutes} minutes ago";
        if (age.TotalHours < 20)
            return "earlier today";
        return at.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    private async void CheckNow_Click(object sender, RoutedEventArgs e)
    {
        CheckNowButton.IsEnabled = false;
        UpdateFoot.Text = "asking GitHub…";
        UpdateProblemText.Text = "";
        try
        {
            await app.Vm.CheckForUpdateAsync(manual: true);
        }
        finally
        {
            CheckNowButton.IsEnabled = true;
            SayWhatTheUpdateClockSays();
        }
    }

    private void BringBack_Click(object sender, RoutedEventArgs e)
    {
        app.Vm.UpdateUndismiss();
        SayWhatTheUpdateClockSays();
    }

    /// <summary>
    /// The browser is owned by the window the page lives in, so it opens above the window rather than
    /// behind it — the classic way to lose a modal is an owner nobody named.
    /// </summary>
    private void FindModels_Click(object sender, RoutedEventArgs e)
        => new ModelsBrowserWindow(app) { Owner = W }.ShowDialog();

    private void CommitSettings()
    {
        if (!ready || committing)
            return;
        committing = true;
        try
        {
            var previousConfig = S.ConfigPath;
            S.BasaltExePath = ExeBox.Text.Trim();
            S.ConfigPath = string.IsNullOrWhiteSpace(ConfigBox.Text) ? DefaultConfigPath : ConfigBox.Text.Trim();
            S.ModelsFolder = ModelsFolderBox.Text.Trim();
            S.ExtraArgs = ExtraBox.Text.Trim();
            S.ApiKey = KeyBox.Password.Trim();
            S.StartServerOnLaunch = StartOnLaunchCheck.IsChecked == true;
            S.StartMinimized = StartMinimisedCheck.IsChecked == true;
            S.MinimiseToTrayOnClose = TrayOnCloseCheck.IsChecked == true;
            S.StopServerOnAppExit = StopOnExitCheck.IsChecked == true;
            S.OpenWebUiOnReady = OpenWebCheck.IsChecked == true;
            if (int.TryParse(GraceBox.Text.Trim(), out var grace))
                S.StopGraceSeconds = Math.Clamp(grace, 2, 900);
            if (int.TryParse(PollBox.Text.Trim(), out var poll))
                S.PollMs = Math.Clamp(poll, 250, 60_000);
            GraceBox.Text = S.StopGraceSeconds.ToString();
            PollBox.Text = S.PollMs.ToString();

            // downloads are only fast if the numbers in the boxes stay inside what the downloader means
            S.DownloadConnections = int.TryParse(ConnectionsBox.Text.Trim(), out var conns) ? Math.Clamp(conns, 1, 16) : 8;
            ConnectionsBox.Text = S.DownloadConnections.ToString();
            S.HfMirror = MirrorBox.Text.Trim();
            S.CheckForUpdates = CheckUpdatesCheck.IsChecked == true;
            var proxy = ProxyBox.Text.Trim();
            if (!string.Equals(S.ProxyUrl, proxy, StringComparison.OrdinalIgnoreCase))
            {
                S.ProxyUrl = proxy;
                Net.Configure(proxy);      // the two outbound clients are built around this address
            }
            SayWhatTheUpdateClockSays();

            S.Save();

            if (AutostartCheck.IsChecked == true != S.RunAtLogon)
                app.SetAutostart(AutostartCheck.IsChecked == true);

            if (!string.Equals(previousConfig, S.ConfigPath, StringComparison.OrdinalIgnoreCase))
            {
                doc = OpenDoc(S.ConfigPath);
                doc.MarkDirty();
                app.UseConfigFile(S.ConfigPath);
                RebuildEverything();
            }
            UpdatePreview();
        }
        finally
        {
            committing = false;
        }
    }

    private void UpdatePreview()
    {
        var exe = string.IsNullOrWhiteSpace(S.BasaltExePath) ? "basalt.exe" : Path.GetFileName(S.BasaltExePath);
        var line = $"{exe} serve --config {LaunchSpec.Quote(string.IsNullOrWhiteSpace(S.ConfigPath) ? DefaultConfigPath : S.ConfigPath)}";
        if (!string.IsNullOrWhiteSpace(S.ApiKey))
            line += " --api-key •••••";
        if (!string.IsNullOrWhiteSpace(S.ExtraArgs))
            line += " " + S.ExtraArgs;
        PreviewText.Text = line;
    }

    private void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "basalt.exe",
            Filter = "basalt executable|basalt.exe|Executable|*.exe|All files|*.*",
            InitialDirectory = PickFolder(S.ExeFallbackDir, Path.GetDirectoryName(S.BasaltExePath)),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(W) != true)
            return;
        ExeBox.Text = dialog.FileName;
        CommitSettings();
    }

    private void BrowseConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "basalt config",
            Filter = "JSON|*.json|All files|*.*",
            InitialDirectory = PickFolder(Path.GetDirectoryName(S.ConfigPath), S.ModelsFolder),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(W) != true)
            return;
        ConfigBox.Text = dialog.FileName;
        CommitSettings();
    }

    private void BrowseModels_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Where the models live",
            InitialDirectory = PickFolder(S.ModelsFolder, Path.GetDirectoryName(S.ConfigPath)),
        };
        if (dialog.ShowDialog(W) != true)
            return;
        ModelsFolderBox.Text = dialog.FolderName;
        CommitSettings();
    }

    private void NewConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "A new config file",
            Filter = "JSON|*.json",
            FileName = "basalt_config.json",
            InitialDirectory = PickFolder(S.ModelsFolder, Path.GetDirectoryName(S.ConfigPath)),
        };
        if (dialog.ShowDialog(W) != true)
            return;

        if (File.Exists(dialog.FileName) &&
            MessageBox.Show(W, "That file already exists. Replace it?", "Basalt UI",
                            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        doc = ConfigDocument.Blank(dialog.FileName, GuessModelPath());
        ConfigBox.Text = dialog.FileName;
        CommitSettings();
        RebuildEverything();
        ShowRaw();
        Tabs.SelectedIndex = 1;
    }

    private void CopyCommandLine_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PreviewText.Text);
        }
        catch
        {
            // the clipboard can be busy; the text is on screen anyway
        }
    }

    private void OpenAppFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppPaths.Ensure();
            Process.Start(new ProcessStartInfo(Path.GetDirectoryName(AppPaths.SettingsFile)!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            app.Notify("could not open the folder: " + ex.Message);
        }
    }

    private static string PickFolder(params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c) && Directory.Exists(c))
                return c;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    // ---- the Models tab -------------------------------------------------------------------------------------
    private void BuildModelList()
    {
        rebuilding = true;
        var keep = ModelsList.SelectedIndex;
        ModelsList.Items.Clear();
        foreach (var item in doc.ModelEntries())
            ModelsList.Items.Add(LabelFor(item));
        ModelsList.SelectedIndex = ModelsList.Items.Count == 0 ? -1 : Math.Clamp(keep, 0, ModelsList.Items.Count - 1);
        rebuilding = false;

        entry = doc.ModelEntries().ElementAtOrDefault(ModelsList.SelectedIndex);
        ShowEntryFields();
    }

    private string LabelFor(JsonObject item)
    {
        var model = ConfigDocument.Text(item, "model") ?? "";
        var name = string.IsNullOrWhiteSpace(model) ? "(no model chosen)" : Path.GetFileNameWithoutExtension(model);
        var alias = ConfigDocument.Text(item, "alias");
        var label = string.IsNullOrWhiteSpace(alias) ? name : alias + "  (" + name + ")";
        return app.Vm.ModelRows.Any(m => m.Id.Equals(name, StringComparison.OrdinalIgnoreCase) ||
                                         m.Id.Equals(alias, StringComparison.OrdinalIgnoreCase))
            ? label + " · serving"
            : label;
    }

    private void ModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rebuilding)
            return;
        entry = doc.ModelEntries().ElementAtOrDefault(ModelsList.SelectedIndex);
        ShowEntryFields();
    }

    private void ShowEntryFields()
    {
        if (entry is null)
        {
            ModelFields.Children.Clear();
            ModelHint.Text = "Add a model to get started — the path to a .basalt or .gguf file is enough.";
            AddFieldCombo.IsEnabled = false;
            PreviewCard.Visibility = Visibility.Collapsed;
            previewRun++;                       // a preview already on its way belongs to an entry that is gone
            return;
        }
        ModelHint.Text = "Only what you set here goes into the file; anything you leave out keeps basalt's default.";
        AddFieldCombo.IsEnabled = true;
        BuildScopeFields(ModelFields, entry, "entry", EntryCommon, AddFieldCombo);
        _ = UpdateLoadPreview();
    }

    /// <summary>
    /// The Models tab with one model's entry selected — what the main page's right-click asks for. The
    /// entry is matched by its file's name, the same word the row and the config both carry. A model the
    /// config does not name has no entry to edit, and the page stays where it was.
    /// </summary>
    public void RevealModel(ViewModels.HomeModelRow row)
    {
        var want = Path.GetFileName(row.Id);
        var index = -1;
        var entries = doc.ModelEntries().ToList();
        for (var i = 0; i < entries.Count; i++)
        {
            var model = ConfigDocument.Text(entries[i], "model") ?? "";
            if (Path.GetFileName(model).Equals(want, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (index < 0)
            return;
        Tabs.SelectedIndex = 1;                         // the Models tab
        ModelsList.SelectedIndex = index;               // the entry — SelectionChanged shows its fields
        if (ModelsList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
            item.BringIntoView();
    }

    // ---- the Configs tab -------------------------------------------------------------------------------------
    // Named configs: sets of entry-level fields that models wear. The store keeps the names; wearing one
    // is written into the model's entry in the file, so editing a config here re-writes every entry that
    // wears it — the bake below runs on every edit while this tab is open, on leaving it, and on saving.
    private string? profileName;

    /// <summary>The Configs tab with one config selected — what a model row's right-click asks for.</summary>
    public void RevealProfile(string name)
    {
        Tabs.SelectedIndex = 2;                         // the Configs tab
        RebuildProfiles(name);
        if (ProfilesList.ItemContainerGenerator.ContainerFromIndex(ProfilesList.SelectedIndex) is ListBoxItem item)
            item.BringIntoView();
    }

    private void RebuildProfiles(string? keep = null)
    {
        rebuilding = true;
        var want = keep ?? profileName;
        ProfilesList.Items.Clear();
        foreach (var n in app.Vm.Profiles.Names)
            ProfilesList.Items.Add(n);
        ProfilesList.SelectedIndex = want is null ? -1 : ProfilesList.Items.IndexOf(want);
        rebuilding = false;
        profileName = ProfilesList.SelectedItem as string;
        ShowProfileFields();
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rebuilding)
            return;
        profileName = ProfilesList.SelectedItem as string;
        ShowProfileFields();
    }

    private void ShowProfileFields()
    {
        ProfileFields.Children.Clear();
        ProfileNameBox.Text = profileName ?? "";
        ProfileNameBox.IsEnabled = profileName is not null;
        var fields = profileName is null ? null : app.Vm.Profiles.Fields(profileName);
        if (fields is null)
        {
            ProfileHint.Text = "No config selected. Attach one to a model from the main page's right-click, or add one here.";
            ProfileAttached.Text = "";
            AddProfileFieldCombo.IsEnabled = false;
            return;
        }
        ProfileHint.Text = "The fields this config sets. A model wearing it carries exactly these in the file — nothing else.";
        AddProfileFieldCombo.IsEnabled = true;
        var wearing = app.Vm.Profiles.AttachedTo(profileName!);
        ProfileAttached.Text = wearing.Count == 0
            ? "no model wears this config yet — attach it from a model row's right-click"
            : "worn by: " + string.Join(", ", wearing);
        fields.Remove("model");     // a config sets every field but the one that says which model it is
        BuildScopeFields(ProfileFields, fields, "entry",
                         EntryCommon.Where(k => k != "model").ToArray(), AddProfileFieldCombo);
    }

    private void AddProfileField_Click(object sender, RoutedEventArgs e)
    {
        if (profileName is null)
            return;
        AddFromChooser(AddProfileFieldCombo, app.Vm.Profiles.Fields(profileName));
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = app.Vm.Profiles.Create("config");
        RebuildProfiles(name);
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (profileName is null)
            return;
        var source = app.Vm.Profiles.Fields(profileName)!;
        var name = app.Vm.Profiles.Create(profileName + " copy");
        var fields = app.Vm.Profiles.Fields(name)!;
        foreach (var kv in source)
            if (kv.Value is not null)
                fields[kv.Key] = JsonNode.Parse(kv.Value.ToJsonString());
        RebuildProfiles(name);
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (profileName is null)
            return;
        var name = profileName;
        profileName = null;
        app.Vm.Profiles.Delete(name);       // assignments travel with it; the entries keep their fields
        RebuildProfiles();
    }

    private void ProfileName_LostFocus(object sender, RoutedEventArgs e)
    {
        var name = ProfileNameBox.Text.Trim();
        if (profileName is null || name.Length == 0 || name.Equals(profileName, StringComparison.Ordinal))
        {
            ProfileNameBox.Text = profileName ?? "";
            return;
        }
        app.Vm.Profiles.Rename(profileName, name);   // the models wearing it keep wearing it, under the new name
        profileName = null;
        RebuildProfiles(name);
    }

    /// <summary>Every entry in the file that wears a config is made to carry it again — the bake that
    /// keeps the file's word and the configs' word the same. The model path is never touched. True when
    /// the bake changed an entry, so the dirty flag only speaks when there is something to save.</summary>
    private bool BakeProfiles()
    {
        var changed = false;
        foreach (var entry in doc.ModelEntries())
        {
            var file = Path.GetFileName(ConfigDocument.Text(entry, "model") ?? "");
            if (app.Vm.Profiles.AssignedTo(file) is not { } worn ||
                app.Vm.Profiles.Fields(worn) is not { } fields)
                continue;
            var before = entry.ToJsonString();
            ConfigProfiles.Apply(entry, fields);
            changed |= entry.ToJsonString() != before;
        }
        if (changed)
            doc.MarkDirty();
        return changed;
    }

    /// <summary>A profile edit is also a change to every entry wearing it: while this tab is open the
    /// bake runs with the dirt, so "unsaved changes" always means the file already carries what is shown.</summary>
    private bool OnConfigsTab => (Tabs.SelectedItem as TabItem)?.Header?.ToString() == "Configs";

    // ---- the load preview ------------------------------------------------------------------------------------
    private int previewRun;

    /// <summary>
    /// How the selected entry's model file would load on this machine. The measuring itself runs off the
    /// UI thread (it opens the file's 64 KiB header and asks nvidia-smi), and a run counter discards the
    /// answer of a selection that has moved on since.
    /// </summary>
    private async Task UpdateLoadPreview()
    {
        var run = ++previewRun;
        var path = ConfigDocument.Text(entry ?? new JsonObject(), "model");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            if (run == previewRun)
                PreviewCard.Visibility = Visibility.Collapsed;
            return;
        }

        // The measuring runs off the UI thread; the answer goes onto the card through the dispatcher rather than
        // on whatever thread the await happens to resume on, and a failure goes there too. A swallowed exception
        // in a fire-and-forget call is exactly how this card sat empty with nothing to show for it.
        try
        {
            var options = PreviewOptions(path);
            var plan = await Task.Run(() => LoadPreview.ForFile(path, options));
            Dispatcher.Invoke(() => ShowPreview(run, plan));
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                if (run != previewRun)
                    return;
                PreviewCard.Visibility = Visibility.Visible;
                PreviewVerdict.Text = "could not read this file — " + ex.Message;
                PreviewRows.Children.Clear();
                PreviewNote.Text = "nothing here blocks anything: saving the config never waits for the preview.";
            });
        }
    }

    /// <summary>Fills the card, unless the selection has moved on since this preview started.</summary>
    private void ShowPreview(int run, LoadPreview.Plan plan)
    {
        if (run != previewRun)
            return;
        PreviewCard.Visibility = Visibility.Visible;
        PreviewVerdict.Text = plan.Verdict;
        PreviewRows.Children.Clear();
        foreach (var (label, value) in plan.Rows)
            PreviewRows.Children.Add(PreviewRow(label, value));
        PreviewNote.Text = plan.Note;
    }

    /// <summary>The entry's own kv, context, slots, vision and GPU, plus the engine's resident-experts switch.</summary>
    private LoadPreview.Options PreviewOptions(string name)
    {
        var block = entry ?? new JsonObject();
        return new LoadPreview.Options(
            name, 0,
            ConfigDocument.Text(block, "kv"),
            ConfigDocument.Int(block, "max-context") ?? 0,
            ConfigDocument.Int(block, "parallel") ?? 1,
            ConfigDocument.Text(block, "vision"),
            ConfigDocument.Switch(doc.Engine, "resident-experts") != false,
            int.TryParse(ConfigDocument.Text(block, "gpu"), out var gpu) ? Math.Max(0, gpu) : 0,
            S.ModelsFolder,
            ConfigDocument.Int(doc.Engine, "kv-resident") ?? 0);
    }

    private static FrameworkElement PreviewRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var res = Application.Current.Resources;
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)res["TextSecondary"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });
        var right = new TextBlock
        {
            Text = value,
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)res["TextPrimary"],
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private void AddModel_Click(object sender, RoutedEventArgs e)
    {
        var picked = PickModelFile(initial: S.ModelsFolder);
        var created = new JsonObject();
        if (picked is not null)
            created["model"] = picked;
        doc.Models.Add(created);
        doc.MarkDirty();
        BuildModelList();
        ModelsList.SelectedIndex = ModelsList.Items.Count - 1;
        UpdateDirty();
    }

    private void DuplicateModel_Click(object sender, RoutedEventArgs e)
    {
        if (entry is null)
            return;
        var copy = (JsonObject)entry.DeepClone();
        var alias = ConfigDocument.Text(copy, "alias");
        if (!string.IsNullOrWhiteSpace(alias))
            copy["alias"] = alias + "-copy";
        doc.Models.Add(copy);
        doc.MarkDirty();
        BuildModelList();
        ModelsList.SelectedIndex = ModelsList.Items.Count - 1;
        UpdateDirty();
    }

    private void RemoveModel_Click(object sender, RoutedEventArgs e)
    {
        if (entry is null)
            return;
        if (MessageBox.Show(W, "Remove this model from the config?\n\n" + LabelFor(entry) +
                                   "\n\nThe file on disk is not touched.", "Basalt UI",
                             MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        doc.Models.Remove(entry);
        doc.MarkDirty();
        BuildModelList();
        UpdateDirty();
    }

    private string? PickModelFile(string initial)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Model file",
            Filter = "Model files|*.basalt;*.gguf;*.safetensors;*.bin|All files|*.*",
            InitialDirectory = PickFolder(initial, Path.GetDirectoryName(ConfigDocument.Text(entry ?? new JsonObject(), "model") ?? "")),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(W) != true)
            return null;
        S.AddRecentModel(dialog.FileName);
        S.Save();
        return dialog.FileName;
    }

    // ---- fields ---------------------------------------------------------------------------------------------
    /// <summary>Curated keys first, then anything else the file already carries, then a chooser for the rest.</summary>
    private void BuildScopeFields(StackPanel host, JsonObject block, string scope, string[] common, ComboBox chooser)
    {
        host.Children.Clear();
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in common)
        {
            AddFieldRow(host, block, key, scope);
            shown.Add(ConfigDocument.Canonical(key));
        }

        var extra = block.Select(pair => pair.Key)
                         .Select(ConfigDocument.Canonical)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Where(k => !shown.Contains(k))
                         .OrderBy(k => k, StringComparer.Ordinal)
                         .ToList();

        if (extra.Count > 0)
        {
            host.Children.Add(Header("Other keys in this block"));
            foreach (var key in extra)
                AddFieldRow(host, block, key, scope);
        }

        FillChooser(chooser, block, scope, shown);
    }

    private void FillChooser(ComboBox chooser, JsonObject block, string scope, HashSet<string> shown)
    {
        var items = catalog.InScope(scope)
                           .Where(d => !ConfigDocument.Has(block, d.Key))
                           .OrderBy(d => GroupRank(d.Group))
                           .ThenBy(d => d.Key, StringComparer.Ordinal)
                           .Select(d => new Choice(d))
                           .ToList();
        chooser.ItemsSource = items;
        chooser.SelectedIndex = -1;
        if (items.Count == 0)
            chooser.Visibility = Visibility.Collapsed;
        else
            chooser.Visibility = Visibility.Visible;
    }

    private static int GroupRank(string group) => group switch
    {
        "Server" => 0, "GPU" => 1, "Context & KV" => 2, "Expert cache" => 3, "Slots" => 4,
        "Prompt" => 5, "Drafting" => 6, "Sampling" => 7, "Vision" => 8, "Conversation cache" => 9,
        "Control vectors" => 10, "Diagnostics" => 11, _ => 20,
    };

    private sealed record Choice(SettingDef Def)
    {
        public string Display => Def.Group + "  ·  " + Def.Key + "  ·  " + Def.Type;
        public override string ToString() => Display;
    }

    private void AddField_Click(object sender, RoutedEventArgs e) => AddFromChooser(AddFieldCombo, entry);
    private void AddServerField_Click(object sender, RoutedEventArgs e) => AddFromChooser(AddServerFieldCombo, doc.Server);
    private void AddEngineField_Click(object sender, RoutedEventArgs e) => AddFromChooser(AddEngineFieldCombo, doc.Engine);

    private void AddFromChooser(ComboBox chooser, JsonObject? block)
    {
        if (block is null || chooser.SelectedItem is not Choice choice)
            return;
        doc.Set(block, choice.Def.Key, InitialValue(choice.Def));
        doc.MarkDirty();
        RebuildCurrentPage();
        UpdateDirty();
    }

    /// <summary>A starting value that parses, so the new row is editable rather than empty.</summary>
    private static JsonNode? InitialValue(SettingDef def)
    {
        switch (def.Type)
        {
            case "bool":
                return JsonValue.Create(true);
            case "list" or "multi":
                return new JsonArray();
            case "int" or "mib" or "gib" or "seconds":
                if (def.Default is JsonValue dv && dv.TryGetValue(out long n))
                    return JsonValue.Create(n);
                return JsonValue.Create(def.Min is double lo ? (long)lo : 0L);
            case "float":
                return JsonValue.Create(def.Min ?? 0.0);
            case "enum":
                return JsonValue.Create(def.Values.Length > 0 ? def.Values[0] : "");
            default:
                return JsonValue.Create("");
        }
    }

    private void AddFieldRow(StackPanel host, JsonObject block, string key, string scope)
    {
        var known = catalog.Find(key);
        var def = known ?? new SettingDef(key, [scope], "string", null, "both", "text", "Other",
                                          "This name is not in the basalt documentation. The file keeps it, and basalt may refuse to start with it.",
                                          null, null, []);

        var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(196) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var help = def.Help + (known is null ? "\n\nNot a documented basalt option." : "");
        if (IsDefaultOn(def))
            help += "\n\nIn a config file a switch can only be turned on: leaving this box empty keeps the default.";

        var label = new TextBlock
        {
            Text = key,
            Style = StyleNamed("Label"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (known is null)
            label.Foreground = BrushNamed("Warn");
        label.ToolTip = new TextBlock { Text = help, MaxWidth = 420, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var editor = BuildEditor(block, def, key);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);

        if (ConfigDocument.Has(block, key))
        {
            var remove = new Button
            {
                Content = "×",
                Width = 26,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = "Remove this key from the file",
                Padding = new Thickness(0),
            };
            remove.Click += (_, _) =>
            {
                doc.Set(block, key, null);
                doc.MarkDirty();
                RebuildCurrentPage();
                UpdateDirty();
            };
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);
        }

        host.Children.Add(grid);
    }

    private static bool IsDefaultOn(SettingDef def) => def.Default?.ToString() == "true";

    private UIElement BuildEditor(JsonObject block, SettingDef def, string key)
    {
        if (def.Type == "bool")
        {
            var box = new CheckBox
            {
                IsChecked = ConfigDocument.Switch(block, key),
                VerticalAlignment = VerticalAlignment.Center,
                Content = ConfigDocument.Switch(block, key) == true ? "on" : null,
            };
            box.Checked += (_, _) => { box.Content = "on"; doc.SetSwitch(block, key, true); doc.MarkDirty(); UpdateDirty(); };
            box.Unchecked += (_, _) => { box.Content = null; doc.SetSwitch(block, key, false); doc.MarkDirty(); UpdateDirty(); };
            return box;
        }

        if (def.Type == "enum" || def.Ui == "combo")
        {
            var combo = new ComboBox
            {
                IsEditable = def.Values.Length == 0,
                ToolTip = def.Help,
            };
            foreach (var value in def.Values)
                combo.Items.Add(value);
            var current = ConfigDocument.Text(block, key);
            if (def.Values.Length == 0)
                combo.Text = current ?? "";
            else
                combo.SelectedItem = def.Values.FirstOrDefault(v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase));
            combo.ToolTip = new TextBlock { Text = def.Help, MaxWidth = 420, TextWrapping = TextWrapping.Wrap };

            void CommitCombo()
            {
                var text = def.Values.Length == 0 ? combo.Text : combo.SelectedItem?.ToString();
                WriteValue(block, def, key, text);
                doc.MarkDirty();
                UpdateDirty();
            }
            combo.SelectionChanged += (_, _) => { if (def.Values.Length > 0) CommitCombo(); };
            combo.LostFocus += (_, _) => { if (def.Values.Length == 0) CommitCombo(); };
            combo.KeyDown += (_, e) => { if (e.Key == Key.Enter && def.Values.Length == 0) CommitCombo(); };
            return combo;
        }

        if (def.Ui == "slider" && def.Min is double lo && def.Max is double hi)
        {
            var panel = new DockPanel { LastChildFill = true };
            var number = new TextBox { MinWidth = 62, HorizontalAlignment = HorizontalAlignment.Stretch };
            DockPanel.SetDock(number, Dock.Right);
            var slider = new Slider
            {
                Minimum = lo,
                Maximum = hi,
                Width = 230,
                Margin = new Thickness(0, 0, 8, 0),
                IsSnapToTickEnabled = false,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = def.Help,
            };
            var current = NumberFor(block, key);
            slider.Value = current is double c ? Math.Clamp(c, lo, hi) : lo;
            number.Text = current is double shown ? Trim(shown) : "";
            panel.Children.Add(number);
            panel.Children.Add(slider);

            var inside = false;
            slider.ValueChanged += (_, _) =>
            {
                if (inside)
                    return;
                inside = true;
                number.Text = Trim(slider.Value);
                WriteValue(block, def, key, number.Text);
                doc.MarkDirty();
                UpdateDirty();
                inside = false;
            };
            CommitOnLeave(number, text =>
            {
                if (inside)
                    return;
                inside = true;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    slider.Value = Math.Clamp(v, lo, hi);
                WriteValue(block, def, key, text);
                doc.MarkDirty();
                UpdateDirty();
                inside = false;
            });
            return panel;
        }

        if (def.IsList)
        {
            var box = new TextBox
            {
                Text = string.Join(", ", ConfigDocument.List(block, key)),
                ToolTip = new TextBlock
                {
                    Text = def.Help + "\n\nSeparate the items with commas.",
                    MaxWidth = 420,
                    TextWrapping = TextWrapping.Wrap,
                },
            };
            CommitOnLeave(box, text =>
            {
                doc.SetList(block, key, text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries));
                doc.MarkDirty();
                UpdateDirty();
            });
            return box;
        }

        var field = new TextBox
        {
            Text = ConfigDocument.Text(block, key) ?? "",
            ToolTip = new TextBlock { Text = def.Help, MaxWidth = 420, TextWrapping = TextWrapping.Wrap },
        };

        if (def.IsPath)
        {
            var panel = new DockPanel { LastChildFill = true };
            var pick = new Button { Content = "…", Width = 26, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(0) };
            DockPanel.SetDock(pick, Dock.Right);
            panel.Children.Add(pick);
            panel.Children.Add(field);
            pick.Click += (_, _) =>
            {
                var folder = PickFolder(
                    Path.GetDirectoryName(field.Text),
                    key == "model" || def.Type == "path" ? S.ModelsFolder : null,
                    S.ModelsFolder);
                if (def.Type == "dir")
                {
                    var dialog = new OpenFolderDialog { Title = key, InitialDirectory = folder };
                    if (dialog.ShowDialog(W) != true)
                        return;
                    field.Text = dialog.FolderName;
                }
                else
                {
                    var dialog = new OpenFileDialog
                    {
                        Title = key,
                        Filter = key == "model"
                            ? "Model files|*.basalt;*.gguf;*.safetensors;*.bin|All files|*.*"
                            : "All files|*.*",
                        InitialDirectory = folder,
                    };
                    if (dialog.ShowDialog(W) != true)
                        return;
                    field.Text = dialog.FileName;
                    if (key == "model")
                    {
                        S.AddRecentModel(dialog.FileName);
                        S.Save();
                    }
                }
                WriteValue(block, def, key, field.Text);
                doc.MarkDirty();
                BuildModelList();
                UpdateDirty();
            };
            CommitOnLeave(field, text =>
            {
                WriteValue(block, def, key, text);
                doc.MarkDirty();
                BuildModelList();
                UpdateDirty();
            });
            return panel;
        }

        CommitOnLeave(field, text =>
        {
            WriteValue(block, def, key, text);
            doc.MarkDirty();
            UpdateDirty();
        });
        return field;
    }

    /// <summary>Writes a value in the shape the key wants: numbers as numbers, paths and names as text.</summary>
    private void WriteValue(JsonObject block, SettingDef def, string key, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            doc.Set(block, key, null);
            return;
        }
        if (def.IsNumeric || def.Type == "enum" && LooksNumeric(text))
            doc.SetNumber(block, key, text);
        else
            doc.SetText(block, key, text);
    }

    private static bool LooksNumeric(string text)
        => double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    private static double? NumberFor(JsonObject block, string key)
    {
        var text = ConfigDocument.Text(block, key);
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string Trim(double value)
        => value.ToString(value == Math.Truncate(value) ? "0" : "0.####", CultureInfo.InvariantCulture);

    private static void CommitOnLeave(TextBox box, Action<string> commit)
    {
        box.LostFocus += (_, _) => commit(box.Text);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                commit(box.Text);
                e.Handled = true;
            }
        };
    }

    private static TextBlock Header(string text) => new()
    {
        Text = text,
        Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.TryFindResource("TextFaint"),
        FontSize = 11,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 16, 0, 6),
    };

    private Style StyleNamed(string name) => (Style)TryFindResource(name);

    private System.Windows.Media.Brush BrushNamed(string name) => (System.Windows.Media.Brush)TryFindResource(name);

    private void RebuildCurrentPage()
    {
        ShowEntryFields();
        BuildScopeFields(ServerFields, doc.Server, "server", ServerCommon, AddServerFieldCombo);
        BuildScopeFields(EngineFields, doc.Engine, "engine", EngineCommon, AddEngineFieldCombo);
        ServerNote.Text = NoteForServer();
        BuildModelList();
        RebuildProfiles();
        ShowIssues(doc.Validate(catalog));
    }

    // ---- raw JSON ------------------------------------------------------------------------------------------
    private bool OnRawTab => (Tabs.SelectedItem as TabItem)?.Header?.ToString() == "Raw JSON";

    private void ShowRaw() => Dispatcher.InvokeAsync(() =>
    {
        RawBox.Text = doc.ToJson();
        RawBox.CaretIndex = 0;
    });

    private bool ApplyRaw()
    {
        try
        {
            var node = JsonNode.Parse(RawBox.Text, documentOptions: new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (node is not JsonObject obj)
                throw new InvalidDataException("the file must contain one JSON object");
            doc = new ConfigDocument(doc.FilePath, obj, doc.OriginalText);
            doc.MarkDirty();
            RebuildEverything();
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show(W, "The JSON on that tab is not valid:\n\n" + e.Message,
                            "Basalt UI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void ApplyRaw_Click(object sender, RoutedEventArgs e) => ApplyRaw();

    private void Reformat_Click(object sender, RoutedEventArgs e) => ShowRaw();

    private void Check_Click(object sender, RoutedEventArgs e) => ShowIssues(doc.Validate(catalog));

    private void ShowIssues(IEnumerable<ConfigDocument.Issue> issues)
    {
        IssuesList.Items.Clear();
        foreach (var issue in issues)
        {
            IssuesList.Items.Add(new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text = (issue.Error ? "error  " : "note    ") + issue.Where + " — " + issue.Message,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = issue.Error ? BrushNamed("Bad") : BrushNamed("Warn"),
                },
                Padding = new Thickness(0, 1, 0, 1),
            });
        }
    }

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // the tab strip is a hairline strip; the pages live beside it and this flips which one shows
        var leaving = wasOnConfigs;
        wasOnConfigs = OnConfigsTab;
        ShowTab();
        if (!ready)
            return;
        if (leaving && !wasOnConfigs)
        {
            // a config edited while its tab was open has already been written into the entries wearing
            // it, one bake per edit; this last one only catches up if the tab closed before the last
            // keystroke committed — and the tabs being entered then show the entries as they now are
            BakeProfiles();
            RebuildCurrentPage();
        }
        var raw = (Tabs.SelectedItem as TabItem)?.Header?.ToString() == "Raw JSON";
        if (!raw && RawBox.IsLoaded && !string.IsNullOrEmpty(RawBox.Text) && RawBox.Text != doc.ToJson())
        {
            // whatever they typed becomes the document, so the form and the file cannot drift apart
            if (!ApplyRaw())
                return;
        }
        if (raw)
            ShowRaw();
        UpdateDirty();
    }

    private bool wasOnConfigs;

    private void ShowTab()
    {
        var index = Tabs.SelectedIndex;
        PageBasalt.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PageModels.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageConfigs.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageServer.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageEngine.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        PageRaw.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- saving --------------------------------------------------------------------------------------------
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySave())
            return;
        if (RestartAfterSaveCheck.IsChecked == true && app.Vm.IsRunning)
        {
            FootText.Text = "restarting basalt…";
            _ = app.Vm.RestartAsync();
        }
    }

    private bool TrySave()
    {
        CommitSettings();
        if (OnRawTab && RawBox.Text != doc.ToJson() && !ApplyRaw())
            return false;
        BakeProfiles();     // the entries wearing a config carry it into what is saved, whatever tab this came from

        var errors = doc.Validate(catalog).Where(i => i.Error).ToList();
        if (errors.Count > 0 &&
            MessageBox.Show(W,
                            "basalt will probably refuse to start with this config:\n\n" +
                            string.Join("\n", errors.Select(x => "· " + x.Where + " — " + x.Message)) +
                            "\n\nSave it anyway?",
                            "Basalt UI", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;

        var backup = LatestBackupName;
        var error = doc.Save();
        if (error is not null)
        {
            MessageBox.Show(W, "The file could not be saved:\n\n" + error, "Basalt UI",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        doc.MarkSaved();
        app.AfterConfigSaved();
        RebuildEverything();
        if (OnRawTab)
            ShowRaw();
        FootText.Text = backup is null ? "saved" : "saved · previous copy kept as " + backup;
        return true;
    }

    private string? latestBackup;

    private string? LatestBackupName
    {
        get
        {
            var before = latestBackup;
            latestBackup = NewestBackupName();
            return latestBackup == before ? null : latestBackup;
        }
    }

    private static string? NewestBackupName()
    {
        try
        {
            if (!Directory.Exists(AppPaths.BackupsDir))
                return null;
            return new DirectoryInfo(AppPaths.BackupsDir).EnumerateFiles("*.json")
                                                         .OrderByDescending(f => f.LastWriteTimeUtc)
                                                         .FirstOrDefault()?
                                                         .Name;
        }
        catch
        {
            return null;
        }
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        if (!File.Exists(doc.FilePath))
        {
            MessageBox.Show(W, "There is nothing on disk to go back to; this file has not been saved yet.",
                            "Basalt UI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (doc.Dirty &&
            MessageBox.Show(W, "Discard the changes on this page and re-read the file?", "Basalt UI",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        doc = ConfigDocument.Load(doc.FilePath);
        RebuildEverything();
        if (OnRawTab)
            ShowRaw();
        FootText.Text = "re-read from disk";
    }

    private void UpdateDirty()
    {
        // while the Configs tab is open, an edit to a config is also an edit to every entry wearing it:
        // the bake runs right here, so the document the file will get already carries what is shown
        if (ready && OnConfigsTab)
            BakeProfiles();
        DirtyText.Text = doc.Dirty ? "unsaved changes" : "";
    }

    /// <summary>
    /// Leaving the page, the way the old close button used to: unsaved work gets one chance to be
    /// saved, and a save that fails (validation, a locked file) keeps the page up rather than throwing
    /// the work away. False means "stay"; the back arrow honours it.
    /// </summary>
    public bool TryLeave()
    {
        if (ready)
            CommitSettings();
        if (!doc.Dirty)
            return true;

        var save = MessageBox.Show(W, "The config has unsaved changes.\n\nSave them before going back?",
                                   "Basalt UI", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (save == MessageBoxResult.Cancel)
            return false;
        if (save != MessageBoxResult.Yes)
        {
            FootText.Text = "changes left unsaved — coming back re-reads the file";
            return true;
        }
        return TrySave();
    }
}
