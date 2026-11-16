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
    private HfRepo? repo;

    public ModelsBrowserWindow(App app)
    {
        this.app = app;
        InitializeComponent();

        SourceInitialized += (_, _) => Shell.Win11.Round(this, App.IsDarkTheme);
        QueryBox.Text = "basalt";
        FolderText.Text = app.Settings.ModelsFolder;
        Loaded += (_, _) => _ = SearchAsync();
        app.Downloads.ItemChanged += OnTransferChanged;
        app.Downloads.ListChanged += OnTransferListChanged;
        Closed += (_, _) =>
        {
            app.Downloads.ItemChanged -= OnTransferChanged;
            app.Downloads.ListChanged -= OnTransferListChanged;
        };
        RefreshTransfer();
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

            // Only the packs. Readmes, chat templates and licence files may be interesting, but this list
            // is for things that can be pulled and loaded — everything else just buries the model files.
            var rows = files.Where(f => f.IsBasalt).Select(f => new FileRow(f, DestinationFor(f))).ToList();
            FilesList.ItemsSource = rows;
            SyncRows();

            var here = rows.Count(r => r.AlreadyThere);
            PickNote.Text = rows.Count switch
            {
                0 => "no .basalt pack in this repo — the readmes and other files stay out of the list",
                var n => $"{n} pack{(n == 1 ? "" : "s")} here, " +
                         $"{string.Join(", ", rows.Select(r => r.SizeText))}" +
                         (here > 0 ? $" · {here} already in the folder" : ""),
            };
            SetModelButton.IsEnabled = rows.Count > 0;
            DownloadAllButton.IsEnabled = rows.Any(r => !r.AlreadyThere);
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
        var pick = rows.FirstOrDefault();
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
    private void DownloadOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FileRow row || repo is null)
            return;
        app.Downloads.Enqueue(repo.Id, HuggingFace.DownloadUrl(Settings.HfMirror, repo.Id, row.File.Path).ToString(),
                              DestinationFor(row.File), row.File.Size, row.File.Sha256);
    }

    private void DownloadAll_Click(object sender, RoutedEventArgs e)
    {
        if (FilesList.ItemsSource is not IEnumerable<FileRow> rows || repo is null)
            return;

        // everything in the list that is not here yet, smallest first: a pack set arrives whole
        foreach (var row in rows.Where(r => !r.AlreadyThere).OrderBy(r => r.File.Size))
            app.Downloads.Enqueue(repo.Id, HuggingFace.DownloadUrl(Settings.HfMirror, repo.Id, row.File.Path).ToString(),
                                  DestinationFor(row.File), row.File.Size, row.File.Sha256);
    }

    // ---- what the transfers are doing ---------------------------------------------------------------------------
    /// <summary>One tracked file changed: the row for it, if this repo is showing it, and the strip below.</summary>
    private void OnTransferChanged(DownloadItem item)
    {
        if (FilesList.ItemsSource is IEnumerable<FileRow> rows)
            foreach (var row in rows.Where(r => SameFile(r.Destination, item.FilePath)))
                row.RefreshFrom(item);
        RefreshTransfer();
    }

    private void OnTransferListChanged() => RefreshTransfer();

    /// <summary>Give every row its honest state: the download centre's word if it is tracking this file,
    /// otherwise what the folder itself says.</summary>
    private void SyncRows()
    {
        if (FilesList.ItemsSource is not IEnumerable<FileRow> rows)
            return;
        foreach (var row in rows)
        {
            var item = app.Downloads.Items.FirstOrDefault(i => SameFile(i.FilePath, row.Destination));
            if (item is not null)
                row.RefreshFrom(item);
            else
                row.ResetFromDisk();
        }
    }

    /// <summary>The strip at the bottom: whatever transfer the centre is running, whatever page it was
    /// started from — it does not belong to this window, it is just shown here.</summary>
    private void RefreshTransfer()
    {
        var item = app.Downloads.Current;
        PauseButton.Visibility = item is { State: "downloading" } ? Visibility.Visible : Visibility.Collapsed;
        if (item is null)
        {
            JobText.Text = "nothing on its way — downloads live in the centre, bottom of the main page";
            SpeedText.Text = "";
            JobBar.Value = 0;
            return;
        }
        JobText.Text = item.Name + (item.State == "queued" ? " — waiting" : "");
        SpeedText.Text = item.Line;
        JobBar.Value = item.Fraction;
    }

    private void PauseTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (app.Downloads.Current is { } item)
            app.Downloads.Pause(item);
    }

    private static bool SameFile(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
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
            Destination = destination;
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
        public string Destination { get; }
        public string Name => File.Name;
        public string SizeText => File.SizeText;
        public string Where => File.IsBasalt ? File.Folder : "not a model file";

        public string State { get => state; set => Set(ref state, value); }

        public bool AlreadyThere { get => alreadyThere; set => Set(ref alreadyThere, value); }

        /// <summary>One job at a time: a second download would fight this one for the file.</summary>
        public bool CanStart => !busy;

        public bool Busy
        {
            get => busy;
            set { if (Set(ref busy, value)) Raise(nameof(CanStart)); }
        }

        /// <summary>The row takes its words from the transfer tracking this file, wherever it was started.</summary>
        public void RefreshFrom(DownloadItem item)
        {
            AlreadyThere = item.State == "finished";
            Busy = item.State == "downloading";
            State = item.State switch
            {
                "failed" => item.Error.Length > 60 ? item.Error[..60] + "…" : item.Error,
                _ => item.ShortLine,
            };
        }

        /// <summary>No transfer is tracking this file any more — the folder gets its say back.</summary>
        public void ResetFromDisk()
        {
            Busy = false;
            try
            {
                AlreadyThere = File.Size > 0 && new FileInfo(Destination).Length == File.Size;
                State = AlreadyThere ? "in the folder" : "";
            }
            catch
            {
                AlreadyThere = false;
                State = "";
            }
        }
    }
}
