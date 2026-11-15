using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace BasaltUI.Core;

/// <summary>What to fetch, where to put it, and what the finished file has to hash to.</summary>
public sealed record DownloadJob(Uri Url, string FilePath, string ExpectedSha256 = "", long ExpectedSize = 0,
                                 string Label = "")
{
    public string Name => Path.GetFileName(FilePath);

    /// <summary>Bytes land here, and only move to <see cref="FinalPath"/> once they are all in and checked.</summary>
    public string PartPath => FilePath + ".part";

    /// <summary>Which parts of <see cref="PartPath"/> are already down, so an interrupted job can carry on.</summary>
    public string CheckpointPath => FilePath + ".part.json";

    public string FinalPath => FilePath;
}

public sealed record DownloadProgress(long Done, long Total, double BytesPerSecond, int ActiveConnections, string Phase)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;

    public string SpeedText => BytesPerSecond > 0 ? BasaltLocator.HumanBytes((long)BytesPerSecond) + "/s" : "—";

    public string Text
        => Phase switch
        {
            "checking" => "checking the sha256 of what came down…",
            "done" => "finished",
            _ => Total > 0
                ? $"{Done * 100.0 / Total:0.#}%  {BasaltLocator.HumanBytes(Done)} of {BasaltLocator.HumanBytes(Total)} · {SpeedText}"
                : $"{BasaltLocator.HumanBytes(Done)} · {SpeedText}",
        };
}

/// <summary>
/// The fast way to pull tens of gigabytes down.
///
/// One HTTPS connection to a CDN is limited less by the line than by that single TCP conversation: the
/// bandwidth-delay product and loss recovery hold it well below what the machine can take. Asking for the
/// file in byte ranges and fetching those ranges on several connections at once is typically 2–5× faster on
/// a home line. It also suits Hugging Face, which answers every request with its own freshly signed CDN
/// address — each connection needs exactly that.
///
/// The split gives resume for free. Each range remembers how far it got, so an interrupted 70 GiB download
/// carries on where it stopped. The file only appears under its real name once every byte is in place and,
/// when the repo published one, the sha256 matches.
/// </summary>
public static class SegmentedDownload
{
    private const int Block = 1 << 20;          // read and write one MiB at a time
    private const long MinSegment = 8L << 20;   // below 8 MiB the range handshake costs more than the parallelism saves

    /// <summary>Returns null when the file is in place, or the reason it is not.</summary>
    public static async Task<string?> RunAsync(DownloadJob job, int connections, IProgress<DownloadProgress>? progress,
                                               Action<string>? trace, CancellationToken ct)
    {
        connections = Math.Clamp(connections, 1, 16);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(job.FinalPath) ?? ".");

            var (total, ranged) = await ProbeAsync(job, ct).ConfigureAwait(false);
            if (total <= 0)
                return "the server would not say how big the file is";
            if (job.ExpectedSize > 0 && total != job.ExpectedSize)
                return $"{job.Name}: the server offered {BasaltLocator.HumanBytes(total)}, the listing said {BasaltLocator.HumanBytes(job.ExpectedSize)}";

            trace?.Invoke($"{job.Name}: {BasaltLocator.HumanBytes(total)}, " +
                          (ranged && connections > 1 ? connections + " connections" : "one connection"));

