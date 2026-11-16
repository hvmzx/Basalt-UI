using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BasaltUI.Core;
using BasaltUI.ViewModels;

namespace BasaltUI;

/// <summary>
/// Hugging Face from inside the app: search, look inside a repo, and pull the files into the models folder
/// the settings already know about. Nothing here is clever about basalt — a <c>.basalt</c> file arrives and
/// the user points a model at it — but it saves the round trip to a browser and the fiddling with shard
/// names, which is where people give up.
/// </summary>
public partial class ModelsBrowserWindow : Window
{
    private readonly App app;
    private CancellationTokenSource? cts;
    private HfRepo? repo;

    public ModelsBrowserWindow(App app)
    {
        this.app = app;
        InitializeComponent();

        SourceInitialized += (_, _) => Shell.Win11.Round(this, App.IsDarkTheme);
        QueryBox.Text = "basalt";
        FolderText.Text = app.Settings.ModelsFolder;
        Loaded += (_, _) => _ = SearchAsync();
        Closed += (_, _) => cts?.Cancel();
    }

    private AppSettings Settings => app.Settings;

    /// <summary>
    /// The packs the basalt author publishes are pinned to the top with a badge, and the list of them is
    /// asked for fresh every time the window opens — a pack the author drops tomorrow shows up without
    /// this app being updated. Only things that look like basalt packs are pinned; the author's other
    /// repos stay out of the way unless a search finds them.
    /// </summary>
    private const string OfficialOwner = "jesdga";

    private IReadOnlyList<RepoRow>? officialCache;

    private static bool IsBasaltPack(HfRepo repo)
        => repo.Id.Contains("basalt", StringComparison.OrdinalIgnoreCase) ||
           repo.Tags.Any(t => t.Contains("basalt", StringComparison.OrdinalIgnoreCase));

    /// <summary>The official packs, remembered for this window once the ask has succeeded once.</summary>
    private async Task<(IReadOnlyList<RepoRow> Rows, string? Error)> OfficialAsync()
    {
        if (officialCache is { } cached)
            return (cached, null);
        try
        {
            var found = await HuggingFace.AuthorAsync(Settings.HfMirror, OfficialOwner, CancellationToken.None).ConfigureAwait(true);
            var rows = found.Where(IsBasaltPack).Select(r => new RepoRow(r, official: true)).ToList();
            officialCache = rows;
            return (rows, null);
        }
        catch (Exception e)
        {
            return ([], Net.Explain(e));
        }
    }

    /// <summary>Official packs first, then whatever the search returned, minus duplicates.</summary>
    private static List<RepoRow> Merge(IReadOnlyList<RepoRow> officials, IReadOnlyList<HfRepo>? found)
    {
        var rows = new List<RepoRow>(officials);
        var seen = officials.Select(r => r.Repo.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (found is not null)
            foreach (var repo in found)
                if (seen.Add(repo.Id))
                    rows.Add(new RepoRow(repo));
        return rows;
    }

    // ---- search -----------------------------------------------------------------------------------------------
    private void Query_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            _ = SearchAsync();
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    private async Task SearchAsync()
    {
        SearchButton.IsEnabled = false;
        SearchFoot.Text = "asking Hugging Face…";
        ReposList.ItemsSource = null;
        FilesList.ItemsSource = null;
        ClearRepo();

        var (officials, officialError) = await OfficialAsync();
        try
        {
            var rows = await HuggingFace.SearchAsync(Settings.HfMirror, QueryBox.Text, 50, CancellationToken.None);
            ReposList.ItemsSource = Merge(officials, rows);
            var packs = officials.Count == 1 ? "1 official pack" : $"{officials.Count} official packs";
            SearchFoot.Text = rows.Count switch
            {
                0 when officials.Count > 0 => $"nothing came back for that — {packs} pinned above",
                0 => "nothing came back for that",
                var n => $"{n} repos, most downloaded first · {packs} pinned above" +
                         (HuggingFace.Token is null ? "" : " · using the token in HF_TOKEN"),
            };
            if (officialError is not null)
                SearchFoot.Text += " · the official packs could not be listed: " + officialError;
        }
        catch (Exception e)
        {
            // a search failing is not the officials' fault: whatever was pinned is still listed
            ReposList.ItemsSource = Merge(officials, null);
            SearchFoot.Text = Net.Explain(e) +
                              (officials.Count > 0 ? " — the official packs are still listed; press Search to try again"
                                                   : " — press Search to try again");
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    // ---- the selected repo -------------------------------------------------------------------------------------
    private void ClearRepo()
    {
        repo = null;
        RepoTitle.Text = "a repo on Hugging Face";
        RepoFacts.Text = "";
        OpenRepoButton.IsEnabled = false;
        SetModelButton.IsEnabled = false;
        DownloadAllButton.IsEnabled = false;
        PickNote.Text = "nothing picked yet — choose a repo on the left, and its model files appear here";
    }

    private async void Repo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReposList.SelectedItem is not RepoRow chosen)
        {
            ClearRepo();
            return;
        }

        repo = chosen.Repo;
        OpenRepoButton.IsEnabled = true;
        RepoTitle.Text = chosen.Repo.NameFirst;
        RepoFacts.Text = chosen.Repo.Id;
        FilesList.ItemsSource = null;
        PickNote.Text = "reading the file list…";

        try
        {
            var files = await HuggingFace.FilesAsync(Settings.HfMirror, chosen.Repo.Id, CancellationToken.None);
            var rows = files.Select(f => new FileRow(f, DestinationFor(f))).ToList();
            FilesList.ItemsSource = rows;

            var loadable = rows.Count(r => r.File.IsLoadable);
            var here = rows.Count(r => r.AlreadyThere);
            PickNote.Text = loadable switch
            {
                0 => "nothing in basalt or gguf format here — the files may still be useful to look at",
                var n => $"{n} model file{(n == 1 ? "" : "s")} here, " +
                         $"{string.Join(", ", rows.Where(r => r.File.IsLoadable).Select(r => r.SizeText))}" +
                         (here > 0 ? $" · {here} already in the folder" : ""),
            };
            SetModelButton.IsEnabled = loadable > 0;
            DownloadAllButton.IsEnabled = rows.Any(r => r.File.IsLoadable && !r.AlreadyThere);
        }
        catch (Exception ex)
        {
            PickNote.Text = Net.Explain(ex) + " — choose the repo again to try once more";
            DownloadAllButton.IsEnabled = false;
        }
    }

    private string DestinationFor(HfFile file) => Path.Combine(Settings.ModelsFolder, file.Name);

    // ---- the load preview ------------------------------------------------------------------------------------
    private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilesList.SelectedItem is not FileRow row || !row.File.IsLoadable || row.File.Size <= 0)
        {
            PreviewCard.Visibility = Visibility.Collapsed;
            return;
        }

