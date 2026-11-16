using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace BasaltUI.Core;

public sealed record GpuRow(int Index, string? Name, double Util, long MemUsed, long MemTotal, double Temp,
                            double Power, double PowerLimit, double PcieGen, double PcieRx, double PcieTx);

public sealed record RequestRow(string Id, string Path, double Time, double DurationS, string Finish,
                                long PromptTokens, long Reused, long OutputTokens, double DecodeTokS,
                                double HitRate, double TtftS, double PcieShare, double? SsdShare,
                                long DraftsOffered, long DraftsAccepted);

public sealed record ModelRow(string Id, string Status, bool Asleep, long NCtx, bool Vision)
{
    /// <summary>
    /// The second line under a model's name. Words rather than a raw True/False: a list of models is
    /// something you read in a second, and "False" makes you stop and work out what it was answering.
    /// </summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();
            var state = Status.Trim().ToLowerInvariant();
            if (state.Length > 0) parts.Add(state);
            if (NCtx > 0) parts.Add(NCtx.ToString("N0", CultureInfo.CurrentCulture) + " ctx");
            if (Asleep && !state.Contains("asleep", StringComparison.Ordinal)) parts.Add("asleep");
            if (Vision) parts.Add("vision");
            return string.Join(" · ", parts);
        }
    }
}

public sealed record RequestQueueRow(string Api, string Model, long PromptTokens, double WaitedS);

/// <summary>
/// One reading of a running server, flattened into what the UI shows. Everything is optional: a field a
/// newer or older basalt leaves out reads as zero or unset instead of breaking the panel.
/// </summary>
public sealed record ServerStatus
{
    public required bool Reachable { get; init; }
    public string? Error { get; init; }
    public DateTime SeenAt { get; init; }

    // health
    public string State { get; init; } = "stopped";                  // ready | loading | unloaded …
    public string ModelName { get; init; } = "";
    public bool Loaded { get; init; }
    public bool Loading { get; init; }
    public double? LoadPercent { get; init; }
    public long MaxContext { get; init; }
    public bool Images { get; init; }
    public bool ApiKeyRequired { get; init; }
    public string? Swap { get; init; }

    // engine block
    public string EngineVersion { get; init; } = "";
    public string Quant { get; init; } = "";
    public string Kv { get; init; } = "";
    public string GpuIds { get; init; } = "";
    public long ExpertSlots { get; init; }
    public long ExpertTotal { get; init; }
    public long ExpertCacheMib { get; init; }
    public long VramFreeMib { get; init; }
    public long ArenaMib { get; init; }
    public int Spec { get; init; }
    public int PoolWorkers { get; init; }
    public double PcieFrac { get; init; }
    public string? Spill { get; init; }

    // live
    public string LiveState { get; init; } = "";
    public int Queued { get; init; }
    public int SlotsTotal { get; init; }
    public int SlotsActive { get; init; }
    public int QueueDepth { get; init; }
    public double? TokS { get; init; }
    public double? PrefillTokS { get; init; }
    public double ElapsedS { get; init; }
    public long LivePromptTokens { get; init; }
    public long LiveGenerated { get; init; }
    public IReadOnlyList<RequestQueueRow> Queue { get; init; } = [];

    // machine
    public double CpuPercent { get; init; }
    public long RamUsed { get; init; }
    public long RamTotal { get; init; }
    public double DiskReadMb { get; init; }
    public IReadOnlyList<GpuRow> Gpus { get; init; } = [];
    public string CpuName { get; init; } = "";
    public int Cores { get; init; }
    public int Threads { get; init; }
    public string Driver { get; init; } = "";
    public string Cuda { get; init; } = "";

    // lists
    public IReadOnlyList<RequestRow> Requests { get; init; } = [];
    public IReadOnlyList<ModelRow> Models { get; init; } = [];

    // charts (the server keeps its own 60-sample history, about one per second)
    public double[] TokSHistory { get; init; } = [];
    public double[] PrefillHistory { get; init; } = [];
    public double[][] SlotHistories { get; init; } = [];
    public double[] GpuUtilHistory { get; init; } = [];
    public double[] GpuPowerHistory { get; init; } = [];
    public double[] GpuTempHistory { get; init; } = [];

