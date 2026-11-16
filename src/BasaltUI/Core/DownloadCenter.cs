using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BasaltUI.Core;

/// <summary>
/// One entry in the downloads centre: a file being pulled from Hugging Face, or one that already
/// arrived. Everything the user sees about a transfer — percent, speed, state — comes off this object,
/// and it lives for the whole app run, so a download does not belong to the window or the page that
/// started it.
/// </summary>
public sealed class DownloadItem : BasaltUI.ViewModels.ObservableObject
{
    public DownloadItem(string repo, string url, string filePath, long totalBytes, string sha256, DateTime started)
    {
        Repo = repo;
        Url = url;
        FilePath = filePath;
        TotalBytes = totalBytes;
        Sha256 = sha256;
        Started = started;
    }

    /// <summary>The repo it came from, for the history line.</summary>
    public string Repo { get; }

    /// <summary>The full download address — kept absolute so a resume needs nothing else.</summary>
    public string Url { get; }

    /// <summary>Where the finished file lands, and the .part file beside it.</summary>
    public string FilePath { get; }

    public long TotalBytes { get; }
    public string Sha256 { get; }
    public DateTime Started { get; }

    public string Name => Path.GetFileName(FilePath);

    private string state = "queued";            // queued · downloading · paused · finished · failed
    public string State
    {
        get => state;
        set
        {
            if (Set(ref state, value))
                RaiseAll(nameof(IsQueued), nameof(IsDownloading), nameof(IsPaused), nameof(IsDoneOrFailed));
        }
    }

    public bool Active => State is "queued" or "downloading";
    public bool IsQueued => State == "queued";
    public bool IsDownloading => State == "downloading";
    public bool IsPaused => State == "paused";
    public bool IsDoneOrFailed => State is "finished" or "failed";

    private long done;
    public long DoneBytes
    {
        get => done;
        set
        {
            if (Set(ref done, value))
                Raise(nameof(PercentText));
        }
    }

    private double bps;
    public double BytesPerSecond
    {
        get => bps;
        set => Set(ref bps, value);
    }

    private string error = "";
    public string Error
    {
        get => error;
        set => Set(ref error, value);
    }

    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)done / TotalBytes, 0, 1) : 0;

    public string SizeText => TotalBytes > 0 ? BasaltLocator.HumanBytes(TotalBytes) : "";
    public string PercentText => TotalBytes > 0 ? $"{done * 100.0 / TotalBytes:0.#}%" : "";
    public string SpeedText => bps > 0 ? BasaltLocator.HumanBytes((long)bps) + "/s" : "—";

    /// <summary>The one line the row and the transfer strip both read.</summary>
    public string Line => State switch
    {
        "queued" => "waiting — the downloads ahead of it finish first",
        "downloading" => TotalBytes > 0
            ? $"{PercentText} · {BasaltLocator.HumanBytes(done)} of {SizeText} · {SpeedText}"
            : $"{BasaltLocator.HumanBytes(done)} · {SpeedText}",
        "paused" => TotalBytes > 0 ? $"paused at {PercentText} of {SizeText}" : "paused",
        "finished" => "in the models folder",
        "failed" => "failed — " + (error.Length > 0 ? error : "the reason is in the log"),
        _ => "",
    };

    /// <summary>A shorter line for a narrow column: the state and the percent, no bytes.</summary>
    public string ShortLine => State switch
    {
        "queued" => "waiting",
        "downloading" => TotalBytes > 0 ? $"downloading {PercentText}" : "downloading",
        "paused" => TotalBytes > 0 ? $"paused {PercentText}" : "paused",
        "finished" => "in the folder",
        "failed" => "failed",
        _ => "",
    };

    // ---- running machinery, not for display -------------------------------------------------------------------
    public IProgress<DownloadProgress>? Report;
    public CancellationTokenSource? Cancel;

    /// <summary>One download per file at a time: a resumed item waits for the cancelled run to unwind
    /// before its own run opens the .part file.</summary>
    public readonly SemaphoreSlim RunGate = new(1, 1);
}

