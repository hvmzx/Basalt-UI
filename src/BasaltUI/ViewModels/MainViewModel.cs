using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using BasaltUI.Core;
using BasaltUI.Shell;

namespace BasaltUI.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>One reading usually moves twenty read-outs; raising them together keeps the UI honest.</summary>
    protected void RaiseAll(params string[] names)
    {
        var handler = PropertyChanged;
        if (handler is null)
            return;
        foreach (var name in names)
            handler(this, new PropertyChangedEventArgs(name));
    }
}

public sealed record LogLine(DateTime At, string Text, LogSource Source)
{
    public string Time => At.ToString("HH:mm:ss");
}

public enum LogSource { Server, App, Error }

/// <summary>
/// Everything the window shows and can do. The controller side (process, polling, config) is handed in, so the
/// tray and the window act on the same state.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly BasaltProcess process;
    private readonly BasaltClient client;
    private readonly StatusPoller poller;
    private readonly Func<ConfigDocument?> getConfig;
    private readonly Action reopenConfig;
    private readonly Action<string> trace;

    private ServerStatus status = ServerStatus.Down("");

    /// <summary>
    /// The app's own record of how fast each model has actually run, per model, per session of the engine:
    /// it is where the Models card's per-model number and the history window come from. The beat that draws
    /// the screen feeds it, so the recorder can never be told about a request the screen was not told about.
    /// </summary>
    private readonly ThroughputRecorder throughput = new();
    public ThroughputRecorder Throughput => throughput;

    /// <summary>
    /// RAM and GPU memory as Windows measures them, refreshed with every beat — the same figures Task Manager
    /// shows, and available whether or not a server is answering. The engine's own memory numbers stay in the
    /// stats it reports; the machine rows on this window are the machine's.
    /// </summary>
    private MachineMemory.Sample machine = MachineMemory.Read();

    private string logFilter = "";

    public AppSettings Settings { get; }

    public MainViewModel(AppSettings settings, BasaltProcess process, BasaltClient client, StatusPoller poller,
                         Func<ConfigDocument?> getConfig, Action reopenConfig, Action<string> trace)
    {
        Settings = settings;
        this.process = process;
        this.client = client;
        this.poller = poller;
        this.getConfig = getConfig;
        this.reopenConfig = reopenConfig;
        this.trace = trace;

        process.StateChanged += OnProcessState;
        poller.StatusChanged += OnStatus;      // thread-pool: the handlers check the dispatcher

        MaybeRescanLocalModels(force: true);   // the Models card has something to show before the first beat

        StartCommand = new RelayCommand(async () => await StartAsync(), () => !process.IsAlive, "Start");
        StopCommand = new RelayCommand(async () => await StopAsync(), () => process.IsAlive, "Stop");
        RestartCommand = new RelayCommand(async () => await RestartAsync(), () => process.IsAlive, "Restart");
        LoadCommand = new RelayCommand(async () => await ControlAsync("/load", null, "load"), () => status.Reachable && !status.Loaded, "Load");
        UnloadCommand = new RelayCommand(async () => await ControlAsync("/unload", null, "unload"), () => status.Reachable && status.Loaded, "Unload");
        OpenWebUiCommand = new RelayCommand(() => OpenWebUi(), () => status.Reachable, "web ui");
        OpenApiCommand = new RelayCommand(() => OpenApi(), () => status.Reachable, "json");
        SettingsCommand = new RelayCommand(() => SettingsOpened?.Invoke(), name: "settings");
        ReloadConfigCommand = new RelayCommand(() =>
        {
            reopenConfig();
            ReapplyPolling();
        }, name: "reload config");
    }

    // ---- commands ---------------------------------------------------------------------------------------
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand UnloadCommand { get; }
    public ICommand OpenWebUiCommand { get; }
    public ICommand OpenApiCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ReloadConfigCommand { get; }

    public event Action? SettingsOpened;
    public event Action<string>? Notice;

    // ---- the screen ---------------------------------------------------------------------------------------
    public ServerStatus Status
    {
        get => status;
        private set
        {
            if (!Set(ref status, value))
                return;
            Raise(nameof(RunStateText));
            Raise(nameof(StatusText));
            Raise(nameof(StatusClasses));
            Raise(nameof(PrimaryClass));
            Raise(nameof(ModelsEmptyText));
            Raise(nameof(IsConnected));       // anything bound to it follows the same beat
            foreach (var cmd in new ICommand[] { StopCommand, RestartCommand, LoadCommand, UnloadCommand, OpenWebUiCommand, OpenApiCommand })
                (cmd as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsConnected => status.Reachable;
    public bool IsRunning => process.IsAlive;

    /// <summary>
    /// What the Models card says when its list is empty. The list carries the models a running server
    /// serves, plus the files in the models folder, so an empty list really does mean there is nothing
    /// to show: no server output and an empty folder.
    /// </summary>
    public string ModelsEmptyText => status.Reachable
        ? "no models here yet — Discover more… to fetch one"
        : process.IsAlive
            ? "waiting for the server — the models it serves show up here once it answers"
            : "the models folder is empty — Discover more… to fetch one";

    /// <summary>One short word for the header pill.</summary>
    public string PrimaryClass => status.Reachable
        ? status.Loading ? "loading"
        : status.Busy ? "busy"
        : status.Loaded ? "ready"
        : "idle"
        : process.IsAlive ? "starting" : "stopped";

    public string RunStateText
    {
        get
        {
            if (!status.Reachable)
                return process.State switch
                {
                    RunState.Starting => "starting",
                    RunState.Stopping => "stopping",
                    RunState.Failed => "failed",
                    _ => "stopped",
                };
            if (status.Swap is not null)
                return "swapping";
            if (status.Loading)
                return status.LoadPercent is { } p ? $"loading {p:0}%" : "loading";
            if (status.Busy)
                return "generating";
            return status.Loaded ? "ready" : "idle";
        }
    }

    /// <summary>The line beside the state pill: how many of the named configs are serving, and how many
    /// models those configs define. The model name and request counts belong to the cards, not here.</summary>
    public string StatusText
    {
        get
        {
            if (!status.Reachable)
            {
                var detail = process.StateDetail;
                return detail.Equals(RunStateText, StringComparison.OrdinalIgnoreCase) ? "" : detail;
            }
            var count = runningProfiles.Count;
            if (count == 0)
                return "";
            var models = runningProfiles.Sum(n =>
                (Profiles.Document(n)?["models"] as JsonArray)?.Count ?? 0);
            return $"{count} config{(count == 1 ? "" : "s")} ({models} model{(models == 1 ? "" : "s")})";
        }
    }

    public string[] StatusClasses
    {
        get
        {
            var list = new List<string> { PrimaryClass };
            if (!string.IsNullOrEmpty(status.Spill))
                list.Add("spill");
            if (status.Gpus.FirstOrDefault() is { } gpu && gpu.Temp >= 80)
                list.Add("hot");
            return [.. list];
        }
    }

    // charts: what the window draws from
    public double[] DecodeSeries => status.TokSHistory;
    public double[] PrefillSeries => status.PrefillHistory;
    public double[] GpuUtilSeries => status.GpuUtilHistory;
    public double[] GpuPowerSeries => status.GpuPowerHistory;
    public double[] GpuTempSeries => status.GpuTempHistory;
    public string Url => client.BaseUrl;

    // gauges and one-line read-outs; the window binds these instead of doing arithmetic in XAML
    private GpuRow? Gpu => status.Gpus.Count > 0 ? status.Gpus[0] : null;

    public string DecodeValue => status.TokS is { } t ? t.ToString("0.0") + " tok/s" : "—";
    public string PrefillValue => status.PrefillTokS is { } p ? p.ToString("0") + " tok/s" : "—";

    /// <summary>
    /// The session's whole decode, in the engine's own totals since it started: what it averaged, and the
    /// fastest it ever got. The live sparkline shows only the moment, so these two numbers are the only
    /// place the session's shape is visible.
    /// </summary>
    public string DecodeStatsText
    {
        get
        {
            if (status.Totals is { Requests: > 0 })
                return $"decode avg {status.Totals.AverageDecodeTokS:0.#} · max {status.Best?.DecodeTokS ?? 0:0.#} tok/s";
            var rows = status.Requests.Where(r => r.DecodeTokS > 0).Select(r => r.DecodeTokS).ToList();
            if (rows.Count == 0)
                return "decode — not yet";
            return $"decode avg {rows.Average():0.#} · max {rows.Max():0.#} tok/s (recent)";
        }
    }

    /// <summary>The session's whole prefill: what the engine averaged while reading fresh prompts, and its record.</summary>
    public string PrefillStatsText
    {
        get
        {
            if (status.Totals is { Requests: > 0 })
                return $"prefill avg {status.Totals.AveragePrefillTokS:N0} · max {status.Best?.PrefillTokS ?? 0:N0} tok/s";
            var rows = status.Requests.Where(r => r.PrefillTokS > 0).Select(r => r.PrefillTokS).ToList();
            if (rows.Count == 0)
                return "prefill — not yet";
            return $"prefill avg {rows.Average():N0} · max {rows.Max():N0} tok/s (recent)";
        }
    }
    public string GpuNameText => Gpu?.Name is { Length: > 0 } name ? name
                               : machine.Video.Name is { Length: > 0 } card ? card
                               : "no gpu reported";
    public double GpuUtilFraction => Math.Clamp((Gpu?.Util ?? 0) / 100.0, 0, 1);
    public string GpuUtilValue => Gpu is { } g ? g.Util.ToString("0") + " %" : "—";
    // The card's memory in Windows' words: what the card carries (DXGI) and what fills it (the performance
    // counter Task Manager's GPU page samples). nvidia-smi's total counts the driver's own carve-out on top,
    // so it never matched that page; the engine's NVML pair is used only when neither OS half could answer,
    // and the chip then reads from that source instead.
    private (long Used, long Total) VramNow => machine.Video.Known
        ? (machine.Video.InUse, machine.Video.Total)
        : Gpu is { MemTotal: > 0 } g ? (g.MemUsed, g.MemTotal) : (0, 0);

    public double GpuMemFraction { get { var v = VramNow; return v.Total > 0 ? Math.Clamp((double)v.Used / v.Total, 0, 1) : 0; } }
    public string GpuMemText
    {
        get
        {
            var v = VramNow;
            return v.Total > 0 ? BasaltLocator.HumanBytes(v.Used) + " / " + BasaltLocator.HumanBytes(v.Total) : "—";
        }
    }
    public double GpuTempFraction => Math.Clamp((Gpu?.Temp ?? 0) / 100.0, 0, 1);
    public bool GpuTempHot => (Gpu?.Temp ?? 0) >= 80;
    public string GpuTempValue => Gpu?.Temp is { } t ? t.ToString("0") + " °C" : "—";
    public double GpuPowerFraction => Gpu is { PowerLimit: > 0 } g ? Math.Clamp(g.Power / g.PowerLimit, 0, 1) : 0;
    public string GpuPowerText => Gpu is { PowerLimit: > 0 } g
        ? g.Power.ToString("0") + " / " + g.PowerLimit.ToString("0") + " W"
        : Gpu is { } one ? one.Power.ToString("0") + " W" : "—";
    public double CpuFraction => Math.Clamp(status.CpuPercent / 100.0, 0, 1);
    public string CpuText => status.CpuPercent.ToString("0") + " %";
    // In use of usable physical memory — the same two numbers Task Manager's memory page leads with, from
    // Windows rather than from the engine.
    public double RamFraction => machine.Memory.Usable > 0
        ? Math.Clamp((double)machine.Memory.InUse / machine.Memory.Usable, 0, 1)
        : 0;
    public string RamText => machine.Memory.Usable > 0
        ? BasaltLocator.HumanBytes(machine.Memory.InUse) + " / " + BasaltLocator.HumanBytes(machine.Memory.Usable)
        : "—";
    public double ExpertFraction => status.ExpertTotal > 0
        ? Math.Clamp((double)status.ExpertSlots / status.ExpertTotal, 0, 1)
        : 0;
    public string ExpertText => status.ExpertTotal > 0
        ? status.ExpertSlots + " / " + status.ExpertTotal + " experts · " +
          (status.ExpertCacheMib > 0 ? BasaltLocator.HumanMib(status.ExpertCacheMib) : "—")
        : "not resident";

    /// <summary>What is left of the card's budget, in the words Windows uses for it.</summary>
    public string VramFreeText => machine.Video.Known
        ? BasaltLocator.HumanBytes(machine.Video.Free) + " VRAM free"
        : status.VramFreeMib > 0 ? BasaltLocator.HumanMib(status.VramFreeMib) + " VRAM free" : "—";
    public string SpecText => status.Spec > 0 ? status.Spec + " draft tokens" : "no drafting";
    public string PcieText => status.PcieFrac > 0 ? "pcie " + status.PcieFrac.ToString("0.00") : "pcie idle";
    public string DiskText => status.DiskReadMb > 0.05 ? status.DiskReadMb.ToString("0.0") + " MB/s from disk" : "no reads";
    public string QueueText => status.Queued + " queued · requests " + status.SlotsActive + "/" +
                               (status.SlotsTotal > 0 ? status.SlotsTotal.ToString() : "?");
    public string LiveText => string.IsNullOrEmpty(status.LiveState) ? "—" : status.LiveState;
    public string EngineLine
    {
        get
        {
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(status.EngineVersion))
                bits.Add("engine " + status.EngineVersion);
            if (!string.IsNullOrWhiteSpace(status.Quant))
                bits.Add(status.Quant);
            if (!string.IsNullOrWhiteSpace(status.Kv))
                bits.Add("kv " + status.Kv);
            if (status.MaxContext > 0)
                bits.Add("ctx " + status.MaxContext.ToString("N0"));
            if (status.Images)
                bits.Add("vision");
            return bits.Count == 0 ? "—" : string.Join(" · ", bits);
        }
    }
    public string HardwareLine
    {
        get
        {
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(status.CpuName))
                bits.Add(status.CpuName.Trim());
            if (status.Cores > 0)
                bits.Add(status.Cores + " cores / " + status.Threads + " threads");
            if (!string.IsNullOrWhiteSpace(status.Driver))
                bits.Add("driver " + status.Driver);
            return bits.Count == 0 ? "—" : string.Join(" · ", bits);
        }
    }
    public string UptimeText
    {
        get
        {
            if (process.StartedAt is not { } at)
                return "";
            var up = DateTime.Now - at;
            // the word lives here, not in the window: a footer that reads "up " on its own is worse than one
            // that says nothing
            return up.TotalHours >= 1
                ? $"up {(int)up.TotalHours}:{up.Minutes:00}:{up.Seconds:00}"
                : $"up {up.Minutes}:{up.Seconds:00}";
        }
    }

    /// <summary>Everything recomputed from one /metrics reading.</summary>
    private static readonly string[] DisplayProps =
    [
        nameof(DecodeValue), nameof(PrefillValue), nameof(DecodeStatsText), nameof(PrefillStatsText),
        nameof(GpuNameText), nameof(GpuUtilFraction), nameof(GpuUtilValue),
        nameof(GpuMemFraction), nameof(GpuMemText), nameof(GpuTempFraction), nameof(GpuTempHot), nameof(GpuTempValue),
        nameof(GpuPowerFraction), nameof(GpuPowerText), nameof(CpuFraction), nameof(CpuText),
        nameof(RamFraction), nameof(RamText), nameof(ExpertFraction), nameof(ExpertText), nameof(VramFreeText),
        nameof(SpecText), nameof(PcieText), nameof(DiskText), nameof(QueueText), nameof(LiveText),
        nameof(EngineLine), nameof(HardwareLine), nameof(UptimeText),
    ];

    private void RaiseDisplay() => RaiseAll(DisplayProps);

    public ObservableCollection<RequestRow> Recent { get; } = [];
    public ObservableCollection<ModelRow> ModelRows { get; } = [];

    /// <summary>What the main page shows: the named configs, each with the models its document defines.</summary>
    public ObservableCollection<ConfigCard> ConfigCards { get; } = [];

    public sealed record ConfigCard(string Name, bool Active, bool Running, int Port, string Size,
                                    IReadOnlyList<ConfigModelRow> Models)
    {
        public string Title => Name;
        public string ActionGlyph => Running ? "\uE71A" : "\uE768";
        public string ActionTip => Running ? "Stop this config" : "Load this config";
    }

    public sealed record ConfigModelRow(string Id, string ModelFile, string Size, string Context, bool Loaded)
    {
        public ModelIdentity.Identity? Badge => ModelIdentity.For(Id);
        public bool HasBadge => Badge is not null;
        public string Facts => $"{Size}  ·  {Context} ctx";
    }

    /// <summary>The configs currently serving models, and the one this window watches.</summary>
    private readonly HashSet<string> runningProfiles = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigRunning(string name) => runningProfiles.Contains(name);

    private void SetRunning(string? name, bool on)
    {
        if (name is null)
            return;
        var changed = on ? runningProfiles.Add(name) : runningProfiles.Remove(name);
        if (!changed)
            return;
        Raise(nameof(StatusText));
        RebuildConfigCards();
    }

    /// <summary>Rebuilds the config cards from the store and the current loaded-model state.</summary>
    private static readonly Dictionary<string, long> expertBytes = new(StringComparer.OrdinalIgnoreCase);

    public void RebuildConfigCards()
    {
        ConfigCards.Clear();
        var loaded = ThroughputRecorder.LoadedModelOf(status);
        foreach (var name in Profiles.Names)
        {
            var document = Profiles.Document(name);
            if (document is null)
                continue;
            var server = document["server"] as JsonObject;
            var port = server is not null && int.TryParse(ConfigDocument.Text(server, "port"), out var p)
                ? p : 8080;
            var rows = new List<ConfigModelRow>();
            var bytes = 0L;
            var experts = 0L;
            foreach (var entry in (document["models"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var model = ConfigDocument.Text(entry, "model") ?? "";
                var file = ModelFilePath(model);
                var length = file is not null ? new FileInfo(file).Length : 0;
                bytes += length;
                if (file is not null && file.EndsWith(".basalt", StringComparison.OrdinalIgnoreCase))
                {
                    lock (expertBytes)
                    {
                        if (!expertBytes.TryGetValue(file, out var known))
                        {
                            known = LoadPreview.ExpertBytes(file);
                            expertBytes[file] = known;
                        }
                        experts += known;
                    }
                }
                var size = length > 0 ? BasaltLocator.HumanBytes(length) : "size unknown";
                var context = ConfigDocument.Int(entry, "max-context");
                var isLoaded = Path.GetFileName(model).Equals(Path.GetFileName(loaded), StringComparison.OrdinalIgnoreCase);
                rows.Add(new ConfigModelRow(Path.GetFileNameWithoutExtension(model), model, size,
                                            context is { } n ? n.ToString() : "default", isLoaded));
            }
            var sizeText = bytes > 0
                ? BasaltLocator.HumanBytes(bytes) + (experts > 0 ? $"  (experts {BasaltLocator.HumanBytes(experts)})" : "")
                : "";
            ConfigCards.Add(new ConfigCard(name, Profiles.Active == name, IsConfigRunning(name),
                                           port, sizeText, rows));
        }
        OnUi(RaiseDisplay);
    }

    /// <summary>Where a model entry's file really is: the entry's own path first, then the models folder,
    /// then the folder's scanned list. A file that exists on disk always has a size, whatever the folder says.</summary>
    private string? ModelFilePath(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;
        if (File.Exists(model))
            return model;
        var folder = Settings.ModelsFolder;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            var candidate = Path.Combine(folder, Path.GetFileName(model));
            if (File.Exists(candidate))
                return candidate;
        }
        return localModels.FirstOrDefault(f =>
                   Path.GetFileName(f.File).Equals(Path.GetFileName(model), StringComparison.OrdinalIgnoreCase))?.File;
    }

    /// <summary>What the Models card lists: served models first, then the models folder's files.</summary>
    public ObservableCollection<HomeModelRow> DisplayModels { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    public string VersionText => string.IsNullOrWhiteSpace(status.EngineVersion) ? "" : "basalt " + status.EngineVersion;

    // ---- is this basalt behind? ------------------------------------------------------------------------------
    private BasaltRelease? latest;
    private bool checkingUpdate;
    private string updateSaying = "";

    /// <summary>The newest release the app has seen. Null until something answers.</summary>
    public BasaltRelease? Latest
    {
        get => latest;
        private set
        {
            if (!Set(ref latest, value))
                return;
            RaiseUpdate();
        }
    }

    public bool CheckingUpdate
    {
        get => checkingUpdate;
        private set
        {
            if (Set(ref checkingUpdate, value))
                RaiseUpdate();
        }
    }

    /// <summary>
    /// What the machine is running, as far as anything can tell: the engine on the port, failing that the exe
    /// file — which for basalt means the number it announced the last time that copy ran, since the file
    /// itself carries no version. The file-side answer is cached, because this is read on every poll and
    /// opening the file every second is not how that question should be answered.
    /// </summary>
    public string InstalledVersionText
    {
        get
        {
            if (status.Reachable &&
                (UpdateChecker.ParseVersion(status.EngineVersion) is not null ||
                 UpdateChecker.ParseVersion(status.BuildInfo) is not null))
                return UpdateChecker.InstalledVersionText(status, Settings.BasaltExePath);
            return InstalledFromExe();
        }
    }

    private string installedFromExe = "";
    private string installedExeKey = "";

    /// <summary>Which copy of the exe a version number would be about.</summary>
    private string ExeKey() => UpdateChecker.ExeVersionKey(Settings.BasaltExePath);

    private string InstalledFromExe()
    {
        var key = ExeKey();
        if (key == installedExeKey)
            return installedFromExe;

        installedExeKey = key;
        installedFromExe = UpdateChecker.InstalledVersionText(null, Settings.BasaltExePath,
                                                              Settings.EngineVersionMemo, key);
        return installedFromExe;
    }

    /// <summary>
    /// What the engine says about itself while it runs is the only version this exe is willing to give, so it is
    /// kept for the hours it is not running — which is when the app has to decide whether to mention an update.
    /// </summary>
    private void RememberVersion(ServerStatus snapshot)
    {
        if (!snapshot.Reachable)
            return;
        var text = UpdateChecker.ParseVersion(UpdateChecker.InstalledVersionText(snapshot, Settings.BasaltExePath))?.ToString() ?? "";
        var key = ExeKey();
        if (text.Length == 0 || (text == Settings.EngineVersionMemo && key == Settings.EngineVersionMemoKey))
            return;

        Settings.EngineVersionMemo = text;
        Settings.EngineVersionMemoKey = key;
        Settings.Save();
        if (key == installedExeKey)
            installedFromExe = text;
        Append("basalt " + text + " is the engine this machine runs", LogSource.App);
    }

    public bool UpdateAvailable
        => Latest is { } release && Settings.DismissedUpdateTag != release.Tag &&
           UpdateChecker.Compare(InstalledVersionText, release) > 0;

    /// <summary>
    /// The bottom-left corner: which basalt this machine runs, green dot when that is the newest release,
    /// yellow when something newer is out — and the whole thing is the update button.
    /// </summary>
    /// <summary>True when there is no basalt.exe where the app expects it: the footer then offers the download.</summary>
    public bool BasaltMissing => string.IsNullOrWhiteSpace(Settings.BasaltExePath) || !File.Exists(Settings.BasaltExePath);

    public string EngineVersionLabel => BasaltMissing
        ? "Basalt not installed"
        : InstalledVersionText.Length == 0 ? "basalt — version unknown" : "basalt " + InstalledVersionText;

    public string UpdateDotTip => BasaltMissing
        ? "click to download and install basalt"
        : InstalledVersionText.Length == 0 ? "click to check for a newer basalt"
        : "basalt " + InstalledVersionText + " — click to check for updates";

    /// <summary>The banner. Nothing when there is nothing to say.</summary>
    public string UpdateText
    {
        get
        {
            if (checkingUpdate)
                return "looking for a newer basalt…";
            if (Latest is null)
                return updateSaying;                       // the reason the last check failed, if there was one

            var release = Latest;
            var here = InstalledVersionText;
            var behind = UpdateChecker.Compare(here, release);
            if (Settings.DismissedUpdateTag == release.Tag && behind > 0)
                return $"{release.Tag} is out — you told Basalt UI to leave it alone (Settings → Basalt)";
            if (here.Length == 0)
                return $"the newest basalt is {release.Tag}, published {PublishText(release)} — Basalt UI cannot tell what this machine runs";
            if (behind > 0)
                return $"basalt {release.Tag} is out and this machine has {here} — published {PublishText(release)}";
            return $"basalt {here} is the newest release";
        }
    }

    private static string PublishText(BasaltRelease release)
    {
        var utc = release.PublishedAt.Length > 10 ? release.PublishedAt[..10] : release.PublishedAt;
        if (!DateTime.TryParse(utc, out var day))
            return "recently";
        var age = DateTime.UtcNow.Date - day.Date;
        return age switch
        {
            { TotalDays: <= 0 } => "today",
            { TotalDays: > 0 and < 2 } => "yesterday",
            _ => $"{day.ToLocalTime():yyyy-MM-dd}",
        };
    }

    /// <summary>Which of the release's files belongs here, and how big it is.</summary>
    public string UpdateAssetText => Latest?.WindowsAsset is { } asset
        ? asset.Name + " · " + asset.SizeText + (asset.Sha256.Length == 64 ? " · sha256 published" : "")
        : Latest is { Assets.Count: 0 }
            ? "the files are still going up for this release — the next look brings them"
            : "this release has no windows zip";

    /// <summary>Why the last check did not answer at all, if it did not. Empty when we know a release.</summary>
    public string UpdateProblem => Latest is null ? updateSaying : "";

    /// <summary>“Not now” for this tag: the banner goes away and the quiet checks leave it alone.</summary>
    public void UpdateDismiss()
    {
        if (Latest is null)
            return;
        Settings.DismissedUpdateTag = Latest.Tag;
        Settings.Save();
        RaiseUpdate();
        Append("app: told not to mention " + Latest.Tag + " again; Settings → Basalt brings it back", LogSource.App);
    }

    /// <summary>The other half of Dismiss.</summary>
    public void UpdateUndismiss()
    {
        if (Settings.DismissedUpdateTag.Length == 0)
            return;
        Settings.DismissedUpdateTag = "";
        Settings.Save();
        RaiseUpdate();
    }

    public void OpenBasaltSettingsFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(Settings.BasaltExePath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                Append("settings: basalt is not where the settings say it is: " + Settings.BasaltExePath, LogSource.Error);
                return;
            }
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Append("settings: " + e.Message, LogSource.Error);
        }
    }

    /// <summary>Why the banner is on the screen at all: news to act on, or a check in front of the user.</summary>
    public bool UpdateSaysAnything => UpdateAvailable || checkingUpdate;

    private void RaiseUpdate()
        => RaiseAll(nameof(UpdateAvailable), nameof(UpdateSaysAnything), nameof(UpdateText),
                    nameof(UpdateAssetText), nameof(InstalledVersionText),
                    nameof(EngineVersionLabel), nameof(UpdateDotTip));

    /// <summary>
    /// Ask GitHub. Quiet calls only say something when they found a release this machine does not have; an
    /// explicit check always answers, because a button that does nothing looks broken.
    /// </summary>
    public async Task CheckForUpdateAsync(bool manual)
    {
        if (checkingUpdate)
            return;
        CheckingUpdate = true;

        // The clock is stamped before the call, not after: an attempt that failed is still an attempt, and
        // the auto-check retries on this same value instead of hammering GitHub every couple of seconds.
        Settings.LastUpdateCheckUtc = DateTime.UtcNow;
        Settings.Save();

        try
        {
            var release = await UpdateChecker.LatestAsync(CancellationToken.None);
            Latest = release;
            var behind = UpdateChecker.Compare(InstalledVersionText, release);
            if (behind > 0)
            {
                // Once per release, not once per check: the quiet check, the six-hour one and a hand-pressed
                // button used to write the same line over and over, which turned the log into a shout.
                if (announcedTag != release.Tag)
                {
                    announcedTag = release.Tag;
                    Append($"update: basalt {release.Tag} is out — this machine has {(InstalledVersionText.Length == 0 ? "nothing I can read" : InstalledVersionText)}", LogSource.App);
                }
                if (manual)
                    Notice?.Invoke($"{release.Tag} is out: {(release.Name.Length > 0 ? release.Name : release.Tag)}");
            }
            else if (behind == 0 && manual)
                Notice?.Invoke("basalt " + release.Tag + " is the newest release");
        }
        catch (Exception e)
        {
            updateSaying = "the update check did not go: " + Net.Explain(e);
            Append("update: " + updateSaying, LogSource.Error);
            if (manual)
                Notice?.Invoke(Net.Explain(e));
        }
        finally
        {
            CheckingUpdate = false;
            RaiseUpdate();
        }
    }

    /// <summary>
    /// One quiet check per app run. The settings switch turns it off entirely; a manual check always works.
    /// </summary>
    private bool autoCheckDone;

    /// <summary>The release already named in the log, so it is announced once and not once per check.</summary>
    private string announcedTag = "";

    /// <summary>The notes of every release newer than the one installed here, oldest first — what the
    /// update popup's changelog button shows when the user asks what "X to Y" actually means.</summary>
    public async Task<string> ChangelogAsync()
    {
        var releases = await UpdateChecker.ReleasesAsync(CancellationToken.None);
        var between = releases
            .Where(r => r.Version is not null && UpdateChecker.Compare(InstalledVersionText, r) > 0)
            .OrderBy(r => r.Version)
            .ToList();
        var text = $"# Changes from {InstalledVersionText} to {Latest?.Tag ?? "newest"}\n\n";
        foreach (var r in between)
            text += $"## {r.Tag} — {(r.PublishedAt.Length >= 10 ? r.PublishedAt[..10] : r.PublishedAt)}\n\n" +
                    (string.IsNullOrWhiteSpace(r.Notes) ? "(no notes were written for this release)" : r.Notes) + "\n\n";
        if (between.Count == 0)
            text += "(no release notes were published between these versions)\n";
        return text;
    }

    /// <summary>Nothing installed yet: download the newest release straight into the app's own basalt folder
    /// and remember that exe as the one every config is launched with.</summary>
    public async Task<string?> InstallBasaltAsync()
    {
        var root = Path.Combine(AppPaths.Root, "basalt");
        Directory.CreateDirectory(root);
        Settings.BasaltExePath = Path.Combine(root, "bin", "basalt.exe");
        Settings.Save();
        if (Latest is null)
        {
            try
            {
                Latest = await UpdateChecker.LatestAsync(CancellationToken.None);
            }
            catch (Exception e)
            {
                return Net.Explain(e);
            }
        }
        return await UpdateBasaltAsync();
    }

    /// <summary>
    /// The quiet check. Once soon after the app starts, and then every few hours it stays open, so a release
    /// published overnight is sitting there when the window is. A manual check always works, even with the
    /// setting off, and always resets the same clock.
    /// </summary>
    public void MaybeCheckForUpdate()
    {
        if (checkingUpdate || !Settings.CheckForUpdates)
            return;
        // A release that was up but had no files yet was caught mid-publish: look again in minutes, not hours
        var waiting = Latest is { Assets.Count: 0 };
        var gap = waiting ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(6);
        if (autoCheckDone && DateTime.UtcNow - Settings.LastUpdateCheckUtc < gap)
            return;
        autoCheckDone = true;
        _ = CheckForUpdateAsync(manual: false);
    }

    /// <summary>When the app last asked — the settings page quotes it, so a silent check is not a mystery.</summary>
    public string UpdateCheckedAtText
        => Settings.LastUpdateCheckUtc == default ? "never" : Settings.LastUpdateCheckUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>The release the user told the app to stop mentioning, if any.</summary>
    public string DismissedTag => Settings.DismissedUpdateTag;

    /// <summary>
    /// An update replaces basalt.exe, and Windows will not replace a file that is running. So the stop is part
    /// of the update rather than a errand sent the user on: stop the server, put the new files down, start it
    /// again — each one said out loud in the log pane, so someone back at the window later can read what
    /// happened in what order. A server this app did not start is adopted only to be stopped politely, and
    /// only after the user answered the question <see cref="PeekUpdatePlan" /> put in front of them; once it
    /// comes back it is Basalt UI's own, which is the honest state of affairs afterwards.
    /// </summary>
    private bool busyUpdating;

    /// <summary>What stands between the new release and the disk, right now. The window asks about this.</summary>
    public UpdatePlan PeekUpdatePlan()
    {
        var port = getConfig()?.Port ?? 8080;
        var answers = process.IsAlive || status.Reachable;

        int[] listeners = [];
        if (process.IsAlive)
            listeners = [process.Pid];
        else if (answers)
        {
            // Answering but not ours: the port table is the only way to learn which process to ask.
            try
            {
                listeners = NativeMethods.ListenersOnPort((ushort)port);
            }
            catch (Exception e)
            {
                AppPaths.Log("update: port scan: " + e.Message);
            }
        }

        return UpdatePlan.Decide(answers, process.IsAlive && process.StartedByUs, listeners, port);
    }

    public async Task<string> UpdateBasaltAsync()
    {
        if (busyUpdating)
            return "that update is still running — the console is where it is talking";
        busyUpdating = true;
        try
        {
            return await PutBasaltAsync();
        }
        finally
        {
            busyUpdating = false;
        }
    }

    private async Task<string> PutBasaltAsync()
    {
        var release = Latest;
        if (release?.WindowsAsset is null)
            return release is { Assets.Count: 0 }
                ? $"the files for {release.Tag} are still going up — click the dot again in a few minutes"
                : "this release has no windows zip — nothing of it belongs on this machine";

        var plan = PeekUpdatePlan();
        var wasRunning = false;
        if (plan.Answers)
        {
            if (plan.Blocked)
                return $"something is answering on port {plan.Port} and no process behind it can be named from here — nothing was replaced";

            if (plan.AskFirst)
            {
                process.ServePort = plan.Port;       // a stop reaches the engine through the port, not through us
                // Ctrl+C has to be sent somewhere, and a process this app only knows as a number on the port
                // table needs to be in its own books before the stop machinery will act on it. The user said
                // yes to exactly this in the question.
                process.Adopt(plan.Pid, $"running — pid {plan.Pid}, stopped for the update");
                Append($"update: taking over {plan.Describe()} so it can be stopped politely", LogSource.App);
            }

            Append("update: stopping the server", LogSource.App);
            if (!await process.StopAsync(Settings.StopGraceSeconds))
            {
                process.ClearAdoption();     // the books back as they were found: nothing was stopped
                return "the server would not stop — nothing was replaced";
            }

            // The port, not the process, is what has to be free: a stopped parent with a child still holding
            // the socket would fail the file replacement a second later with a worse message.
            await poller.BeatAsync();
            if (status.Reachable)
                return $"something is still answering on port {plan.Port} — nothing was replaced";

            wasRunning = true;
            SetForeign([]);                          // whatever was out there is being replaced by ours now
        }

        var zip = UpdateInstaller.LocalZipFor(release);
        if (!File.Exists(zip))
        {
            var progress = new Progress<DownloadProgress>(p => OnUi(() =>
            {
                Append($"update: {p.Text}", LogSource.App);
            }));
            var error = await UpdateInstaller.DownloadAsync(release, Settings, progress, line => Append("update: " + line, LogSource.App), CancellationToken.None);
            if (error is not null)
                return error;
            if (!File.Exists(zip))
                return "the download finished without leaving the file behind";
        }

        var installRoot = Path.GetDirectoryName(Settings.BasaltExePath) ?? "";
        var trace = new Action<string>(line => Append("update: " + line, LogSource.App));
        var applyError = await UpdateInstaller.ApplyAsync(release, installRoot, trace, CancellationToken.None);
        if (applyError is not null)
            return applyError;

        // The exe now on disk is a different file, and it is this release: remember that about it, so the app is
        // not left guessing which number belongs there until the engine starts and says it again.
        installedExeKey = "";
        lastVersionSaw = "";
        Settings.EngineVersionMemo = UpdateChecker.ParseVersion(release.Tag)?.ToString() ?? "";
        Settings.EngineVersionMemoKey = ExeKey();
        Settings.LastUpdateCheckUtc = DateTime.UtcNow;
        Settings.DismissedUpdateTag = "";
        Settings.Save();
        Latest = null;
        announcedTag = "";                    // a later release has to be able to announce itself too
        RaiseUpdate();

        // The server that was running was stopped to free the files; whether it comes back up is the
        // user's call, made in the window the moment the install lands — the old auto-start took the
        // choice away and then had to announce what it had done.
        if (wasRunning)
            Append("update: " + release.Tag + " is installed — the server is still down, starting it is a question in the window", LogSource.App);
        else
            Notice?.Invoke(release.Tag + " is installed — start the server to run it");
        return "";
    }

    /// <summary>Open the release page, notes and all, in the browser the user already trusts.</summary>
    public void OpenReleasePage()
    {
        var url = Latest?.HtmlUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = UpdateChecker.ReleasePage;
        if (!TryOpen(url))
            Notice?.Invoke("could not open the browser — the release notes are at " + url);
        else
            Notice?.Invoke("the release notes are opening in your browser");
    }

    public void OpenModelsFolder()
    {
        try
        {
            Directory.CreateDirectory(Settings.ModelsFolder);
            Process.Start(new ProcessStartInfo(Settings.ModelsFolder) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Append("models: " + e.Message, LogSource.Error);
        }
    }

    public string ModelsFolderText => Settings.ModelsFolder;

    public event Action? ModelsBrowserRequested;

    public void OpenModelsBrowser() => ModelsBrowserRequested?.Invoke();

    // ---- the log pane ---------------------------------------------------------------------------------------
    public ObservableCollection<LogLine> Log { get; } = [];
    private const int LogCapacity = 2000;

    public string LogFilter
    {
        get => logFilter;
        set
        {
            if (!Set(ref logFilter, value))
                return;
            ApplyFilter();
        }
    }

    private readonly List<LogLine> all = [];
    private readonly object logGate = new();

    public void Append(string text, LogSource source = LogSource.Server)
    {
        if (string.IsNullOrEmpty(text))
            return;
        var line = new LogLine(DateTime.Now, text, source);
        lock (logGate)
        {
            all.Add(line);
            if (all.Count > LogCapacity)
                all.RemoveRange(0, all.Count - LogCapacity);
        }
        if (MatchesFilter(line))
            OnUi(() =>
            {
                Log.Add(line);
                while (Log.Count > LogCapacity)
                    Log.RemoveAt(0);
            });
    }

    private bool MatchesFilter(LogLine line)
        => string.IsNullOrWhiteSpace(logFilter) ||
           line.Text.Contains(logFilter, StringComparison.OrdinalIgnoreCase);

    private void ApplyFilter()
    {
        List<LogLine> snapshot;
        lock (logGate) snapshot = all.Where(MatchesFilter).ToList();
        OnUi(() =>
        {
            Log.Clear();
            foreach (var line in snapshot)
                Log.Add(line);
        });
    }

    public string SaveLogTo(string path)
    {
        try
        {
            List<LogLine> snapshot;
            lock (logGate) snapshot = [.. all];
            File.WriteAllLines(path, snapshot.Select(l => $"{l.At:yyyy-MM-dd HH:mm:ss} {l.Text}"));
            return "";
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    public void OpenWebUiFor(HomeModelRow row) => OpenWebUiFor(row.Id);

    /// <summary>Open the web UI of the server this model actually runs with — the config assigned to its
    /// name, or the active one when it has no assignment. With several basalts there is no single web UI.</summary>
    public void OpenWebUiFor(string modelName)
    {
        var name = Profiles.AssignedTo(modelName);
        var document = name is not null ? Profiles.Document(name) : getConfig()?.Root;
        var host = "127.0.0.1";
        var port = 8080;
        if (document?["server"] is JsonObject server)
        {
            var listening = ConfigDocument.Text(server, "host") ?? host;
            host = ConfigDocument.ThisPcOnly(listening) ? "127.0.0.1" : listening;
            if (int.TryParse(ConfigDocument.Text(server, "port"), out var p) && p is > 0 and < 65536)
                port = p;
        }
        WebUiRequested?.Invoke($"http://{host}:{port}/");
    }

    // ---- actions ---------------------------------------------------------------------------------------
    /// <summary>The api key belongs to the active config: it is passed on that config's command line and
    /// sent as the bearer key when polling its server. Older settings with no active config still work.</summary>
    public string EffectiveApiKey => Profiles.Active is { } name
        ? Profiles.ApiKey(name)
        : Settings.ApiKey;

    /// <summary>The file the active config is written into, and the one the app polls.</summary>
    public string ActiveConfigFile => Profiles.Active is { } name
        ? Profiles.FilePath(name) ?? Settings.ConfigPath
        : Settings.ConfigPath;

    /// <summary>The address and key to poll, straight from the config file the app points at.</summary>
    public void ReapplyPolling()
    {
        var cfg = getConfig();
        var port = cfg?.Port ?? 8080;
        var host = cfg?.Host ?? "127.0.0.1";
        var key = EffectiveApiKey;
        poller.Configure(port, host, string.IsNullOrWhiteSpace(key) ? null : key,
                         readServerLog: !process.StartedByUs);
        process.ServePort = port;                                  // how a stop finds the server's own processes
        Raise(nameof(Url));
    }

    public async Task<bool> StartAsync()
    {
        if (!PrepareStart(out var spec))
            return false;
        Append($"app: starting {Path.GetFileName(spec.ExePath)} {spec.BuildArguments()}", LogSource.App);
        var ok = process.Start(spec);
        OnUi(() => RefreshCommands());
        if (ok)
        {
            ReapplyPolling();
            poller.Start(Settings.PollMs);
            await poller.BeatAsync();
        }
        return ok;
    }

    /// <summary>The configs started alongside the one this window watches: one process each, on its own
    /// file and port, so several basalts can run at once without fighting over a config or a socket.</summary>
    private readonly Dictionary<string, BasaltProcess> otherServers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>At launch, apply and start every config that asked for it. The active one is started
    /// through this window's own server; the others get their own processes and write to the log.</summary>
    public async Task StartFlaggedConfigsAsync()
    {
        if (!Settings.StartConfigsOnLaunch)
            return;
        var watched = Profiles.Active;
        var flagged = Profiles.Names.Where(Profiles.StartsOnLaunch).ToList();
        foreach (var name in flagged)
        {
            var error = ApplyProfile(name, null);
            if (error is not null)
            {
                Append($"app: config \"{name}\" did not start — {error}", LogSource.Error);
                continue;
            }
            if (name == watched)
            {
                if (!process.IsAlive)
                    await StartAsync();
                if (process.IsAlive)
                    SetRunning(name, true);
                continue;
            }

            var target = Profiles.FilePath(name)!;
            var server = Profiles.Document(name)?["server"] as JsonObject;
            var port = server is not null && int.TryParse(ConfigDocument.Text(server, "port"), out var p) ? p : 8080;
            var spec = new LaunchSpec
            {
                ExePath = Settings.BasaltExePath,
                ConfigPath = target,
                ApiKey = Profiles.ApiKey(name),
                ExtraArgs = Profiles.ExtraArgs(name),
                Port = port,
            };
            var extra = new BasaltProcess();
            extra.ConsoleLine += line => Append($"[{name}] {line}", LogSource.Server);
            extra.Notice += note => Append($"[{name}] {note}", LogSource.App);
            if (extra.Start(spec))
            {
                otherServers[name] = extra;
                SetRunning(name, true);
                Append($"app: started config \"{name}\" on port {port} — {Path.GetFileName(target)}", LogSource.App);
            }
            else
                Append($"app: config \"{name}\" could not start", LogSource.Error);
        }

        // Applying each config pointed the app at the last one; the window watches the one that was
        // active when it opened, so put its file back at the end. Its process is already running.
        if (watched is not null && flagged.Contains(watched))
        {
            ApplyProfile(watched, null);
            ReapplyPolling();
        }
    }

    /// <summary>Everything that makes this installation this installation: the app's own settings, and
    /// every named config — its document, its file, its launch words and its model assignments.</summary>
    public string ExportConfiguration()
    {
        var root = new JsonObject
        {
            ["exportedAt"] = DateTime.UtcNow.ToString("O"),
            ["basaltUiSettings"] = JsonNode.Parse(JsonSerializer.Serialize(Settings)),
            ["configs"] = Profiles.ToJson(),
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Every basalt this app started, including the one the window watches, gets a clean stop.</summary>
    public async Task StopAllConfigsAsync()
    {
        foreach (var (name, extra) in otherServers.ToList())
        {
            if (extra.IsAlive)
            {
                Append($"app: stopping config \"{name}\"", LogSource.App);
                await extra.StopAsync(Settings.StopGraceSeconds);
            }
            otherServers.Remove(name);
        }
        if (process.IsAlive)
            await process.StopAsync(Settings.StopGraceSeconds);
    }

    /// <summary>Checks the paths and api-key rules, and hands back the launch spec. Shows what is wrong.</summary>
    private bool PrepareStart(out LaunchSpec spec)
    {
        spec = new LaunchSpec { ExePath = "", ConfigPath = "" };
        var cfg = getConfig();

        if (string.IsNullOrWhiteSpace(Settings.BasaltExePath) || !File.Exists(Settings.BasaltExePath))
        {
            Notice?.Invoke("Set the path to basalt.exe in Settings → Paths.");
            return false;
        }
        var configFile = ActiveConfigFile;
        if (string.IsNullOrWhiteSpace(configFile) || !File.Exists(configFile))
        {
            Notice?.Invoke("Set a config file in the Configs tab; basalt needs one to serve several models.");
            return false;
        }
        var apiKey = EffectiveApiKey;
        if (cfg is not null && cfg.KeyWouldBeRefused)
        {
            Notice?.Invoke("Remove api-key from the config file: basalt refuses it there. Put it in the config's launch options.");
            return false;
        }
        if (cfg is not null)
        {
            var bad = cfg.Validate(SettingsCatalog.Shared).Where(i => i.Error).ToList();
            if (bad.Count > 0)
            {
                Notice?.Invoke("The config has errors: " + string.Join(" | ", bad.Select(b => b.Where + " — " + b.Message)));
                return false;
            }
        }

        // One server to a port. Something already answering that this app does not track would only meet a
        // second basalt fighting for the same address — the new one fails to bind, or both answer and the
        // port's words become a coin toss. The banner up top offers to watch the one that is there.
        if (!process.IsAlive)
        {
            var port = cfg?.Port ?? 8080;
            if (NativeMethods.ListenersOnPort((ushort)port).Length > 0)
            {
                Notice?.Invoke($"port {port} is already serving — Basalt UI did not start that server, and a second " +
                               "basalt on the same port would only fight it. The banner up top offers to watch it.");
                return false;
            }
        }

        spec = new LaunchSpec
        {
            ExePath = Settings.BasaltExePath,
            ConfigPath = configFile,
            ExtraArgs = Profiles.Active is { } active ? Profiles.ExtraArgs(active) : Settings.ExtraArgs,
            ApiKey = apiKey,
            Port = cfg?.Port ?? 8080,
        };
        return true;
    }

    public async Task StopAsync()
    {
        if (!process.IsAlive)
            return;
        Append("app: stopping (Ctrl+C to the server)", LogSource.App);
        await process.StopAsync(Settings.StopGraceSeconds);
        if (!process.IsAlive)
            foreach (var name in runningProfiles.ToList())
                SetRunning(name, false);
        OnUi(RefreshCommands);
    }

    /// <summary>Stop and start again. A stop that did not land means no restart: starting into a port that
    /// is still held only adds a second server to fight the first. True when a start was attempted. A
    /// directed restart — a click that chose it — may end a server that ignores Ctrl+C; a plain one is
    /// refused instead, because a server the app did not start is only ever stopped by the user's say-so.</summary>
    public async Task<bool> RestartAsync(bool userDirected = false)
    {
        if (process.IsAlive)
        {
            Append("app: stopping (Ctrl+C to the server)", LogSource.App);
            await process.StopAsync(Settings.StopGraceSeconds, userDirected: userDirected);
            if (process.IsAlive)
            {
                Append("app: the server did not stop — the restart is off; its own console keeps control",
                       LogSource.Error);
                return false;
            }
        }
        var started = await StartAsync();
        if (started && userDirected && !process.IsAlive)
        {
            // A start that follows a forced end can die in its first seconds: windows is still settling
            // the tree just ended, and the fresh server's own first child fails to spawn — the engine
            // then reports a config check it could not run and exits. The teardown is over in moments,
            // so one quiet retry turns that into a normal start.
            Append("app: the fresh server died as it started — the one just ended is still being settled; starting once more",
                   LogSource.App);
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (!process.IsAlive)
                started = await StartAsync();
        }
        return started;
    }

    /// <summary>Load or unload the model in place, without touching the process.</summary>
    public async Task ControlAsync(string path, string? body, string what)
    {
        var (ok, detail) = await client.PostAsync(path, body);
        Append($"server: {what} → {detail}", ok ? LogSource.App : LogSource.Error);
        trace($"{what} -> {detail}");
        await poller.BeatAsync();
    }

    /// <summary>
    /// The engine's web page, opened inside the app — the click must always visibly do something, and a
    /// browser the user never sees come up is how it stopped doing that. The page runs on the embedded
    /// browser (WebView2). The command itself still asks for a server that answers: with none, there is no
    /// page to open, so the button reads disabled rather than opening an error page.
    /// </summary>
    public event Action<string>? WebUiRequested;

    public void OpenWebUi() => WebUiRequested?.Invoke(client.BaseUrl + "/");

    /// <summary>The same idea for the raw JSON the panel is built from.</summary>
    public void OpenApi() => WebUiRequested?.Invoke(client.BaseUrl + "/health");

    /// <summary>
    /// Opens in whatever browser the machine trusts; false when the shell refused. After the launch the
    /// app watches for a new window coming up and raises it, because a tab that arrives behind this one
    /// reads as the click having done nothing.
    /// </summary>
    private bool TryOpen(string url)
    {
        try
        {
            var before = Shell.Raise.Snapshot();
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Shell.Raise.AfterLaunch(before);
            return true;
        }
        catch (Exception e)
        {
            Append("open: " + e.Message, LogSource.Error);
            return false;
        }
    }

    public void OpenUrl(string url)
    {
        if (!TryOpen(url))
            Notice?.Invoke("could not open " + url);
    }

    /// <summary>A server answering on the port that we did not start: show it, and let the user stop the other one.</summary>
    public void AdoptExternal(int pid)
    {
        // A stop reaches the engine processes through the port, so a server adopted from elsewhere needs the
        // port the config names; without it only the launcher would get the Ctrl+C and the model would stay up.
        process.ServePort = getConfig()?.Port ?? 8080;
        process.Adopt(pid, $"running — pid {pid}, started elsewhere");
        ReapplyPolling();
        poller.Start(Settings.PollMs);
        OnUi(RefreshCommands);
    }

    public void ForgetExternal()
    {
        process.ClearAdoption();
        OnUi(RefreshCommands);
    }

    public Task ForceStopAsync() => process.StopAsync(2, force: true);

    // ---- a server we did not start ----------------------------------------------------------------------
    private int[] foreignPids = [];

    /// <summary>pids answering on the port that this app did not launch; empty once the user decides.</summary>
    public int[] ForeignPids
    {
        get => foreignPids;
        private set
        {
            if (foreignPids.SequenceEqual(value))
                return;
            foreignPids = value;
            Raise(nameof(ForeignPids));
            Raise(nameof(HasForeign));
            Raise(nameof(ForeignHint));
        }
    }

    public bool HasForeign => foreignPids.Length > 0;

    public string ForeignHint
    {
        get
        {
            var pidList = string.Join(", ", foreignPids);
            return pidList.Length == 0
                ? ""
                : $"A server is answering on {client.BaseUrl} that Basalt UI did not start (pid {pidList}). " +
                  "You can watch it and load or unload models; its own terminal keeps control of starting and stopping.";
        }
    }

    public event Action? TakeOverRequested;

    public void SetForeign(int[] pids) => OnUi(() => ForeignPids = pids ?? []);

    /// <summary>Watch the other server here too: display and HTTP control, never a kill.</summary>
    public void TakeOverForeign() => TakeOverRequested?.Invoke();

    public void IgnoreForeign() => ForeignPids = [];

    /// <summary>Run a different model without restarting the server. A "model not found" right after a
    /// restart is the old server's last word — it answers until it is gone while the new one takes seconds
    /// to listen — so that one answer is waited out and asked again, silently; anything else is said once.</summary>
    public async Task SwitchModelAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return;
        var body = "{\"model\":" + System.Text.Json.JsonSerializer.Serialize(name) + "}";
        for (var tries = 0; ; tries++)
        {
            var (ok, detail) = await client.PostAsync("/v1/load", body);
            if (!ok && detail.Contains("model not found", StringComparison.OrdinalIgnoreCase) && tries < 5)
            {
                await Task.Delay(1500);
                continue;                                       // the ghost's answer; the new server is coming up
            }
            Append($"server: switch to {name} → {detail}", ok ? LogSource.App : LogSource.Error);
            trace($"switch {name} -> {detail}");
            await poller.BeatAsync();
            return;
        }
    }

    /// <summary>
    /// A file clicked in the Models card. The engine only serves what its config listed when it started, and
    /// it loads one model at a time, so the click makes the config carry the file and gets the model its
    /// turn: an entry that is already there switches in place — unless another model's load is under way,
    /// which is asked about, because standing in line behind an hour-long read is a decision, not a default —
    /// a new one is asked for first (another entry's settings with the path replaced, or basalt's own
    /// defaults), and then a running server restarts with the file aboard, a stopped one starts with it.
    /// </summary>
    public async Task OpenLocalModel(string file)
    {
        var cfg = getConfig();
        if (cfg is null)
        {
            Append("no config file — pick one in Settings → Paths first", LogSource.Error);
            return;
        }
        var entries = cfg.ModelEntries();
        var entryIndex = entries.FindIndex(e => SameModelFile(ConfigDocument.Text(e, "model"), file));
        var name = Path.GetFileName(file);

        // In the config and the running server serves it (its list is in the config's order, so the entry's
        // face is known while the counts agree; a name match is the fallback)? Then the switch answers in
        // place, no restart.
        var served = entryIndex >= 0 && (status.Models.Count == entries.Count || ServesFile(file));

        // Another model's load under way? The engine grants turns one at a time, so this click either
        // stands in line behind it — minutes, or most of an hour — or a restart brings this model first
        // and the running load is thrown away. That trade is the click's to make, not ours — but once
        // per load: an answer already given for this very load is applied again without asking, or a
        // click that only re-reads the card would ask the same question once a minute.
        var jump = false;
        var swapWords = status.Swap ?? "";
        var swapTarget = SwapTargetOf(swapWords);
        if (status.Reachable && BusyLoadingOther(file))
        {
            if (busyAnswered.TryGetValue(file, out var prior) && prior.Target == swapTarget)
            {
                if (!prior.Restart)
                {
                    Append($"app: {name} is already in line — it loads when the load under way ends", LogSource.App);
                    return;
                }
                jump = true;                                    // answered restart, still not through: again, silently
            }
            else
            {
                var untracked = !process.IsAlive && ForeignPids.Length > 0;
                var options = new List<string>();
                if (served)
                    options.Add("wait — the switch keeps its place in line, it loads when the running load ends");
                options.Add(untracked
                    ? $"take the running server over and restart — {name} loads first, the load under way is thrown away"
                    : $"restart — {name} loads first, the load under way is thrown away");
                if (!served)
                    options.Add("step back — do nothing for now");
                var pick = Dialog.Choose(null, "A load is already under way", BusyWords(file), options);
                if (pick is null)
                    return;                                     // the click stepped back: ask again next time
                if (pick == options[0] && served)
                {
                    busyAnswered[file] = (swapTarget, Restart: false);
                    await SwitchModelAsync(name);
                    return;
                }
                if (pick == options[^1] && !served)
                    return;                                     // the click stepped back: ask again next time
                busyAnswered[file] = (swapTarget, Restart: true);
                jump = true;                                    // the restart option
            }
        }

        if (status.Reachable && served && !jump)
        {
            await SwitchModelAsync(name);
            return;
        }

        if (entryIndex < 0)
        {
            var (ok, template) = AskEntry(file);
            if (!ok)
                return;                                         // the click stepped back
            if (!AddConfigEntry(file, template))
                return;                                         // the save failed: the log says what stopped it
        }

        // The engine preloads the config's first model as it comes up, and this start is about the file
        // that was just clicked — so its entry moves to the front and the preload is the model that was
        // asked for, not whatever happened to sit there. The entries, and their settings, keep their
        // order otherwise; nothing about a model changes but its place in line.
        var now = getConfig();
        var moved = now?.ModelEntries().FirstOrDefault(e => SameModelFile(ConfigDocument.Text(e, "model"), file));
        if (now is not null && moved is not null && now.Models.IndexOf(moved) > 0)
        {
            var arr = now.Models;
            var at = arr.IndexOf(moved);
            arr.RemoveAt(at);
            arr.Insert(0, moved);
            try
            {
                now.Save();
                Append($"{Path.GetFileName(file)} moves to the front of the config — it is the model this start preloads",
                       LogSource.App);
            }
            catch (Exception ex)
            {
                Append("could not reorder the config: " + ex.Message, LogSource.Error);
            }
        }

        if (status.Reachable)
        {
            // A restart stops the server through this app's own books; a server it never started is
            // adopted first — the restart choice above is the user's say-so, the same "watch it" the
            // banner asks for.
            if (!process.IsAlive && ForeignPids.Length > 0)
            {
                var pid = ForeignPids[0];
                Append($"app: the restart stops the server through this app, so the one answering (pid {pid}) is watched first", LogSource.App);
                AdoptExternal(pid);
                SetForeign([]);
            }
            Append(jump
                ? $"app: restarting — {name} loads first, the load under way is not kept"
                : $"app: the engine only serves models its config listed at start — restarting with {file} aboard",
                   LogSource.App);
            await RestartAsync(userDirected: true);
            if (!process.IsAlive)
                return;                                 // the restart did not happen: the log says what stopped it
        }
        else
        {
            Append($"app: starting basalt — {file} is in the config, it loads as the server comes up", LogSource.App);
            if (!await StartAsync())
                return;                                         // the start checks already said what stopped it
        }

        // The engine listens before it loads, so an answer can come while the first model is still coming
        // in; the switch below waits its turn with everything else.
        for (var waited = 0; waited < 30; waited++)
        {
            await poller.BeatAsync();
            if (status.Reachable)
                break;
            await Task.Delay(1000);
        }
        if (!status.Reachable)
        {
            Append("app: the server has not answered yet — it may still be loading; the log keeps the word",
                   LogSource.App);
            return;
        }
        await SwitchModelAsync(Path.GetFileName(file));
    }

    /// <summary>Does anything the server serves answer to this file's name? Its list carries pack names, but
    /// a served model also answers to its own file's name.</summary>
    private bool ServesFile(string file) => status.Models.Any(m => SameModelFile(m.Id, file));

    // ---- the engine's failures, in the app's words -------------------------------------------------------
    // The engine's log passes through as its own words. A RAM failure reads as a contradiction — the
    // engine says the memory ran out, the RAM bar above says it did not — because the two count
    // different things: the engine wants memory it can pin for the experts it keeps, the bar counts
    // what Windows could hand out, file cache included. Once per model, the app says both figures
    // and where the want comes from.
    private readonly Dictionary<string, DateTime> ramSaidAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When the engine's own log last said anything, and whether it has said anything at all:
    /// the silence a stalled load is judged by. The engine prints through every read and placement, so
    /// minutes of nothing while a load is "under way" is the honest signal.</summary>
    private DateTime engineWordsAt = DateTime.MinValue;
    private bool engineWordsSeen;

    public void ExplainServerTrouble(IReadOnlyList<string> lines) => OnUi(() =>
    {
        engineWordsAt = DateTime.UtcNow;                    // the engine said something: it is not silent
        engineWordsSeen = true;
        foreach (var line in lines)
        {
            var at = line.IndexOf(" did not load", StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                continue;
            // the line carries the engine's own tags and error prefixes — "[srv/err] error: …" — and
            // sometimes more than one; the model is whatever is left after the last of them
            var head = line[..at].Trim();
            for (var e = head.IndexOf("error: ", StringComparison.OrdinalIgnoreCase); e >= 0;
                 e = head.IndexOf("error: ", StringComparison.OrdinalIgnoreCase))
                head = head[(e + 7)..].Trim();
            var model = head;
            if (model.Length == 0)
                continue;
            if (ramSaidAt.TryGetValue(model, out var said) && DateTime.UtcNow - said < TimeSpan.FromMinutes(10))
                continue;
            ramSaidAt[model] = DateTime.UtcNow;
            var free = machine.Memory.Usable - machine.Memory.InUse;
            Append($"app: the engine could not load {model} — it could not get the RAM it keeps its experts pinned in" +
                   (machine.Memory.Usable > 0 ? $" (windows counts {BasaltLocator.HumanBytes(free)} available right now; everything running shares it)" : "") +
                   ". The entry's resident-experts switch decides how much RAM a load wants — the settings page can turn it off, " +
                   "and the experts then read from the file as each request needs them", LogSource.App);
        }
    });

    /// <summary>The served row that carries this file, by the same pairing the card uses: the config's
    /// order while the counts agree, then the names.</summary>
    private ModelRow? ServedFaceOf(string file)
    {
        var name = Path.GetFileName(file);
        var served = status.Models;
        var entries = status.Reachable ? getConfig()?.ModelEntries() : null;
        if (entries is not null && served.Count == entries.Count)
        {
            for (var i = 0; i < entries.Count; i++)
                if (SameModelFile(ConfigDocument.Text(entries[i], "model"), file))
                    return served[i];
        }
        return served.FirstOrDefault(m => SameModelFile(m.Id, name) ||
                                          Stem(m.Id).Equals(Stem(name), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The load a click was asked about, and what was answered, per file: the same load is not
    /// asked about twice. The entry clears itself when the load ends and something else takes the port.</summary>
    private readonly Dictionary<string, (string Target, bool Restart)> busyAnswered = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Is a load of some other model in flight — one this file would stand behind? The engine
    /// grants turns one at a time; while another model's weights are still coming in, a switch waits.
    /// A load of this very model (its turn already granted) is not "other".</summary>
    private bool BusyLoadingOther(string file)
    {
        if (status.Swap is not { } words)
            return false;
        var target = SwapTargetOf(words);
        if (target.Length == 0 || target.Equals(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase))
            return false;
        var face = ServedFaceOf(file);
        return face is null || !target.Equals(face.Id, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The question the click answers when a load is under way: what is coming in, how long it
    /// has been at it, and what the engine's own progress meter says when it says anything.</summary>
    private string BusyWords(string file)
    {
        var target = SwapTargetOf(status.Swap ?? "");
        var mins = (int)(DateTime.Now - swapSince).TotalMinutes;
        var pct = status.LoadPercent is > 0 ? $", {status.LoadPercent:0}% read" : "";
        return $"{target} is coming in right now ({(mins > 0 ? mins + " min so far" : "just started")}{pct}). " +
               $"The engine loads one model at a time, so {Path.GetFileName(file)} would stand in line behind it — " +
               "or a restart brings it first. A restart throws the running load away.";
    }

    /// <summary>
    /// A file with no entry in the config yet: the entry decides how the model is served — kv, context,
    /// gpu, the rest — so the click asks instead of choosing for you. The answers are an existing entry's
    /// settings with the model path replaced, or a new entry with basalt's own defaults.
    /// </summary>
    private (bool Ok, JsonObject? Template) AskEntry(string file)
    {
        var entries = getConfig()?.ModelEntries() ?? [];
        var options = new List<string>();
        var templates = new List<JsonObject?>();

        for (var i = 0; i < entries.Count; i++)
        {
            var name = Path.GetFileNameWithoutExtension(ConfigDocument.Text(entries[i], "model")?.Trim() ?? "");
            if (name.Length == 0)
                name = $"entry {i + 1}";
            options.Add($"the settings of {name} — with the model path replaced");
            templates.Add(entries[i]);
        }
        options.Add("a new entry — basalt's own defaults");
        templates.Add(null);

        var pick = Dialog.Choose(null, "Add the model to the config",
            $"{file} is not in the config yet, and the engine only serves what the config lists. " +
            "The new entry carries which settings?",
            options);
        if (pick is null)
            return (false, null);
        var at = options.IndexOf(pick);
        return (true, at >= 0 ? templates[at] : null);
    }

    /// <summary>Add the entry pointing at the file and save: the chosen settings, the path replaced. A copied
    /// alias would serve the new file under the old name, so it does not travel along.</summary>
    private bool AddConfigEntry(string file, JsonObject? template)
    {
        var cfg = getConfig();
        if (cfg is null)
        {
            Append("no config file — pick one in Settings → Paths first", LogSource.Error);
            return false;
        }

        var full = Path.IsPathRooted(file) ? file
                 : Settings.ModelsFolder.Length > 0 ? Path.Combine(Settings.ModelsFolder, file)
                 : file;
        var model = full.Replace('\\', '/');

        var entry = template is { } t ? (JsonObject)t.DeepClone() : new JsonObject();
        entry["model"] = model;
        entry.Remove("alias");
        cfg.Models.Add(entry);
        try
        {
            cfg.Save();
            reopenConfig();                                     // the app re-reads what was just written
            Append($"config: model entry added — {model}", LogSource.App);
            return true;
        }
        catch (Exception ex)
        {
            Append("could not save the config: " + ex.Message, LogSource.Error);
            return false;
        }
    }

    /// <summary>Does a config model entry's path point at this file? The entry may carry a full path or a name,
    /// and Windows does not care about case in either.</summary>
    private static bool SameModelFile(string? entryPath, string file)
    {
        var trimmed = entryPath?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;
        return trimmed.Equals(file, StringComparison.OrdinalIgnoreCase) ||
               Path.GetFileName(trimmed).Equals(file, StringComparison.OrdinalIgnoreCase);
    }

    // ---- plumbing ---------------------------------------------------------------------------------------
    private void OnProcessState(RunState state, string detail) => OnUi(() =>
    {
        if (!status.Reachable)
            Status = status with { };                        // recompute the labels against the new process state
        else if (state is RunState.Stopped or RunState.Failed)
            Status = ServerStatus.Down(detail);
        Raise(nameof(RunStateText));
        Raise(nameof(IsRunning));
        Raise(nameof(ModelsEmptyText));
        if (state is RunState.Stopped or RunState.Failed)
            foreach (var name in runningProfiles.ToList())
                if (name == Profiles.Active)
                    SetRunning(name, false);
        RefreshCommands();
    });

    private string lastVersionSaw = "";

    private void OnStatus(ServerStatus snapshot) => OnUi(() =>
    {
        Status = snapshot;
        machine = poller.Machine;       // read on the same beat, off the UI thread: Windows' own figures
        throughput.Observe(snapshot);   // the recorder keeps its counts with the same beat the screen reads
        RaiseAll(nameof(DecodeSeries), nameof(PrefillSeries), nameof(GpuUtilSeries), nameof(GpuPowerSeries),
                 nameof(GpuTempSeries));
        RaiseDisplay();
        Raise(nameof(StatusText));
        Raise(nameof(VersionText));
        Raise(nameof(EngineVersionLabel));

        // the engine naming itself is the moment the update banner can say something new, and the moment
        // there is something worth remembering about the file on disk
        if (VersionText != lastVersionSaw)
        {
            lastVersionSaw = VersionText;
            RememberVersion(snapshot);
            RaiseUpdate();
        }

        // A beat, reachable or not, is the app's clock. With the server down the installed number comes from what
        // the engine last told us about this very file, so a check is worth as much either way.
        if (snapshot.Reachable || !autoCheckDone)
            MaybeCheckForUpdate();     // the clock inside decides; this only asks it whether it is time

        if (snapshot.Reachable && Recent.Count == 0)
            RebuildRows(snapshot);
        else if (snapshot.Reachable && snapshot.Requests.Count > 0 &&
                 snapshot.Requests[0].Id != Recent.FirstOrDefault()?.Id)
            RebuildRows(snapshot);

        RebuildModels(snapshot);
        RebuildWarnings(snapshot);
        RebuildConfigCards();          // the loaded one stays green among the config cards
        MaybeProbeServed();      // the card's dedup asks the router what names it serves; answers may redraw it
    });

    /// <summary>The file a model row stands for, when the models folder holds one by that name; the
    /// models the server serves without a local file have no path to hand out.</summary>
    public string? LocalFileOf(HomeModelRow row)
    {
        var want = Path.GetFileName(row.Id);
        return localModels.FirstOrDefault(f =>
                   Path.GetFileName(f.File).Equals(want, StringComparison.OrdinalIgnoreCase))?.File;
    }

    // ---- named configs -----------------------------------------------------------------------------------------
    /// <summary>
    /// Named configs: whole basalt config documents — their own models, server, engine, raw JSON — kept
    /// beside the file the engine reads. Attaching one to a model applies it; the store remembers which
    /// model runs with which, and which one the file currently carries.
    /// </summary>
    public ConfigProfiles Profiles { get; } = new();

    /// <summary>The model file the row's entry in the real config stands for — the name the store keys
    /// assignments with. Null when the file has no entry for the row's model.</summary>
    private string? FileOfRow(HomeModelRow row)
    {
        var entry = getConfig()?.ModelEntries().FirstOrDefault(
            e => SameModelFile(ConfigDocument.Text(e, "model"), row.Id));
        return entry is null ? null : Path.GetFileName(ConfigDocument.Text(entry, "model") ?? row.Id);
    }

    /// <summary>
    /// Attach a named config to a model — which means apply it. The config's whole document (its models,
    /// its server, its engine) is written into the real config file now, with this model's entry first
    /// in line, and the config is remembered as the one this model runs with. A config that does not
    /// carry this model gains it, keeping the entry the file has for it. Detaching (null) only ends the
    /// link — the file is untouched. A running server reads its config at start, so it takes the new
    /// document the next time it starts.
    /// </summary>
    public void AttachProfile(HomeModelRow row, string? profile)
    {
        if (profile is null)
        {
            if (FileOfRow(row) is not { } file)
            {
                Append($"no config entry for {row.Id} — double-click the model and one is added", LogSource.Error);
                return;
            }
            Profiles.Assign(file, null);
            Append($"app: config detached from {file} — only the link went; the file still carries what it carries", LogSource.App);
            return;
        }

        var error = ApplyProfile(profile, row.Id);
        if (error is not null)
        {
            Append("could not apply the config: " + error, LogSource.Error);
            return;
        }
        Append($"app: config \"{profile}\" applied — the file now carries its models, server and engine, " +
               "and a running server takes it on at its next start", LogSource.App);
    }

    /// <summary>Writes a named config's document into the real config file, with the model named by
    /// <paramref name="frontFile"/> first in line, and remembers it as the active one. Returns the error,
    /// or null when it worked. The editor's Save uses this too, with no model to front.</summary>
    public string? ApplyProfile(string name, string? frontFile)
    {
        var source = Profiles.Document(name);
        if (source is null)
            return $"no config named \"{name}\"";
        var cfg = getConfig();
        if (cfg is null)
            return "no config file is set — pick one in Settings";

        var root = (JsonObject)source.DeepClone();
        var fronted = FrontModel(root, frontFile, cfg);
        var target = Profiles.FilePath(name) is { Length: > 0 } file && file.Length > 0
            ? file
            : cfg.FilePath;
        var fresh = new ConfigDocument(target, root, File.Exists(target) ? File.ReadAllText(target) : "");
        try
        {
            fresh.Save();
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        Profiles.Active = name;
        if (!string.Equals(Settings.ConfigPath, target, StringComparison.OrdinalIgnoreCase))
        {
            Settings.ConfigPath = target;           // the app now points at the config that was applied
            Settings.Save();
        }
        if (fronted is { } modelFile)
            Profiles.Assign(modelFile, name);
        reopenConfig();                                     // the app re-reads what was just written
        ReapplyPolling();                                   // the new config may listen on another port
        RebuildConfigCards();
        return null;
    }

    /// <summary>Load a whole config: write its document to its file, make it active, then start basalt with
    /// that file — restarting the watched server when one is already running.</summary>
    public async Task LoadConfigAsync(string name)
    {
        var error = ApplyProfile(name, null);
        if (error is not null)
        {
            Notice?.Invoke($"Could not load config “{name}”: {error}");
            Append("could not load config \"" + name + "\": " + error, LogSource.Error);
            return;
        }
        if (process.IsAlive)
            await RestartAsync(userDirected: true);
        else
            await StartAsync();
        if (process.IsAlive)
            SetRunning(name, true);
        Append($"app: config \"{name}\" loaded", LogSource.App);
    }

    /// <summary>Makes the model named by <paramref name="frontFile"/> the first entry of
    /// <paramref name="root"/>, adding it when the config does not carry it — the entry it keeps is the
    /// one the current file has for it. Returns the fronted entry's model file name, or null.</summary>
    private string? FrontModel(JsonObject root, string? frontFile, ConfigDocument current)
    {
        if (frontFile is null)
            return null;
        var want = Path.GetFileName(frontFile);
        var models = root["models"] as JsonArray;
        if (models is null)
        {
            models = [];
            root["models"] = models;
        }

        JsonObject? found = null;
        foreach (var node in models)
            if (node is JsonObject o && SameModelFile(ConfigDocument.Text(o, "model"), want))
            {
                found = o;
                break;
            }

        if (found is null)
        {
            // the config does not carry this model: it joins, with the entry the file has for it
            found = current.ModelEntries()
                           .Where(e => SameModelFile(ConfigDocument.Text(e, "model"), want))
                           .Select(e => (JsonObject)e.DeepClone())
                           .FirstOrDefault()
                           ?? new JsonObject { ["model"] = LocalFileOf(want) ?? want.Replace('\\', '/') };
            models.Insert(0, found);
        }
        else if (models[0] != found)
        {
            models.Remove(found);                           // first in line is what the engine preloads
            models.Insert(0, found);
        }
        return Path.GetFileName(ConfigDocument.Text(found, "model") ?? want);
    }

    /// <summary>The models folder's path for a file name, when it holds one.</summary>
    private string? LocalFileOf(string file)
        => localModels.FirstOrDefault(f =>
               Path.GetFileName(f.File).Equals(file, StringComparison.OrdinalIgnoreCase))?.File;

    /// <summary>A new config that starts as the file the machine runs now, with the model that asked for
    /// it first in line — a place to diverge from, not a blank page. Nothing is written to the file:
    /// attaching the config is what does that.</summary>
    public string NewProfileFor(HomeModelRow row)
    {
        var cfg = getConfig();
        var seed = cfg is null ? ConfigProfiles.BlankDoc() : (JsonObject)cfg.Root.DeepClone();
        var name = Profiles.Create("config", seed);
        if (FileOfRow(row) is { } file)
            Profiles.Assign(file, name);
        RebuildConfigCards();
        return name;
    }

    // ---- the recorder's hand to the row and the window ----------------------------------------------------------
    /// <summary>
    /// The one number a model row can say about its past: the average decode speed of the session under way
    /// when one is, else the last closed session's average, said to be last. Blank while the app has seen
    /// nothing for this model — a row that has no history should not wear a number.
    /// </summary>
    private string StatsOf(string key)
    {
        if (key.Length == 0)
            return "";
        var list = Throughput.Sessions(key);
        var live = list.FirstOrDefault(s => s.Current && s.AvgDecodeTokS > 0);
        if (live is not null)
            return $"avg {live.AvgDecodeTokS:0.#} tok/s";
        var last = list.FirstOrDefault(s => !s.Current && s.AvgDecodeTokS > 0);
        return last is null ? "" : $"last {last.AvgDecodeTokS:0.#} tok/s · {last.Requests} req";
    }

    /// <summary>
    /// What the history window asks for about one model row: every session the app recorded for it, the live
    /// one first, then the closed ones newest first.
    /// </summary>
    public IReadOnlyList<ModelSession> HistoryFor(HomeModelRow row)
    {
        var stem = Path.GetFileNameWithoutExtension(row.Id);
        var candidates = new List<string> { row.Key, row.Id, row.Id + ".basalt", row.Id + ".gguf" };
        foreach (var key in candidates)
            if (key.Length > 0 && Throughput.HasAnything(key))
                return Throughput.Sessions(key);

        var match = Throughput.ModelNames().FirstOrDefault(k =>
        {
            var file = Path.GetFileName(k);
            return file.Equals(row.Id, StringComparison.OrdinalIgnoreCase) ||
                   Path.GetFileNameWithoutExtension(file).Equals(stem, StringComparison.OrdinalIgnoreCase);
        });
        return match is null ? Array.Empty<ModelSession>() : Throughput.Sessions(match);
    }

    /// <summary>
    /// The app is going away: the session under way becomes history, and the file is written before the
    /// answer, because nobody will be around to finish a deferred save after the process is gone.
    /// </summary>
    public void CloseSessions() => OnUi(throughput.CloseSessions);

    private void RebuildRows(ServerStatus snapshot)
    {
        Recent.Clear();
        foreach (var row in snapshot.Requests)
            Recent.Add(row);
    }

    private void RebuildModels(ServerStatus snapshot)
    {
        MaybeRescanLocalModels();

        ModelRows.Clear();
        foreach (var m in snapshot.Models)
            ModelRows.Add(m);
        RebuildDisplay();
    }

    /// <summary>
    /// What the card shows is the models folder: every local file, always, and each file only once. A file
    /// the running server serves wears that server's own words for it — loaded, context, vision — tied to
    /// the file by the config: the server's list is in the config's order, so while the counts agree the
    /// i-th row is the i-th entry's, which is how a pack name ("Qwen3.8-Flash-Next UD-IQ3_XXS") ties to
    /// its file ("Qwen3.8-FN-UD-IQ3_XXS.basalt"), whose names share nothing. When the counts do not agree
    /// (the running server started with an older config), names pair what they can and the router itself
    /// is asked for the rest: /metrics with a model name answers for a name one of its services serves,
    /// without loading anything. Served models no local file accounts for keep their own rows.
    /// </summary>
    private void RebuildDisplay()
    {
        var rows = new List<HomeModelRow>();
        var served = status.Models;

        var entries = status.Reachable ? getConfig()?.ModelEntries() : null;
        var paired = entries is not null && served.Count == entries.Count;

        var faces = new Dictionary<string, ModelRow>(StringComparer.OrdinalIgnoreCase);  // file -> its served face
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);             // served ids a file row carries

        if (paired)
        {
            foreach (var f in localModels)
            {
                for (var i = 0; i < entries!.Count; i++)
                {
                    if (!SameModelFile(ConfigDocument.Text(entries[i], "model"), f.Name))
                        continue;
                    faces[f.Name] = served[i];
                    claimed.Add(served[i].Id);
                    break;
                }
            }
        }

        var openFiles = new List<LocalModelRow>();
        foreach (var f in localModels)
        {
            if (faces.ContainsKey(f.Name))
                continue;
            var byName = served.FirstOrDefault(m => SameModelFile(m.Id, f.Name) ||
                                                    Stem(m.Id).Equals(Stem(f.Name), StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                faces[f.Name] = byName;
                claimed.Add(byName.Id);
                continue;
            }
            if (probedServed.TryGetValue(f.Name, out var serves) && serves)
                openFiles.Add(f);
        }

        // the same order on both sides — the config's order drives the server's list — pairs the rest
        var openServed = served.Where(m => !claimed.Contains(m.Id)).ToList();
        if (openFiles.Count > 0 && openFiles.Count == openServed.Count)
        {
            var order = EntryOrder();
            foreach (var f in openFiles.OrderBy(f => order.GetValueOrDefault(f.Name, int.MaxValue)))
            {
                var face = openServed[0];
                openServed.RemoveAt(0);
                faces[f.Name] = face;
                claimed.Add(face.Id);
            }
        }

        foreach (var f in localModels)
        {
            string detail, key;
            var loaded = false;
            if (faces.TryGetValue(f.Name, out var m))
            {
                loaded = m.Status.Trim().Equals("loaded", StringComparison.OrdinalIgnoreCase);
                detail = m.Detail;                          // loaded · 262,144 ctx · vision — the server's words
                key = m.Id;                                 // the server's name for this row is what the recorder counts
            }
            else
            {
                var ctx = EntryContextOf(f.Name);
                detail = f.Detail + (ctx > 0 ? $" · {ctx:N0} ctx" : "");
                key = f.Name;
            }
            rows.Add(new HomeModelRow(f.Name, detail, Live: false, Loaded: loaded, Stats: StatsOf(key), Key: key));
        }

        foreach (var m in served)
        {
            if (claimed.Contains(m.Id))
                continue;
            rows.Add(new HomeModelRow(m.Id, m.Detail, Live: true,
                                      Loaded: m.Status.Trim().Equals("loaded", StringComparison.OrdinalIgnoreCase),
                                      Stats: StatsOf(m.Id), Key: m.Id));
        }

        var ids = string.Join("|", rows.Select(r => r.Id + ":" + r.Detail + ":" + r.Stats));
        if (ids == lastDisplayIds)
            return;
        lastDisplayIds = ids;
        DisplayModels.Clear();
        foreach (var r in rows)
            DisplayModels.Add(r);
        Raise(nameof(ModelsEmptyText));
    }

    /// <summary>The context a file's config entry carries, when one points at the file at all.</summary>
    private long EntryContextOf(string file)
    {
        var entry = getConfig()?.ModelEntries()
            .FirstOrDefault(e => SameModelFile(ConfigDocument.Text(e, "model"), file));
        return entry is null ? 0 : ConfigDocument.Int(entry, "max-context") ?? 0;
    }

    /// <summary>Each local file's place in the config, for pairing in the config's own order.</summary>
    private Dictionary<string, int> EntryOrder()
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var entries = getConfig()?.ModelEntries();
        if (entries is null)
            return order;
        for (var i = 0; i < entries.Count; i++)
        {
            var name = Path.GetFileName(ConfigDocument.Text(entries[i], "model")?.Trim() ?? "");
            if (name.Length > 0 && !order.ContainsKey(name))
                order[name] = i;
        }
        return order;
    }

    // ---- what the running server serves, by its own word ------------------------------------------------------
    // A pack's name shares nothing with its file's name, and the running server's config may not even be
    // the one on disk now. For a file the pairing and the names both miss, the router is asked directly:
    // /metrics with a model name answers 200 for a name one of its services serves, 404 for the rest —
    // a read, never a load. One ask per file, refreshed now and then in case the server changed its mind.
    private readonly Dictionary<string, bool> probedServed = new(StringComparer.OrdinalIgnoreCase);
    private int probing;
    private DateTime probesAt = DateTime.MinValue;

    private void MaybeProbeServed()
    {
        if (!status.Reachable)
        {
            probedServed.Clear();
            return;
        }
        if (Interlocked.CompareExchange(ref probing, 1, 0) != 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                var refresh = DateTime.UtcNow - probesAt > TimeSpan.FromSeconds(30);
                var changed = false;
                foreach (var f in localModels.ToList())
                {
                    if (!refresh && probedServed.TryGetValue(f.Name, out var known) && known)
                        continue;                       // a served file stays served until the next refresh
                    var answer = await client.ServesAsync(f.Name);
                    if (answer is null)
                        continue;                       // no answer is not a no; ask again next beat
                    probesAt = DateTime.UtcNow;
                    if (!probedServed.TryGetValue(f.Name, out var old) || old != answer.Value)
                    {
                        probedServed[f.Name] = answer.Value;
                        changed = true;
                    }
                }
                if (changed)
                    OnUi(RebuildDisplay);
            }
            finally
            {
                Interlocked.Exchange(ref probing, 0);
            }
        });
    }

    /// <summary>A name without its model-file extension, so a served id and a folder file can be matched.</summary>
    private static string Stem(string name)
    {
        foreach (var ext in new[] { ".basalt", ".gguf" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return name[..^ext.Length];
        return name;
    }

    // ---- the models folder --------------------------------------------------------------------------------------
    private List<LocalModelRow> localModels = [];
    private DateTime lastLocalScan;
    private string lastDisplayIds = "";

    public IReadOnlyList<LocalModelRow> LocalModelRows => localModels;

    /// <summary>
    /// What the models folder holds, scanned rarely (at most every few seconds, and only when a status
    /// beat arrives) so a folder on a slow disk cannot make the panel stutter. A folder that cannot be
    /// read reads as empty, which the empty line in the card is honest about.
    /// </summary>
    private void MaybeRescanLocalModels(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now - lastLocalScan < TimeSpan.FromSeconds(5))
            return;
        lastLocalScan = now;

        var found = new List<LocalModelRow>();
        try
        {
            var folder = Settings.ModelsFolder;
            if (folder.Length > 0 && Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder))
                {
                    var ext = Path.GetExtension(file);
                    if (!ext.Equals(".basalt", StringComparison.OrdinalIgnoreCase) &&
                        !ext.Equals(".gguf", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var info = new FileInfo(file);
                    found.Add(new LocalModelRow(file, info.Length, info.LastWriteTimeUtc));
                }
        }
        catch
        {
            // an unreadable folder is an empty one; the card already says what it can
        }

        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        if (found.Count == localModels.Count &&
            found.Zip(localModels).All(p => p.First.File == p.Second.File && p.First.Bytes == p.Second.Bytes))
            return;
        localModels = found;
        lastDisplayIds = "";          // the merged list has to be rebuilt, not just compared
        RebuildDisplay();
    }

    /// <summary>The model a console-carried swap was heading for, or null while none is being said. The
    /// swap is one event, said once going in and once coming out — the seconds it spends loading do not
    /// renew it.</summary>
    private string? swapSaidTo;

    /// <summary>When the swap now under way began, as far as the app can tell: the beat its target last
    /// changed. What the click dialog quotes as "so far".</summary>
    private DateTime swapSince = DateTime.Now;
    private string swapTargetSeen = "";

    /// <summary>The model the swap's human words are heading for: after "→ " for a hand-over, after
    /// "loading " for a cold one.</summary>
    private static string SwapTargetOf(string words)
    {
        if (words.Length == 0)
            return "";
        var at = words.IndexOf("→", StringComparison.Ordinal);
        var start = at >= 0 ? at + 1 : "loading ".Length;
        if (start >= words.Length)
            return words;
        while (start < words.Length && words[start] == ' ')
            start++;
        var end = words.IndexOf(" —", StringComparison.Ordinal);
        if (end < start)
            end = words.IndexOf(" (", StringComparison.Ordinal);
        return end > start ? words[start..end].Trim() : words[start..].Trim();
    }

    private void RebuildWarnings(ServerStatus snapshot)
    {
        // the swap's target changing is the clock reset for "how long has it been at it"
        var target = snapshot.Swap is { } words ? SwapTargetOf(words) : "";
        if (!target.Equals(swapTargetSeen, StringComparison.Ordinal))
        {
            swapTargetSeen = target;
            swapSince = DateTime.Now;
        }

        var list = new List<string>();
        if (!string.IsNullOrEmpty(snapshot.Spill))
            list.Add("experts read from disk — " + snapshot.Spill);
        if (snapshot.Swap is not null)
            list.Add(snapshot.Swap);
        // A load the engine has gone silent about — no percent on /health, no end, and nothing in its
        // own log either — is either reading the file slowly or waiting on its memory check; the row
        // only says "loading", so the warning says the other half. The buckets hold the words still,
        // so the console says this three times an evening at most, not once a minute.
        if (snapshot.Swap is not null && status.LoadPercent is not > 0 && engineWordsSeen)
        {
            var quiet = (DateTime.UtcNow - engineWordsAt).TotalMinutes;
            var quietWords = quiet switch
            {
                > 120 => "more than two hours",
                > 60 => "more than an hour",
                > 30 => "more than half an hour",
                _ => "",
            };
            if (quietWords.Length > 0)
                list.Add($"no progress report from the engine in {quietWords} — it may be reading the file slowly, or waiting on its RAM check");
        }
        if (snapshot.Gpus.FirstOrDefault() is { } gpu && gpu.Temp >= 80)
            list.Add($"GPU at {gpu.Temp:0} °C");
        if (snapshot.Gpus.FirstOrDefault() is { } g2 && g2.PowerLimit > 0 && g2.Power >= g2.PowerLimit * 0.97)
            list.Add($"GPU at its power limit ({g2.PowerLimit:0} W)");
        // Windows' own figure, so this warning cannot contradict the RAM bar above it. The bar is calm until
        // the machine is genuinely out of room, and a Windows memory cache keeps "available" low by design,
        // which is why this line names what is left instead of a percentage.
        if (machine.Memory.Usable > 0 && machine.Memory.InUse / (double)machine.Memory.Usable > 0.97)
            list.Add("RAM is almost full — " +
                     BasaltLocator.HumanBytes(machine.Memory.Usable - machine.Memory.InUse) + " available");

        // From 0.3.3 the engine refuses to start with the answer room switched flat off: it takes a number or
        // auto. It is the exe on disk that decides, not the one answering right now: the refusal would happen on
        // the next start, which is exactly when this is worth saying. The version read is cached.
        if (getConfig() is { AnswerRoomSwitchedOff: true } &&
            UpdateChecker.ParseVersion(InstalledFromExe()) is { } here && here >= new Version(0, 3, 3))
            list.Add("“answer-room”: false stops basalt 0.3.3 and newer from starting — switch it back on, or give it a number in the Raw JSON tab");

        if (!string.IsNullOrEmpty(snapshot.Error))
            list.Add(snapshot.Error!);

        if (list.Count == Warnings.Count && list.SequenceEqual(Warnings))
            return;

        // The card is gone; the console is where these live now. Each line is said once — when it first
        // appears — and again only if it went away and came back.
        var before = Warnings.ToHashSet(StringComparer.Ordinal);
        foreach (var w in list)
            if (!before.Contains(w))
                Append("warning: " + w, LogSource.App);

        Warnings.Clear();
        foreach (var w in list)
            Warnings.Add(w);

        // A swap is said once when it starts (the line above does that, its words hold still while the
        // model comes in) and once when it ends. A swap that ends while the server is gone says nothing —
        // the stop already had its lines.
        if (target.Length > 0)
            swapSaidTo = target;
        else if (swapSaidTo is not null)
        {
            var to = swapSaidTo;
            swapSaidTo = null;
            if (snapshot.Reachable)
                Append($"app: the swap to {to} ended", LogSource.App);
        }
    }

    private void RefreshCommands()
    {
        foreach (var cmd in new ICommand[] { StartCommand, StopCommand, RestartCommand, LoadCommand, UnloadCommand, OpenWebUiCommand, OpenApiCommand })
            (cmd as RelayCommand)?.RaiseCanExecuteChanged();
        Raise(nameof(IsRunning));
        Raise(nameof(StatusText));
    }

    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }
}

/// <summary>One row of the Models card: a model the server serves, or a file in the models folder.</summary>
public sealed record HomeModelRow(string Id, string Detail, bool Live, bool Loaded = false,
                                  string Stats = "", string Key = "")
{
    /// <summary>The family mark the row wears; null when the name matches no known family, and a null row wears no tile.</summary>
    public ModelIdentity.Identity? Badge => ModelIdentity.For(Id);

    public bool HasBadge => Badge is not null;

    /// <summary>Whether the row stands for a file in the models folder: a file has a config entry to edit
    /// and a folder to show; a model the server serves without one has neither.</summary>
    public bool HasFile => !Live;
}

/// <summary>A model file sitting in the models folder, as the folder itself reports it.</summary>
public sealed record LocalModelRow(string File, long Bytes, DateTime Changed)
{
    public string Name => Path.GetFileName(File);
    public string Detail => (Bytes > 0 ? BasaltLocator.HumanBytes(Bytes) : "size unknown") +
                            (Changed == default ? "" : " · " + Changed.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
}