    // /props extras, only fetched once in a while
    public string BuildInfo { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public bool Sleeping { get; init; }

    public bool Busy => LiveState is not ("" or "idle") || SlotsActive > 0 || Queued > 0;
    public bool Warning => Spill is not null || PcieFrac > 0.9 || Swap is not null;

    public static ServerStatus Down(string error) => new()
    {
        Reachable = false,
        Error = error,
        SeenAt = DateTime.Now,
    };
}

/// <summary>
/// The HTTP side of the app: basalt already publishes everything the stats page shows, so the panel polls
/// the same endpoints rather than parsing the console log. Unauthenticated endpoints are polled freely;
/// authentication is only sent for the server's own pages when the user set an api key.
/// </summary>
public sealed class BasaltClient
{
    private static readonly HttpClient Http = Create(TimeSpan.FromSeconds(2.5));
    /// <summary>A load or unload blocks for the whole weights load — minutes for a big pack — so the control
    /// posts get a client with room to wait. The polls keep the short one: a hung poll must not stall the panel.</summary>
    private static readonly HttpClient Slow = Create(TimeSpan.FromMinutes(15));

    private static HttpClient Create(TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 4,
        };
        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private string baseUrl = "http://127.0.0.1:8080";

    public void Configure(int port, string host, string? apiKey)
    {
        baseUrl = $"http://{HostFor(host)}:{port}";
        ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
    }

    public string? ApiKey { get; private set; }
    public string BaseUrl => baseUrl;

    private static string HostFor(string host)
    {
        var h = host.Trim();
        if (h.Length == 0 || h is "0.0.0.0" or "::" or "*")
            return "127.0.0.1";
        return h.Contains(':') && !h.StartsWith('[') ? "[" + h + "]" : h;
    }

    public Uri Url(string path) => new(baseUrl + path);
    public bool HasKey => ApiKey is not null;

    /// <summary>GETs a path and hands the parsed root to <paramref name="parse"/>. Null means unreachable.</summary>
    private async Task<T?> GetAsync<T>(string path, Func<JsonElement, T> parse, bool withKey,
                                       bool[]? failed = null, CancellationToken ct = default) where T : class
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(path));
            if (withKey && ApiKey is not null)
                request.Headers.Add("Authorization", "Bearer " + ApiKey);

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                failed?[0] = true;
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            return parse(doc.RootElement);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            failed?[0] = true;
            return null;
        }
    }

    // ---- endpoints --------------------------------------------------------------------------------------
    /// <summary>GET /health — the only status endpoint that answers without a key.</summary>
    public async Task<HealthResult?> HealthAsync(CancellationToken ct = default)
        => await GetAsync("/health", HealthResult.Parse, withKey: false, ct: ct);

    /// <summary>GET /metrics — the whole stats payload: engine, live, machine, requests, 60-sample history.</summary>
    public async Task<ServerStatus?> MetricsAsync(CancellationToken ct = default)
        => await GetAsync("/metrics", ServerStatusParser.ParseMetrics, withKey: true, ct: ct);

    /// <summary>GET /api/logs?after=&lt;id&gt;&amp;limit=N — the server's own console buffer, paged by id cursor.</summary>
    public async Task<LogPage> LogsAsync(int after = 0, CancellationToken ct = default)
    {
        var page = await GetAsync($"/api/logs?after={after}&limit=400", root => LogPage.Parse(root, after),
                                  withKey: true, ct: ct);
        return page ?? LogPage.Empty;
    }

    /// <summary>
    /// GET /props — the slow-changing facts: which build is answering, the file behind the loaded alias, and
    /// whether the model is parked in RAM. Fetched now and then, never on the hot path; the chat template it
    /// also returns is deliberately ignored.
    /// </summary>
    public async Task<PropsResult?> PropsAsync(CancellationToken ct = default)
        => await GetAsync("/props", PropsResult.Parse, withKey: true, ct: ct);

    /// <summary>POST a control path (/load, /unload, /v1/load with a model). No Origin: basalt rejects a control call from any other page.</summary>
    public async Task<(bool Ok, string Detail)> PostAsync(string path, string? jsonBody, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url(path));
            if (ApiKey is not null)
                request.Headers.Add("Authorization", "Bearer " + ApiKey);
            request.Content = new StringContent(jsonBody ?? "{}", Encoding.UTF8, "application/json");

            using var response = await Slow.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var detail = body.Length > 240 ? body[..240] + "…" : body;
            return (response.IsSuccessStatusCode, (int)response.StatusCode + " " + detail.ReplaceLineEndings(" "));
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }
}

/// <summary>One page of the server's log buffer. Ids restart when the server restarts, which is detected here.</summary>
public sealed record LogPage(int Last, IReadOnlyList<string> Lines, bool Restarted)
{
    public static readonly LogPage Empty = new(0, [], false);