        var plan = LoadPreview.ForRemote(row.File.Name, row.File.Size, PreviewOptions(row.File.Name));
        PreviewCard.Visibility = Visibility.Visible;
        PreviewVerdict.Text = plan.Verdict;
        PreviewRows.Children.Clear();
        foreach (var (label, value) in plan.Rows)
            PreviewRows.Children.Add(PlanRow(label, value));
        PreviewNote.Text = plan.Note;
    }

    /// <summary>
    /// The context a preview needs, taken from the config's first model entry — its kv, context, slots,
    /// vision and GPU — plus whether the engine keeps experts resident in RAM. No entry yet, or a field
    /// left out, means basalt's own default.
    /// </summary>
    private LoadPreview.Options PreviewOptions(string name)
    {
        var entry = app.Config?.ModelEntries().FirstOrDefault() ?? new JsonObject();
        var engine = app.Config?.Engine ?? new JsonObject();
        return new LoadPreview.Options(
            name, 0,
            ConfigDocument.Text(entry, "kv"),
            ConfigDocument.Int(entry, "max-context") ?? 0,
            ConfigDocument.Int(entry, "parallel") ?? 1,
            ConfigDocument.Text(entry, "vision"),
            ConfigDocument.Switch(engine, "resident-experts") != false,
            GpuIndex(entry),
            Settings.ModelsFolder,
            ConfigDocument.Int(engine, "kv-resident") ?? 0);
    }

    private static int GpuIndex(JsonObject entry) =>
        int.TryParse(ConfigDocument.Text(entry, "gpu"), out var gpu) ? Math.Max(0, gpu) : 0;

    private static FrameworkElement PlanRow(string label, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var res = Application.Current.Resources;
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = (Brush)res["TextSecondary"],
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        });
        var right = new TextBlock
        {
            Text = value,
            FontSize = 12,
            Foreground = (Brush)res["TextPrimary"],
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    // ---- buttons at the top of the file list -------------------------------------------------------------------
    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        if (repo is null)
            return;
        Open(HuggingFace.Base(Settings.HfMirror) + "/" + repo.Id);
    }

    private void SetModel_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.ItemsSource is not IEnumerable<FileRow> rows)
            return;

        // The point of this menu item is that the path is in the clipboard before the user has to type it.
        var pick = rows.FirstOrDefault(r => r.File.IsBasalt) ?? rows.FirstOrDefault(r => r.File.IsLoadable);
        if (pick is null)
            return;

        Clipboard.SetText(Settings.ModelsFolder);
        PickNote.Text = $"models folder copied — in Settings → Models add a model and point its path at {pick.File.Name}, once it has come down";
    }

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        // settings is a page in the main window now, so the browser steps aside for it rather than
        // stacking another modal over this one
        Close();
        app.ShowSettings();

        // and the folder line is the one thing in this window that is only true until settings change
        FolderText.Text = Settings.ModelsFolder;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Settings.ModelsFolder);
            Open(Settings.ModelsFolder);
        }
        catch (Exception ex)
        {
            PickNote.Text = "could not open that folder — " + Net.Explain(ex);
        }
    }

    private static void Open(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch
        {
            // a folder that will not open is not worth a dialog
        }
    }

    // ---- downloading ---------------------------------------------------------------------------------------------
    private async void DownloadOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FileRow row)
            return;
        await Pull(new[] { row });
    }

    private async void DownloadAll_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.ItemsSource is not IEnumerable<FileRow> rows)
            return;

        // everything basalt could read, smallest first: a shard set arrives whole, and a mistake is cheap early on
        var wanted = rows.Where(r => r.File.IsLoadable && !r.AlreadyThere)
                         .OrderBy(r => r.File.Size).ToList();
        if (wanted.Count == 0)
            return;

        await Pull(wanted);
    }

    private async Task Pull(IReadOnlyList<FileRow> rows)
    {
        cts?.Cancel();
        cts = new CancellationTokenSource();
        var ct = cts.Token;

        foreach (var row in rows)
            row.Busy = true;
        CancelButton.Visibility = Visibility.Visible;
        DownloadAllButton.IsEnabled = false;

        var failed = 0;
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested)
                break;

            JobText.Text = row.Name;
            var job = new DownloadJob(
                HuggingFace.DownloadUrl(Settings.HfMirror, repo?.Id ?? "", row.File.Path),
                DestinationFor(row.File), row.File.Sha256, row.File.Size, row.File.Name);

            var progress = new Progress<DownloadProgress>(p => Show(row, p));
            var error = await SegmentedDownload.RunAsync(job, Settings.DownloadConnections, progress, Trace, ct);

            row.Busy = false;
            if (error is null && !ct.IsCancellationRequested)
            {
                row.AlreadyThere = true;
                row.State = "in the folder";
                JobText.Text = row.Name + " is in " + Settings.ModelsFolder;
                SpeedText.Text = "";
                JobBar.Value = 1;
            }
            else if (error is not null)
            {
                failed++;
                row.State = error.Length > 60 ? error[..60] + "…" : error;
                JobText.Text = row.Name + ": " + error;
            }
        }

        CancelButton.Visibility = Visibility.Collapsed;
        foreach (var row in rows)
            row.Busy = false;
        if (!ct.IsCancellationRequested && failed == 0)
            JobText.Text = "done — add the file to a model in Settings → Models, then restart basalt";
        DownloadAllButton.IsEnabled = FilesList.ItemsSource is IEnumerable<FileRow> all &&
                                      all.Any(r => r.File.IsLoadable && !r.AlreadyThere);
    }

    private void Show(FileRow row, DownloadProgress p)
    {
        JobBar.Value = p.Fraction;
        JobText.Text = row.Name;
        SpeedText.Text = p.Text + (p.ActiveConnections > 1 ? $" · {p.ActiveConnections} connections" : "");
        row.State = p.Phase is "checking" or "done" ? p.Text : p.SpeedText;
    }

    private void Trace(string line)
        => Dispatcher.BeginInvoke(() => TraceText.Text = line);

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        cts?.Cancel();
        CancelButton.Visibility = Visibility.Collapsed;
        JobText.Text = "stopped — run the file again to carry on from where it stopped";
        SpeedText.Text = "";
    }

    // ---- rows ---------------------------------------------------------------------------------------------------
    private sealed class RepoRow
    {
        public RepoRow(HfRepo repo, bool official = false)
        {
            Repo = repo;
            Official = official;
            LikeMark = official ? ""
                       : repo.Private ? "needs an account"
                       : repo.Likes > 0 ? "♥ " + repo.Likes
                       : "";
        }

        public HfRepo Repo { get; }
        public bool Official { get; }
        public string Name => Repo.NameFirst;
        public string Owner => Repo.Owner;
        public string Facts => Repo.FactsText;
        public string LikeMark { get; }
    }

    private sealed class FileRow : ObservableObject
    {
        private string state = "";
        private bool alreadyThere;
        private bool busy;

        public FileRow(HfFile file, string destination)
        {
            File = file;
            try
            {
                AlreadyThere = file.Size > 0 && new FileInfo(destination).Length == file.Size;
                state = AlreadyThere ? "in the folder" : "";
            }
            catch
            {
                // a missing destination is the normal case
            }
        }

        public HfFile File { get; }
        public string Name => File.Name;
        public string SizeText => File.SizeText;
        public string Where => File.IsLoadable ? File.Folder : "not a model file";

        public string State { get => state; set => Set(ref state, value); }

        public bool AlreadyThere { get => alreadyThere; set => Set(ref alreadyThere, value); }

        /// <summary>One job at a time: a second download would fight this one for the file.</summary>
        public bool CanStart => !busy;

        public bool Busy
        {
            get => busy;
            set { if (Set(ref busy, value)) Raise(nameof(CanStart)); }
        }
    }
}