            if (!ranged || (connections == 1 && !File.Exists(job.CheckpointPath)))
                return await OneStreamAsync(job, total, progress, trace, ct).ConfigureAwait(false);
            return await ManyRangesAsync(job, total, connections, progress, trace, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            trace?.Invoke(job.Name + ": stopped; what came down is kept, run it again to carry on");
            return null;
        }
        catch (Exception e)
        {
            trace?.Invoke(job.Name + ": " + e.Message);
            return Net.Explain(e);
        }
    }

    // ---- what is out there ------------------------------------------------------------------------------------
    /// <summary>Ask for a single byte: the answer carries the whole length and tells us whether ranges work here.</summary>
    private static async Task<(long Total, bool Ranged)> ProbeAsync(DownloadJob job, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, job.Url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        HuggingFace.ApplyAuth(request);

        using var response = await Net.Streams.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        var status = response.StatusCode;
        if (status is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
        {
            var hint = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? " — this repo needs an account or an accepted licence; set HF_TOKEN in the environment"
                : "";
            throw new HttpRequestException($"{(int)status} {response.ReasonPhrase} for {job.Url}{hint}");
        }

        if (response.Content.Headers.ContentRange is { } range && range.Length is long total && total > 0)
            return (total, true);
        return (response.Content.Headers.ContentLength ?? -1, false);
    }

    // ---- one connection ---------------------------------------------------------------------------------------
    private static async Task<string?> OneStreamAsync(DownloadJob job, long total, IProgress<DownloadProgress>? progress,
                                                     Action<string>? trace, CancellationToken ct)
    {
        var already = File.Exists(job.PartPath) ? new FileInfo(job.PartPath).Length : 0L;
        if (already > total)
            already = 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, job.Url);
        if (already > 0)
            request.Headers.Range = new RangeHeaderValue(already, null);
        HuggingFace.ApplyAuth(request);

        using var response = await Net.Streams.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
            return $"{(int)response.StatusCode} fetching {job.Name}";

        if (already > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            // answering a resume request with the whole file means it is sending from zero
            already = 0;
            trace?.Invoke(job.Name + ": starting over, the server would not resume");
        }

        long done;
        using (var file = new FileStream(job.PartPath, already > 0 ? FileMode.Open : FileMode.Create,
                                         FileAccess.Write, FileShare.None, Block, FileOptions.Asynchronous))
        {
            done = await PumpAsync(response, file, total, already, progress, ct).ConfigureAwait(false);
        }

        if (done != total)
            return $"{job.Name}: stopped at {BasaltLocator.HumanBytes(done)} of {BasaltLocator.HumanBytes(total)} — run it again to carry on";
        return await FinishAsync(job, total, progress, trace, ct).ConfigureAwait(false);
    }

    // ---- many connections on byte ranges -----------------------------------------------------------------------
    /// <summary>The counters the connections share. They live on an object so Interlocked can reach them.</summary>
    private sealed class Shared
    {
        public int Next = -1;
        public long Done;
        public int Active;
        public long LastSaveTicks;
    }

    private static async Task<string?> ManyRangesAsync(DownloadJob job, long total, int connections,
                                                       IProgress<DownloadProgress>? progress, Action<string>? trace,
                                                       CancellationToken ct)
    {
        var segment = RoundUp(Math.Max(total / (connections * 6L), MinSegment));

        // A checkpoint already on disk describes a layout. When it is for this same file, that layout wins:
        // bytes that came down yesterday are only worth something if changing the connection count in
        // Settings tomorrow does not throw them away.
        var saved = SavedLayout(job);
        if (saved.total == total && saved.segment >= MinSegment && saved.segment % (1L << 20) == 0)
            segment = saved.segment;

        var count = (int)((total + segment - 1) / segment);
        var written = new long[count];
        var shared = new Shared { LastSaveTicks = DateTime.UtcNow.Ticks };

        var resuming = File.Exists(job.PartPath) && LoadCheckpoint(job, segment, total, written);
        if (File.Exists(job.PartPath) && !resuming)
            File.Delete(job.PartPath);

        // Open keeps whatever came down before; Create starts the file over. Either way the file is made
        // whole up front, because every connection writes into its own window and nothing has to be ordered.
        string? stopping;
        using (var file = new FileStream(job.PartPath, resuming ? FileMode.Open : FileMode.Create,
                                         FileAccess.Write, FileShare.ReadWrite, Block, FileOptions.Asynchronous))
        {
            file.SetLength(total);

            shared.Done = written.Sum();

            using var reportGate = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var reporter = ReportAsync(progress, () => (Interlocked.Read(ref shared.Done), total, Volatile.Read(ref shared.Active)), reportGate.Token);

            var workers = new Task[connections];
            for (var w = 0; w < connections; w++)
                workers[w] = WorkerAsync(job, total, segment, count, written, shared, file, ct);

            var failure = await WaitForAllAsync(workers).ConfigureAwait(false);
            reportGate.Cancel();
            try
            {
                await reporter.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // the reporter ends when the download does
            }

            SaveCheckpoint(job, segment, total, written);
            stopping = failure is not null ? job.Name + ": " + failure.Message
                 : Interlocked.Read(ref shared.Done) != total
                     ? $"{job.Name}: stopped at {BasaltLocator.HumanBytes(Interlocked.Read(ref shared.Done))} of {BasaltLocator.HumanBytes(total)} — run it again to carry on"
                     : null;
        }

        // Closing the handle above is not a formality: checking the sha256 and moving the part file into place
        // are both refused by Windows while the file is still open for writing.
        if (stopping is not null)
        {
            trace?.Invoke(stopping);
            return stopping;
        }

        return await FinishAsync(job, total, progress, trace, ct).ConfigureAwait(false);
    }

    /// <summary>The per-connection loop: take the next range that still needs bytes, then write it into the file.</summary>
    private static async Task WorkerAsync(DownloadJob job, long total, long segment, int count, long[] written,
                                          Shared shared, FileStream file, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Block);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var index = Interlocked.Increment(ref shared.Next);
                if (index >= count)
                    return;

                var start = index * segment + Interlocked.Read(ref written[index]);
                var end = Math.Min(total - 1, (index + 1) * segment - 1);
                if (start > end)
                    continue; // this range came down in an earlier run

                Interlocked.Increment(ref shared.Active);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, job.Url);
                    request.Headers.Range = new RangeHeaderValue(start, end);
                    HuggingFace.ApplyAuth(request);

                    using var response = await Net.Streams.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                                                     .ConfigureAwait(false);
                    if (response.StatusCode != HttpStatusCode.PartialContent)
                        throw new HttpRequestException($"{(int)response.StatusCode}: the server would not send the bytes {start}–{end} of {job.Name}");

                    await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    long offset = start;
                    int read;
                    while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await RandomAccess.WriteAsync(file.SafeFileHandle, new ReadOnlyMemory<byte>(buffer, 0, read), offset, ct)
                                        .ConfigureAwait(false);
                        offset += read;
                        Interlocked.Add(ref shared.Done, read);

                        var landed = Interlocked.Add(ref written[index], read);
                        if (landed >= end - start + 1 || NeedsSave(shared))
                            SaveCheckpoint(job, segment, total, written);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref shared.Active);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>The progress file is written by one worker at a time, and no more than once every five seconds.</summary>
    private static bool NeedsSave(Shared shared)
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref shared.LastSaveTicks);
        if (now - last < TimeSpan.FromSeconds(5).Ticks)
            return false;
        return Interlocked.CompareExchange(ref shared.LastSaveTicks, now, last) == last;
    }

    // ---- after the bytes ---------------------------------------------------------------------------------------
    private static async Task<string?> FinishAsync(DownloadJob job, long total, IProgress<DownloadProgress>? progress,
                                                   Action<string>? trace, CancellationToken ct)
    {
        if (job.ExpectedSha256.Length == 64)
        {
            progress?.Report(new DownloadProgress(total, total, 0, 0, "checking"));
            var digest = await Task.Run(() => Hash(job.PartPath, ct), ct).ConfigureAwait(false);
            if (!string.Equals(digest, job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                trace?.Invoke($"{job.Name}: the sha256 came out {digest[..12]}…, not the {job.ExpectedSha256[..12]}… the repo says");
                return $"{job.Name} is not what the repo published — delete the .part file and try again";
            }

            trace?.Invoke(job.Name + ": sha256 matches what the repo published");
        }

        if (File.Exists(job.CheckpointPath))
            TryDelete(job.CheckpointPath);
        if (File.Exists(job.FinalPath))
            TryDelete(job.FinalPath);
        File.Move(job.PartPath, job.FinalPath);

        progress?.Report(new DownloadProgress(total, total, 0, 0, "done"));
        trace?.Invoke("saved " + job.FinalPath);
        return null;
    }

    private static string Hash(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, Block,
                                          FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(stream));
    }

    // ---- the small helpers ---------------------------------------------------------------------------------------
    private static async Task<long> PumpAsync(HttpResponseMessage response, FileStream file, long total, long from,
                                              IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(Block);
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var done = from;
            var speed = new Speed(total, done, progress);

            int read;
            while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await RandomAccess.WriteAsync(file.SafeFileHandle, new ReadOnlyMemory<byte>(buffer, 0, read), done, ct)
                                .ConfigureAwait(false);
                done += read;
                speed.Tick(done);
            }

            return done;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Task ReportAsync(IProgress<DownloadProgress>? progress, Func<(long Done, long Total, int Active)> snapshot,
                                    CancellationToken ct)
        => Task.Run(async () =>
        {
            if (progress is null)
                return;
            var speed = new Speed(0, 0, progress);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(400, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                var (d, t, a) = snapshot();
                speed.Tick(d, a);
            }
        }, CancellationToken.None);

    /// <summary>
    /// Every connection, not just the first to fail: the others are still writing into the file, and this method's
    /// caller closes that file the moment it returns. A cancelled connection is not a failure.
    /// </summary>
    private static async Task<Exception?> WaitForAllAsync(Task[] workers)
    {
        Exception? first = null;
        var open = workers.ToList();
        while (open.Count > 0)
        {
            var finished = await Task.WhenAny(open).ConfigureAwait(false);
            _ = open.Remove(finished);
            if (first is null && finished.Exception is { } aggregate &&
                aggregate.Flatten().InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) is { } real)
                first = real;
        }

        return first;
    }

    /// <summary>Percent, speed and the line beside it, smoothed over the last couple of seconds.</summary>
    private sealed class Speed
    {
        private readonly IProgress<DownloadProgress>? progress;
        private readonly int connections;
        private DateTime last = DateTime.UtcNow;
        private long lastBytes;
        private double bps;

        public Speed(long total, long startBytes, IProgress<DownloadProgress>? progress, int connections = 1)
        {
            this.progress = progress;
            this.connections = connections;
            lastBytes = startBytes;
            Total = total;
        }

        public long Total { get; private set; }

        public void Tick(long done, int active = -1, long total = 0)
        {
            if (progress is null)
                return;
            if (total > 0)
                Total = total;

            var now = DateTime.UtcNow;
            var dt = (now - last).TotalSeconds;
            if (dt < 0.4)
                return;

            bps = bps == 0 ? (done - lastBytes) / dt : bps * 0.65 + (done - lastBytes) / dt * 0.35;
            last = now;
            lastBytes = done;
            progress.Report(new DownloadProgress(done, Total, bps, active < 0 ? connections : active, "downloading"));
        }
    }

    private static void SaveCheckpoint(DownloadJob job, long segment, long total, long[] written)
    {
        try
        {
            var state = new JsonObject
            {
                ["segment"] = segment,
                ["total"] = total,
                ["written"] = new JsonArray([.. written.Select(w => (JsonNode)w)]),
            };
            File.WriteAllText(job.CheckpointPath, state.ToJsonString());
        }
        catch
        {
            // a checkpoint that did not get written costs the resume, never the file
        }
    }

    /// <summary>How a leftover checkpoint split the file, and for how big a file: zeros when there is nothing
    /// readable to agree with.</summary>
    private static (long segment, long total) SavedLayout(DownloadJob job)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(job.CheckpointPath)) is not JsonObject state)
                return (0, 0);
            var segment = state["segment"] is JsonValue s && s.TryGetValue(out long sv) ? sv : 0;
            var total = state["total"] is JsonValue t && t.TryGetValue(out long tv) ? tv : 0;
            return (segment, total);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static bool LoadCheckpoint(DownloadJob job, long segment, long total, long[] written)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(job.CheckpointPath)) is not JsonObject state)
                return false;
            if (state["segment"] is not JsonValue seg || !seg.TryGetValue(out long savedSegment) || savedSegment != segment)
                return false;
            if (state["total"] is not JsonValue tot || !tot.TryGetValue(out long savedTotal) || savedTotal != total)
                return false;
            if (state["written"]?.AsArray() is not { } rows || rows.Count != written.Length)
                return false;
            if (new FileInfo(job.PartPath).Length != total)
                return false;

            long have = 0;
            for (var i = 0; i < written.Length; i++)
            {
                if (rows[i] is not JsonValue v || !v.TryGetValue(out long w) || w < 0)
                    return false;
                written[i] = Math.Min(w, Math.Min(segment, total - i * segment));
                have += written[i];
            }

            AppPaths.Log($"{job.Name}: carrying on from {BasaltLocator.HumanBytes(have)} of {BasaltLocator.HumanBytes(total)}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Round to a whole MiB, so a range boundary is also a block boundary.</summary>
    private static long RoundUp(long value)
    {
        const long block = 1L << 20;
        return (value + block - 1) / block * block;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e)
        {
            AppPaths.Log("could not clear " + path + ": " + e.Message);
        }
    }
}