    public static LogPage Parse(JsonElement root, int askedAfter)
    {
        var last = root.Get("last").I(0);
        var lines = root.Get("lines").Arr().Select(l => l.ValueKind == JsonValueKind.String
                                                           ? l.GetString() ?? ""
                                                           : $"[{l.Get("src").S("srv")}/{l.Get("lvl").S("info")}] {Message(l)}")
                        .Where(t => t.Length > 0)
                        .ToArray();
        // ids count up from 0; a smaller last than the cursor we sent means the server restarted
        return new LogPage(last, lines, askedAfter > 0 && last < askedAfter);
    }

    /// <summary>The body of a log line: newer builds call it msg, older ones text.</summary>
    private static string Message(JsonElement line)
    {
        var msg = line.Get("msg").S();
        return msg.Length > 0 ? msg : line.Get("text").S();
    }
}

public sealed record PropsResult(string BuildInfo, string ModelPath, bool Sleeping)
{
    public static PropsResult Parse(JsonElement root) => new(root.Get("build_info").S(),
                                                              root.Get("model_path").S(),
                                                              root.Get("is_sleeping").B());
}

public sealed record HealthResult(bool Ok, string State, string Model, bool Loaded, bool Loading, double? Percent,
                                  long MaxContext, bool Images, bool ApiKeyRequired, string? Swap)
{
    /// <summary>/health answers without a key, so it doubles as the “is anything listening there?” probe.</summary>
    public static HealthResult Parse(JsonElement root)
    {
        var loaded = root.Get("loaded").B();
        var loading = root.Get("loading").B();
        return new HealthResult(
            Ok: root.Get("status").S("ok") is "ok" or "loading",
            State: loading ? "loading" : loaded ? "ready" : "unloaded",
            Model: root.Get("model").S(),
            Loaded: loaded,
            Loading: loading,
            Percent: root.Get("percent").DN(),
            MaxContext: root.Get("max_context").L(),
            Images: root.Get("images").B(),
            ApiKeyRequired: root.Get("api_key").B(),
            Swap: root.Get("swap") is { } s && s.ValueKind != JsonValueKind.Null ? s.ToString() : null);
    }
}

