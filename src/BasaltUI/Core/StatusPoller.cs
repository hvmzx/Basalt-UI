namespace BasaltUI.Core;

/// <summary>
/// Watches a basalt server on its own address: a short status beat every second, the full stats payload every
/// other beat, and the server's own log buffer only when we did not start the process ourselves (in which case
/// there is no console to read).
/// </summary>
public sealed class StatusPoller : IDisposable
{
    private readonly BasaltClient client;
    private Timer? timer;
    private int beating;
    private int tick;
    private ServerStatus? last;
    private readonly object gate = new();

    public StatusPoller(BasaltClient client) => this.client = client;

    /// <summary>Fires on a thread-pool thread; the UI marshals what it needs.</summary>
    public event Action<ServerStatus>? StatusChanged;

    /// <summary>Console lines from a server we did not start, newest last, already formatted.</summary>
    public event Action<IReadOnlyList<string>>? ServerLogLines;

    public ServerStatus? Current { get { lock (gate) return last; } }

    /// <summary>
    /// The machine's own memory, read on every beat with it: the RAM and GPU figures the window shows are
    /// Windows' figures (Task Manager's), so they stay right and stay live whether or not a server answers.
    /// </summary>
    public MachineMemory.Sample Machine { get; private set; } = MachineMemory.Read();

    public void Configure(int port, string host, string? apiKey, bool readServerLog)
    {
        client.Configure(port, host, apiKey);
        ReadServerLog = readServerLog;
    }

    public bool ReadServerLog { get; set; }

    public void Start(int intervalMs)
    {
        Stop();
        timer = new Timer(_ => _ = BeatAsync(), null, 200, Math.Max(300, intervalMs));
    }

    public void Stop()
    {
        timer?.Dispose();
        timer = null;
        lock (gate) last = null;
    }

    /// <summary>One immediate beat, for the moment the server starts or the settings change.</summary>
    public async Task BeatAsync()
    {
        if (Interlocked.Exchange(ref beating, 1) == 1)
            return;
        try
        {
            var current = Interlocked.Increment(ref tick);
            ServerStatus? status = null;

            if (current % 2 == 0 || last is null)
            {
                status = await client.MetricsAsync();
                if (status is not null)
                    status = status with { Reachable = true };
            }
            status ??= Current;

            var health = await client.HealthAsync();
            if (health is not null)
            {
                var before = status ?? new ServerStatus { Reachable = true, SeenAt = DateTime.Now };
                status = before with
                {
                    Reachable = true,
                    Error = null,
                    SeenAt = DateTime.Now,
                    Loaded = health.State is "ready",
                    Loading = health.State is "loading",
                    ModelName = string.IsNullOrEmpty(before.ModelName) ? health.Model : before.ModelName,
                    MaxContext = health.MaxContext > 0 ? health.MaxContext : before.MaxContext,
                    Images = health.Images,
                    ApiKeyRequired = health.ApiKeyRequired,
                    // the swap ends when the engine stops reporting it — not carried forward from an
                    // older beat, or a finished swap would sit on the dashboard forever
                    Swap = health.Swap,
                    LoadPercent = health.Percent ?? before.LoadPercent,
                };
            }
            else if (status is { Reachable: true } && Current?.Reachable == true && tick % 3 == 0)
            {
                // the stats beat worked but the health beat did not: keep showing the stats, mark them stale
                status = status with { Error = "the server stopped answering /health" };
            }

            if (status is null)
            {
                status = ServerStatus.Down(client.BaseUrl + " is not answering");
            }
            else
            {
                logLast = await ReadServerLogAsync(logLast, current, ct: default);
            }

            status = await MergePropsAsync(status, current);

            Machine = MachineMemory.Read();

            lock (gate) last = status;
            StatusChanged?.Invoke(status);
        }
        finally
        {
            Interlocked.Exchange(ref beating, 0);
        }
    }

    private int logLast;
    private PropsResult? props;

    /// <summary>
    /// The slow-changing facts (which build, which file, asleep or not) ride along on every status we hand out,
    /// but /props itself is only fetched on first contact and then every 40th beat.
    /// </summary>
    private async Task<ServerStatus> MergePropsAsync(ServerStatus status, int current)
    {
        if (!status.Reachable)
        {
            props = null;                       // the next server to answer may be a different build entirely
            return status;
        }
        try
        {
            props ??= await client.PropsAsync();
            if (current % 40 == 0)
                props = await client.PropsAsync() ?? props;
        }
        catch (Exception e)
        {
            AppPaths.Log("props: " + e.Message);
        }
        return props is null ? status : status with
        {
            BuildInfo = props.BuildInfo,
            ModelPath = props.ModelPath,
            Sleeping = props.Sleeping,
        };
    }

    /// <summary>
    /// The server's own buffer, paged by id cursor and only when we did not start the process ourselves:
    /// a process we launched already gives us its console. Ids restart when the server restarts, which is
    /// detected here and answered by starting the cursor over.
    /// </summary>
    private async Task<int> ReadServerLogAsync(int cursor, int current, CancellationToken ct)
    {
        if (!ReadServerLog || current % 3 != 0)
            return cursor;

        try
        {
            var page = await client.LogsAsync(cursor, ct);
            if (page.Restarted)
                cursor = 0;
            if (page.Lines.Count == 0)
                return page.Last == 0 ? cursor : page.Last;

            ServerLogLines?.Invoke(page.Lines);
            return Math.Max(page.Last, cursor);
        }
        catch (Exception e)
        {
            AppPaths.Log("server log: " + e.Message);
            return cursor;
        }
    }

    public void Dispose() => Stop();
}
