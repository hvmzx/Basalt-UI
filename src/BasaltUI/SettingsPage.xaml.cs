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
    private ConfigDocument fileDoc = null!;      // the real file the engine reads
    private ConfigDocument configDoc = null!;    // the selected named config, as a scratch document
    private string? profileName;
    private JsonObject? entry;
    private bool ready;
    private bool rebuilding;
    private bool committing;
    private bool wasOnConfigs;

    /// <summary>
    /// The document the fields are working on: the real file everywhere except the Configs tab, where it
    /// is the selected config's own document — which is what lets every editor on this page shape a
    /// named config with exactly the code that shapes the file.
    /// </summary>
    private ConfigDocument doc => OnConfigsTab ? configDoc : fileDoc;

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

        mainPreview = new PreviewUI(PreviewCard, PreviewVerdict, PreviewRows, PreviewNote);
        configPreview = new PreviewUI(ConfigPreviewCard, ConfigPreviewVerdict, ConfigPreviewRows, ConfigPreviewNote);

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

        fileDoc = OpenDoc(app.Settings.ConfigPath);
        if (OnConfigsTab)
            LoadConfigDoc();
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
        PathText.Text = OnConfigsTab
            ? $"config “{profileName ?? "?"}” — {ConfigProfiles.StorePath}"
            : doc.FilePath;
        RebuildCurrentPage();
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
                fileDoc = OpenDoc(S.ConfigPath);
                fileDoc.MarkDirty();
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

        fileDoc = ConfigDocument.Blank(dialog.FileName, GuessModelPath());
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

    // ---- the Models tab (and the config's own Models page, which share it) ------------------------------------
    /// <summary>Whichever models list the visible page owns: the main tab's or the selected config's.</summary>
    private ListBox EntryList => OnConfigsTab ? ConfigModelsList : ModelsList;

    private (StackPanel Fields, TextBlock Hint, ComboBox Combo) EntryHosts => OnConfigsTab
        ? (ConfigEntryFields, ConfigModelHint, ConfigAddFieldCombo)
        : (ModelFields, ModelHint, AddFieldCombo);

    private void BuildModelList()
    {
        rebuilding = true;
        var list = EntryList;
        var keep = list.SelectedIndex;
        list.Items.Clear();
        foreach (var item in doc.ModelEntries())
            list.Items.Add(LabelFor(item));
        list.SelectedIndex = list.Items.Count == 0 ? -1 : Math.Clamp(keep, 0, list.Items.Count - 1);
        rebuilding = false;

        entry = doc.ModelEntries().ElementAtOrDefault(list.SelectedIndex);
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
        => EntrySelected(ModelsList);

    private void ConfigModelsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => EntrySelected(ConfigModelsList);

    private void EntrySelected(ListBox list)
    {
        if (rebuilding)
            return;
        entry = doc.ModelEntries().ElementAtOrDefault(list.SelectedIndex);
        ShowEntryFields();
    }

    private void ShowEntryFields()
    {
        var (fields, hint, combo) = EntryHosts;
        if (entry is null)
        {
            fields.Children.Clear();
            hint.Text = "Add a model to get started — the path to a .basalt or .gguf file is enough.";
            combo.IsEnabled = false;
            Preview.Card.Visibility = Visibility.Collapsed;
            previewRun++;                       // a preview already on its way belongs to an entry that is gone
            return;
        }
        hint.Text = "Only what you set here goes into the file; anything you leave out keeps basalt's default.";
        combo.IsEnabled = true;
        BuildScopeFields(fields, entry, "entry", EntryCommon, combo);
        _ = UpdateLoadPreview();
    }

    /// <summary>
    /// The Models tab with one model's entry selected — what the main page's right-click asks for. The
    /// entry is matched by its file's name, the same word the row and the config both carry. A model the
    /// config does not name has no entry to edit, and the page stays where it was.
    /// </summary>
    public void RevealModel(ViewModels.HomeModelRow row)
    {
        Tabs.SelectedIndex = 1;             // the Models tab — leaving the Configs tab persists its config first
        var want = Path.GetFileName(row.Id);
        var index = -1;
        var entries = fileDoc.ModelEntries().ToList();
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
        ModelsList.SelectedIndex = index;   // the entry — SelectionChanged shows its fields
        if (ModelsList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
            item.BringIntoView();
    }

    // ---- the Configs tab -------------------------------------------------------------------------------------
    // Named configs are whole basalt config documents — their own models, server, engine, raw JSON — kept
    // beside the file. One is the active one: the config file on disk currently carries its document.
    // Editing here shapes the selected config's own document (the doc property points at it); saving
    // keeps it in the store, and rewrites the file with it when it is the active one.
    /// <summary>The Configs tab with one config selected — what a model row's right-click asks for.</summary>
    public void RevealProfile(string name)
    {
        Tabs.SelectedIndex = 2;             // the Configs tab — SelectionChanged loads the config's document
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

        var selected = ProfilesList.SelectedItem as string;
        if (selected != profileName)
        {
            if (configDoc is not null && configDoc.Dirty)
                PersistConfigDoc(apply: true);      // the one being left keeps what was typed into it
            profileName = selected;
            LoadConfigDoc();
        }
        ShowProfileState();
        RebuildEverything();
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (rebuilding)
            return;
        if (configDoc is not null && configDoc.Dirty)
            PersistConfigDoc(apply: true);
        profileName = ProfilesList.SelectedItem as string;
        LoadConfigDoc();
        ShowProfileState();
        RebuildEverything();
    }

    /// <summary>The selected config's document, as a scratch document the editors can work on.</summary>
    private void LoadConfigDoc()
    {
        configDoc = profileName is { } name && app.Vm.Profiles.Document(name) is { } root
            ? new ConfigDocument(fileDoc.FilePath, (JsonObject)root.DeepClone(), "")
            : new ConfigDocument(fileDoc.FilePath, ConfigProfiles.BlankDoc(), "");
    }

    /// <summary>What was typed into the config is kept in the store — and when the config is the active
    /// one, the file is rewritten with it, because editing the active config is editing the file.</summary>
    private void PersistConfigDoc(bool apply)
    {
        if (profileName is null)
            return;
        var wasDirty = configDoc.Dirty;
        app.Vm.Profiles.Update(profileName, configDoc.Root);
        configDoc.MarkSaved();
        if (!wasDirty)
            return;
        if (apply && app.Vm.Profiles.Active == profileName)
        {
            var error = app.Vm.ApplyProfile(profileName, null);
            FootText.Text = error is null
                ? $"config “{profileName}” kept in the store and written to the file"
                : $"config “{profileName}” kept in the store, but the file refused it: {error}";
        }
        else
            FootText.Text = $"config “{profileName}” kept in the store";
    }

    private bool SaveConfigDoc()
    {
        if (OnConfigRawTab && ConfigRawBox.Text != configDoc.ToJson() && !ApplyConfigRaw())
            return false;
        if (profileName is null)
        {
            FootText.Text = "no config is selected — pick one on the left, or add one";
            return false;
        }

        var errors = configDoc.Validate(catalog).Where(i => i.Error).ToList();
        if (errors.Count > 0 &&
            MessageBox.Show(W,
                            "basalt will probably refuse to start with this config:\n\n" +
                            string.Join("\n", errors.Select(x => "· " + x.Where + " — " + x.Message)) +
                            "\n\nKeep it anyway?",
                            "Basalt UI", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return false;

        app.Vm.Profiles.Update(profileName, configDoc.Root);
        configDoc.MarkSaved();
        if (app.Vm.Profiles.Active == profileName)
        {
            var error = app.Vm.ApplyProfile(profileName, null);
            if (error is not null)
            {
                MessageBox.Show(W, "The config is kept in the store, but the file could not take it:\n\n" + error,
                                "Basalt UI", MessageBoxButton.OK, MessageBoxImage.Error);
                FootText.Text = "kept in the store; the file refused the change";
                UpdateDirty();
                return false;
            }
            FootText.Text = "saved · the file now carries this config";
        }
        else
            FootText.Text = "saved · it goes into the file when a model attaches it";
        UpdateDirty();
        return true;
    }

    private void ShowProfileState()
    {
        ProfileNameBox.Text = profileName ?? "";
        ProfileNameBox.IsEnabled = profileName is not null;
        if (profileName is null)
        {
            ProfileState.Text = "";
            ProfileAttached.Text = "no config selected — add one here, or make one from a model row's right-click";
            return;
        }
        ProfileState.Text = app.Vm.Profiles.Active == profileName
            ? "● active — the config file carries this one"
            : "not in the file — a model attaching it applies it";
        var wearing = app.Vm.Profiles.AttachedTo(profileName);
        ProfileAttached.Text = wearing.Count == 0
            ? "no model is assigned to it yet"
            : "runs with: " + string.Join(", ", wearing);
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        // a config starts with the machine's own server and engine words and an empty models list —
        // enough to grow into something the engine would start with
        var seed = ConfigProfiles.BlankDoc();
        if (fileDoc.Root["engine"] is JsonObject engine)
            seed["engine"] = (JsonObject)engine.DeepClone();
        if (fileDoc.Root["server"] is JsonObject server)
            seed["server"] = (JsonObject)server.DeepClone();
        var name = app.Vm.Profiles.Create("config", seed);
        RebuildProfiles(name);
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (profileName is null)
            return;
        var name = app.Vm.Profiles.Create(profileName + " copy", app.Vm.Profiles.Document(profileName));
        RebuildProfiles(name);
    }

    private void RemoveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (profileName is null)
            return;
        var name = profileName;
        if (MessageBox.Show(W, $"Delete the config “{name}”?\n\nThe models that ran with it lose the link; " +
                               "the config file is untouched.", "Basalt UI",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        profileName = null;
        app.Vm.Profiles.Delete(name);
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
        app.Vm.Profiles.Rename(profileName, name);   // the models running with it keep running with it
        profileName = name;
        RebuildProfiles(name);
    }

    /// <summary>True while the main tab strip sits on Configs: the doc property points at the selected
    /// config's document, and saving means the store rather than the file.</summary>
    private bool OnConfigsTab => (Tabs.SelectedItem as TabItem)?.Header?.ToString() == "Configs";

    // ---- the load preview ------------------------------------------------------------------------------------
    private int previewRun;

    /// <summary>One load-preview card: whichever page is visible owns the one being shown.</summary>
    private sealed record PreviewUI(Border Card, TextBlock Verdict, StackPanel Rows, TextBlock Note);

    private PreviewUI mainPreview = null!;
    private PreviewUI configPreview = null!;

    private PreviewUI Preview => OnConfigsTab ? configPreview : mainPreview;

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
                Preview.Card.Visibility = Visibility.Collapsed;
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
                Preview.Card.Visibility = Visibility.Visible;
                Preview.Verdict.Text = "could not read this file — " + ex.Message;
                Preview.Rows.Children.Clear();
                Preview.Note.Text = "nothing here blocks anything: saving the config never waits for the preview.";
            });
        }
    }

    /// <summary>Fills the card, unless the selection has moved on since this preview started.</summary>
    private void ShowPreview(int run, LoadPreview.Plan plan)
    {
        if (run != previewRun)
            return;
        Preview.Card.Visibility = Visibility.Visible;
        Preview.Verdict.Text = plan.Verdict;
        Preview.Rows.Children.Clear();
        foreach (var (label, value) in plan.Rows)
            Preview.Rows.Children.Add(PreviewRow(label, value));
        Preview.Note.Text = plan.Note;
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
        EntryList.SelectedIndex = EntryList.Items.Count - 1;
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
        EntryList.SelectedIndex = EntryList.Items.Count - 1;
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

    private void ConfigAddField_Click(object sender, RoutedEventArgs e) => AddFromChooser(ConfigAddFieldCombo, entry);
    private void ConfigAddServerField_Click(object sender, RoutedEventArgs e) => AddFromChooser(ConfigAddServerFieldCombo, configDoc.Server);
    private void ConfigAddEngineField_Click(object sender, RoutedEventArgs e) => AddFromChooser(ConfigAddEngineFieldCombo, configDoc.Engine);

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
        if (OnConfigsTab)
        {
            BuildScopeFields(ConfigServerFields, configDoc.Server, "server", ServerCommon, ConfigAddServerFieldCombo);
            BuildScopeFields(ConfigEngineFields, configDoc.Engine, "engine", EngineCommon, ConfigAddEngineFieldCombo);
            ConfigServerNote.Text = NoteForServer();
            ShowIssues(ConfigIssuesList, configDoc.Validate(catalog));
            ShowProfileState();
        }
        else
        {
            BuildScopeFields(ServerFields, fileDoc.Server, "server", ServerCommon, AddServerFieldCombo);
            BuildScopeFields(EngineFields, fileDoc.Engine, "engine", EngineCommon, AddEngineFieldCombo);
            ServerNote.Text = NoteForServer();
            ShowIssues(IssuesList, fileDoc.Validate(catalog));
        }
        BuildModelList();
        UpdateDirty();
    }

    // ---- raw JSON ------------------------------------------------------------------------------------------
    private bool OnRawTab => (Tabs.SelectedItem as TabItem)?.Header?.ToString() == "Raw JSON";
    private bool OnConfigRawTab => (ConfigTabs.SelectedItem as TabItem)?.Header?.ToString() == "Raw JSON";

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
            var fresh = new ConfigDocument(fileDoc.FilePath, obj, fileDoc.OriginalText);
            fresh.MarkDirty();
            fileDoc = fresh;
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

    private void Check_Click(object sender, RoutedEventArgs e) => ShowIssues(IssuesList, doc.Validate(catalog));

    // the config's own raw JSON: the same shape, pointed at the selected config's document
    private void ShowConfigRaw() => Dispatcher.InvokeAsync(() =>
    {
        ConfigRawBox.Text = configDoc.ToJson();
        ConfigRawBox.CaretIndex = 0;
    });

    private bool ApplyConfigRaw()
    {
        try
        {
            var node = JsonNode.Parse(ConfigRawBox.Text, documentOptions: new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (node is not JsonObject obj)
                throw new InvalidDataException("the config must contain one JSON object");
            var fresh = new ConfigDocument(configDoc.FilePath, obj, configDoc.OriginalText);
            fresh.MarkDirty();
            configDoc = fresh;
            RebuildCurrentPage();
            return true;
        }
        catch (Exception e)
        {
            MessageBox.Show(W, "The JSON on that tab is not valid:\n\n" + e.Message,
                            "Basalt UI", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private void ApplyConfigRaw_Click(object sender, RoutedEventArgs e) => ApplyConfigRaw();

    private void ReformatConfig_Click(object sender, RoutedEventArgs e) => ShowConfigRaw();

    private void CheckConfig_Click(object sender, RoutedEventArgs e) => ShowIssues(ConfigIssuesList, configDoc.Validate(catalog));

    private void ShowIssues(ListBox box, IEnumerable<ConfigDocument.Issue> issues)
    {
        box.Items.Clear();
        foreach (var issue in issues)
        {
            box.Items.Add(new ListBoxItem
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

    /// <summary>The config's own sub-tab strip: same hairline shape, same pages-beside-the-strip trick.</summary>
    private void ConfigTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowConfigTab();
        if (!ready)
            return;
        var raw = OnConfigRawTab;
        if (!raw && ConfigRawBox.IsLoaded && !string.IsNullOrEmpty(ConfigRawBox.Text) && ConfigRawBox.Text != configDoc.ToJson())
        {
            // whatever was typed becomes the config's document, so the form and the store cannot drift apart
            if (!ApplyConfigRaw())
                return;
        }
        if (raw)
            ShowConfigRaw();
    }

    private void ShowConfigTab()
    {
        var index = ConfigTabs.SelectedIndex;
        ConfigPageModels.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfigPageServer.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        ConfigPageEngine.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        ConfigPageRaw.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
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
            // leaving the configs: what was typed into the config is kept in the store first — and when
            // the config is the active one, the file was rewritten with it, so the file tabs re-read it
            var applied = configDoc.Dirty && app.Vm.Profiles.Active == profileName;
            if (configDoc.Dirty)
                PersistConfigDoc(apply: true);
            if (applied)
                fileDoc = OpenDoc(S.ConfigPath);
            RebuildCurrentPage();       // the tabs being entered show the document they edit, as it now is
        }
        else if (!leaving && wasOnConfigs)
        {
            if (profileName is null && app.Vm.Profiles.Names.Count > 0)
                RebuildProfiles(app.Vm.Profiles.Names[0]);   // first visit: the first config is as good a place to start as any
            else
            {
                LoadConfigDoc();        // entering the configs: the selected config's own document
                RebuildEverything();
            }
        }
        var raw = (Tabs.SelectedItem as TabItem)?.Header?.ToString() == "Raw JSON";
        if (!raw && RawBox.IsLoaded && !string.IsNullOrEmpty(RawBox.Text) && RawBox.Text != fileDoc.ToJson())
        {
            // whatever they typed becomes the document, so the form and the file cannot drift apart
            if (!ApplyRaw())
                return;
        }
        if (raw)
            ShowRaw();
        UpdateDirty();
    }

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
        if (OnConfigsTab)
            return SaveConfigDoc();
        if (OnRawTab && RawBox.Text != doc.ToJson() && !ApplyRaw())
            return false;

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
        if (OnConfigsTab)
        {
            if (configDoc.Dirty &&
                MessageBox.Show(W, "Discard the changes to this config and re-read it from the store?",
                                "Basalt UI", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            LoadConfigDoc();
            RebuildCurrentPage();
            FootText.Text = "re-read from the store";
            return;
        }
        if (!File.Exists(fileDoc.FilePath))
        {
            MessageBox.Show(W, "There is nothing on disk to go back to; this file has not been saved yet.",
                            "Basalt UI", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (fileDoc.Dirty &&
            MessageBox.Show(W, "Discard the changes on this page and re-read the file?", "Basalt UI",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        fileDoc = ConfigDocument.Load(fileDoc.FilePath);
        RebuildEverything();
        if (OnRawTab)
            ShowRaw();
        FootText.Text = "re-read from disk";
    }

    private void UpdateDirty()
    {
        DirtyText.Text = OnConfigsTab
            ? (configDoc.Dirty ? $"config “{profileName}” unsaved" : "")
            : fileDoc.Dirty ? "unsaved changes" : "";
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
        if (OnConfigsTab && configDoc.Dirty)
        {
            var keep = MessageBox.Show(W,
                $"The config “{profileName}” has unsaved changes.\n\n" +
                "Keep it before going back? It goes into the store, and into the config file if it is the active one.",
                "Basalt UI", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (keep == MessageBoxResult.Cancel)
                return false;
            if (keep == MessageBoxResult.Yes)
            {
                if (!SaveConfigDoc())
                    return false;
            }
            else
                PersistConfigDoc(apply: false);
        }
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