/// <summary>Turns the two JSON payloads into one ServerStatus.</summary>
internal static class ServerStatusParser
{
    public static ServerStatus ParseMetrics(JsonElement root)
    {
        var engine = root.Get("engine");
        var live = root.Get("live");
        var hw = root.Get("hardware");
        var stat = root.Get("hardware_static");
        var hist = root.Get("history");

        var health = root.Get("health");

        var gpus = hw.Get("gpus").Arr().Select(g => new GpuRow(
            g.Get("index").I(),
            null,
            g.Get("util").D(),
            g.Get("mem_used").L(),
            g.Get("mem_total").L(),
            g.Get("temp").D(),
            g.Get("power").D(),
            g.Get("power_limit").D(),
            g.Get("pcie_gen").D(),
            g.Get("pcie_rx_mb").D(),
            g.Get("pcie_tx_mb").D())).ToList();

        var staticNames = new Dictionary<int, string?>();
        foreach (var g in stat.Get("gpus").Arr())
            staticNames[g.Get("index").I()] = g.Get("name").SR();
        for (var i = 0; i < gpus.Count; i++)
            gpus[i] = gpus[i] with { Name = staticNames.GetValueOrDefault(gpus[i].Index) ?? gpus[i].Name };

        var requests = root.Get("requests").Arr().Select(r => new RequestRow(
            r.Get("id").S(),
            r.Get("path").S(),
            r.Get("time").D(),
            r.Get("duration_s").D(),
            r.Get("finish").S(),
            r.Get("prompt_tokens").L(),
            r.Get("reused").L(),
            r.Get("output_tokens").L(),
            r.Get("decode_tok_s").D(),
            r.Get("hit_rate").D(),
            r.Get("ttft_s").D(),
            r.Get("pcie_share").D(),
            r.Get("ssd_share").DN(),
            r.Get("drafts_offered").L(),
            r.Get("drafts_accepted").L())).ToList();

        var models = root.Get("models").Arr().Select(m => new ModelRow(
            m.Get("id").S(),
            m.Get("status").Get("value").S(),
            m.Get("asleep").B(),
            m.Get("meta").Get("n_ctx").L(),
            m.Get("architecture").Get("input_modalities").Arr().Any(e => e.ToString().Contains("image")))).ToList();

        var queue = live.Get("queue").Arr().Select(q => new RequestQueueRow(
            q.Get("api").S(), q.Get("model").S(), q.Get("prompt_tokens").L(), q.Get("waited_s").D())).ToList();

        var slotHist = hist.Get("slot_tok_s").Arr().Select(a => a.Numbers()).ToArray();

        var gpuHist = hist.Get("gpus").Arr().FirstOrDefault();
        var swap = DescribeSwap(root.Get("swap"));
        var spill = DescribeSpill(engine.Get("spill"));
        var load = live.Get("load");

        return new ServerStatus
        {
            Reachable = true,
            SeenAt = DateTime.Now,
            State = live.Get("state").S("idle"),
            ModelName = engine.Get("model").S(),
            Loaded = live.Get("state").S() is not ("loading" or "unloaded"),
            Loading = live.Get("state").S() == "loading",
            LoadPercent = load.Get("percent").DN(),
            MaxContext = engine.Get("max_context").L(),
            Swap = swap,

            EngineVersion = engine.Get("version").S(),
            Quant = engine.Get("quant").S(),
            Kv = engine.Get("kv").S(),
            GpuIds = engine.Get("gpus").S(),
            ExpertSlots = engine.Get("expert_slots").L(),
            ExpertTotal = engine.Get("expert_total").L(),
            ExpertCacheMib = engine.Get("expert_cache_mib").L(),
            VramFreeMib = engine.Get("vram_free_mib").L(),
            ArenaMib = engine.Get("arena_mib").L(),
            Spec = engine.Get("spec").I(),
            PoolWorkers = engine.Get("pool_workers").I(),
            PcieFrac = D(engine.Get("pcie_frac")),
            Spill = spill,

            LiveState = live.Get("state").S(""),
            Queued = live.Get("queued").I(),
            SlotsTotal = live.Get("slots").Get("total").I(),
            SlotsActive = live.Get("slots").Get("active").I(),
            QueueDepth = live.Get("slots").Get("queue_depth").I(),
            TokS = live.Get("tok_s").DN(),
            PrefillTokS = live.Get("prefill_tok_s_mean").DN(),
            ElapsedS = live.Get("elapsed_s").D(),
            LivePromptTokens = live.Get("prompt_tokens").L(),
            LiveGenerated = live.Get("generated").L(),
            Queue = queue,

            CpuPercent = hw.Get("cpu").D(),
            RamUsed = hw.Get("ram_used").L(),
            RamTotal = hw.Get("ram_total").L(),
            DiskReadMb = hw.Get("disk_read_mb").D(),
            Gpus = gpus,
            CpuName = stat.Get("cpu_name").S(),
            Cores = stat.Get("cores").I(),
            Threads = stat.Get("threads").I(),
            Driver = stat.Get("driver").S(),
            Cuda = stat.Get("cuda").S(),

            Requests = requests,
            Models = models,

            TokSHistory = hist.Get("tok_s").Filled(),
            PrefillHistory = hist.Get("prefill_tok_s_mean").Filled(),
            SlotHistories = slotHist,
            GpuUtilHistory = gpuHist.Get("util").Filled(),
            GpuPowerHistory = gpuHist.Get("power").Filled(),
            GpuTempHistory = gpuHist.Get("temp").Filled(),
        };
    }

    /// <summary>pcie_frac arrives as the string "0.55" in some builds and as a number in others.</summary>
    private static double D(JsonElement? el)
    {
        if (el is not { } e)
            return 0;
        if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d))
            return d;
        if (e.ValueKind == JsonValueKind.String &&
            double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            return p;
        return 0;
    }

    /// <summary>swap is {"from","to","phase","waiting"} while one model gives way to another.</summary>
    private static string? DescribeSwap(JsonElement? el)
    {
        if (el is not { } e || e.ValueKind != JsonValueKind.Object)
            return null;
        var from = e.Get("from").S("?");
        var to = e.Get("to").S("?");
        var phase = e.Get("phase").S();
        var waiting = e.Get("waiting").I();
        return waiting > 0 ? $"swapping {from} → {to} ({phase}, {waiting} waiting)" : $"swapping {from} → {to} ({phase})";
    }

    /// <summary>engine.spill names the tier experts are read from; the bytes are the size the stats page shows.</summary>
    private static string? DescribeSpill(JsonElement? el)
    {
        if (el is not { } e)
            return null;
        if (e.ValueKind == JsonValueKind.String)
            return e.GetString() is { Length: > 0 } s ? s : null;
        if (e.ValueKind != JsonValueKind.Object)
            return null;
        var where = e.Get("where").S(e.Get("tier").S("ssd"));
        var bytes = e.Get("bytes").L();
        return bytes > 0 ? $"{where}: {bytes / 1073741824.0:0.#} GiB of experts" : where;
    }
}