/// <summary>
/// The download manager. It owns every transfer the app starts, keeps them in one serial queue, and
/// remembers the list on disk, so the browser window, the page it was opened from, even the whole app
/// going away does not stop a download — only the user does, from the centre or the browser's pause
/// button. On the next start anything that was mid-flight picks up from where the bytes stopped,
/// because the segmented downloader keeps its own checkpoint beside the .part file.
/// </summary>
public sealed class DownloadCenter
{
    private readonly AppSettings settings;

    public DownloadCenter(AppSettings settings) => this.settings = settings;

    /// <summary>Everything this app has pulled lately — the active queue and the history below it.</summary>
    public ObservableCollection<DownloadItem> Items { get; } = [];

    /// <summary>One item changed — for rows elsewhere that track a specific file.</summary>
    public event Action<DownloadItem>? ItemChanged;

    /// <summary>The shape of the whole list changed — for the footer link and the queue strip.</summary>
    public event Action? ListChanged;

    public int ActiveCount => Items.Count(i => i.Active);

    public DownloadItem? Current => Items.OrderBy(i => i.Started)
                                         .FirstOrDefault(i => i.State is "downloading" or "queued");

    private string StorePath => Path.Combine(AppPaths.Root, "downloads.json");

    // ---- starting ------------------------------------------------------------------------------------------
    /// <summary>
    /// Add a file to the queue and get the queue moving. An item for the same file that is still around
    /// (waiting, running, paused or even failed) is resumed rather than duplicated; only a finished one
    /// counts as done, and the caller normally filters those out before asking.
    /// </summary>
    public DownloadItem Enqueue(string repo, string url, string filePath, long totalBytes, string sha256)
    {
        var item = Items.FirstOrDefault(i => SamePath(i.FilePath, filePath) && i.State != "finished");
        if (item is null)
        {
            item = new DownloadItem(repo, url, filePath, totalBytes, sha256, DateTime.Now);
            Items.Insert(0, item);                        // newest at the top, like a download manager
            Save();
        }
        else
        {
            item.Error = "";
            if (item.State is not ("queued" or "downloading"))
                Resume(item);
        }
        ItemChanged?.Invoke(item);
        ListChanged?.Invoke();
        Kick();
        return item;
    }

    // ---- user commands -------------------------------------------------------------------------------------
    /// <summary>The user pressed pause — the only thing that stops a download from the app's side.</summary>
    public void Pause(DownloadItem item)
    {
        if (!item.Active)
            return;
        item.State = "paused";
        item.Cancel?.Cancel();
        AppPaths.Log($"download paused: {item.Name}");
        Save();
        ItemChanged?.Invoke(item);
        ListChanged?.Invoke();
    }

    public void Resume(DownloadItem item)
    {
        if (item.State is "queued" or "downloading")
            return;
        item.Error = "";
        item.State = "queued";
        Save();
        ItemChanged?.Invoke(item);
        ListChanged?.Invoke();
        Kick();
    }

    /// <summary>Take one entry out of the list. A paused or failed one keeps its .part file, so the
    /// same file can be queued again later and carry on.</summary>
    public void Remove(DownloadItem item)
    {
        if (item.State == "downloading")
            Pause(item);
        Items.Remove(item);
        Save();
        ListChanged?.Invoke();
    }

    public void ClearHistory()
    {
        for (var i = Items.Count - 1; i >= 0; i--)
            if (Items[i].IsDoneOrFailed)
                Items.RemoveAt(i);
        Save();
        ListChanged?.Invoke();
    }

    /// <summary>App exit: stop the network work, but leave every mid-flight item saying "downloading" —
    /// the save keeps that state, and the next start resumes it.</summary>
    public void StopForExit()
    {
        foreach (var item in Items.Where(i => i.State == "downloading").ToList())
            item.Cancel?.Cancel();
        Save();
    }

    // ---- the queue -----------------------------------------------------------------------------------------
    private bool pumping;

    /// <summary>Serial on purpose: several multi-gigabyte pulls would fight each other for the disk.</summary>
    private void Kick()
    {
        if (pumping)
            return;
        pumping = true;
        _ = PumpAsync();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (true)
            {
                var item = Items.OrderBy(i => i.Started).FirstOrDefault(i => i.State == "queued");
                if (item is null)
                    return;
                await RunAsync(item);
            }
        }
        finally
        {
            pumping = false;
            ListChanged?.Invoke();
        }
    }

    private async Task RunAsync(DownloadItem item)
    {
        await item.RunGate.WaitAsync();
        try
        {
            var cts = new CancellationTokenSource();
            item.Cancel = cts;
            item.State = "downloading";
            // Created here on the UI thread, so its callbacks land there too — the items are bound to the page.
            item.Report = new Progress<DownloadProgress>(p =>
            {
                item.DoneBytes = p.Done;
                item.BytesPerSecond = p.BytesPerSecond;
                ItemChanged?.Invoke(item);
            });
            ListChanged?.Invoke();

            var job = new DownloadJob(new Uri(item.Url), item.FilePath, item.Sha256, item.TotalBytes, item.Name);
            var error = await SegmentedDownload.RunAsync(job, settings.DownloadConnections, item.Report,
                                                         line => AppPaths.Log(line), cts.Token);

            // Only the run that still owns the item may tell its story: a pause or a resume since the
            // download started has already said what happened, or handed the item to a newer run.
            if (ReferenceEquals(item.Cancel, cts))
            {
                item.Cancel = null;
                if (item.State == "downloading")            // this run was still the current one
                {
                    if (error is null)
                    {
                        item.DoneBytes = item.TotalBytes;
                        item.BytesPerSecond = 0;
                        item.State = "finished";
                    }
                    else
                    {
                        item.Error = error;
                        item.BytesPerSecond = 0;
                        item.State = "failed";
                    }
                    Save();
                    ItemChanged?.Invoke(item);
                }
            }
        }
        finally
        {
            item.RunGate.Release();
            ListChanged?.Invoke();
            Kick();                                         // whatever is waiting in the queue, it goes now
        }
    }

    // ---- persistence ---------------------------------------------------------------------------------------
    /// <summary>
    /// The list outlives the app: what came down stays in the history, and anything that was on its way
    /// is recorded as still on its way, so the next start picks it up again.
    /// </summary>
    public void Save()
    {
        try
        {
            var list = new JsonArray();
            foreach (var item in Items.OrderBy(i => i.Started))
            {
                list.Add(new JsonObject
                {
                    ["repo"] = item.Repo,
                    ["url"] = item.Url,
                    ["path"] = item.FilePath,
                    ["bytes"] = item.TotalBytes,
                    ["sha256"] = item.Sha256,
                    ["state"] = item.State,
                    ["started"] = item.Started.ToString("o"),
                });
            }
            File.WriteAllText(StorePath, list.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            AppPaths.Log("downloads save: " + e.Message);
        }
    }

    /// <summary>Restore the list at startup and give the queue its head: mid-flight items resume by
    /// themselves, exactly because the user never asked them to stop.</summary>
    public void LoadSaved()
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(StorePath)) is not JsonArray list)
                return;
            var resumed = false;
            foreach (var node in list)
            {
                if (node is not JsonObject o) continue;
                var url = (string?)o["url"];
                var path = (string?)o["path"];
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(path))
                    continue;
                var item = new DownloadItem(
                    (string?)o["repo"] ?? "",
                    url, path,
                    o["bytes"]?.GetValue<long>() ?? 0,
                    (string?)o["sha256"] ?? "",
                    DateTime.TryParse((string?)o["started"], out var at) ? at : DateTime.Now);
                var state = (string?)o["state"];
                item.State = state switch
                {
                    "paused" => "paused",
                    "finished" => "finished",
                    "failed" => "failed",
                    _ => "queued",                      // queued or downloading: it is on its way again
                };
                Items.Insert(0, item);
                resumed |= item.State == "queued";
            }
            if (resumed)
                Kick();
        }
        catch (Exception e)
        {
            AppPaths.Log("downloads load: " + e.Message);
        }
    }

    private static bool SamePath(string a, string b)
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
}
