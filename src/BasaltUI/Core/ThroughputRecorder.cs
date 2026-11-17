using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BasaltUI.Core;

/// <summary>
/// One line of a model's history: what it did during one engine session, as far as the app could see it.
/// </summary>
/// <remarks>
/// A session is one start of basalt: the engine counts itself from <c>totals.since</c>, and that is the
/// session's clock, so swapping in another model mid-run does not split a session — only the engine itself
/// ends one. When an engine answers without those counters, the app keeps its own: a session runs from the
/// moment the port first answered until the next moment it does again.
/// </remarks>
public sealed record ModelSession(DateTime Since, int Requests, double AvgDecodeTokS, double MaxDecodeTokS,
                                  double AvgPrefillTokS, double MaxPrefillTokS, bool Current, bool Gap,
                                  string Config = "")
{
    public string DecodeRange => MaxDecodeTokS > 0 || AvgDecodeTokS > 0
        ? $"{AvgDecodeTokS:0.#} · {MaxDecodeTokS:0.#} tok/s"
        : "—";

    public string PrefillRange => MaxPrefillTokS > 0 || AvgPrefillTokS > 0
        ? $"{AvgPrefillTokS:N0} · {MaxPrefillTokS:N0} tok/s"
        : "—";
}

/// <summary>
/// The app's memory of how fast each model has actually been: per model, per session of the engine, the
/// requests it watched and the speeds they ran at. It is the only reason a model row can say more than what
/// is happening this second, and the only thing that survives an app restart — which is why the open
/// session's running counts are in the file too, and the file is the hand-over.
/// </summary>
/// <remarks>
/// What the app can and cannot know: it counts requests as they arrive in the server's recent table, and it
/// attributes each to the model that was loaded then, by the model timeline it keeps from its own beats.
/// Requests that happened before the app first saw this server start are not lost — the engine's own
/// session totals are the difference, one row, flagged <see cref="ModelSession.Gap"/>. While the app is
/// closed and the engine keeps serving, that difference simply grows.
/// </remarks>
public sealed class ThroughputRecorder
{
    /// <summary>What one model is accumulating in the session under way, in the app's own running counts.</summary>
    private sealed class Acc
    {
        public long Key;
        public DateTime Since;
        public int Requests;
        public long Written;
        public double DecodeMs;
        public long PrefillNew;
        public double PrefillMs;
        public double MaxDecode;
        public double MaxPrefill;
        public string Context = "";                 // the config the watched server was serving
    }

    private sealed class ModelBook
    {
        public string Model = "";
        public readonly List<ModelSession> Closed = [];        // oldest first
        public Acc? Open;
    }

    private readonly Dictionary<string, ModelBook> books = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(DateTime At, string Model)> timeline = [];
    private readonly Dictionary<string, bool> seenRequests = new(StringComparer.Ordinal);
    private long episodeKey;                                   // a counter-less engine's session, in the app's own clock
    private long gappedForKey;                                 // the session this run already wrote a gap row for
    private string context = "";                               // the config the watched server is serving
    private bool reachableLastBeat;
    private bool dirty;
    private DateTime lastSaveAttempt;
    private readonly object gate = new();

    public ThroughputRecorder() => Load();

    /// <summary>Which config the watched server is serving from now on. Every session opened after this
    /// call wears the name, which is how a model held by two configs keeps two histories apart.</summary>
    public void SetContext(string configName)
    {
        lock (gate)
            context = configName ?? "";
    }

    /// <summary>
    /// One status beat, on the thread the screen already runs on: requests counted, sessions opened and
    /// closed, the file written. Nothing here may outlive that thread's rule.
    /// </summary>
    public void Observe(ServerStatus s)
    {
        if (!s.Reachable)
        {
            reachableLastBeat = false;
            episodeKey = 0;
            return;
        }

        long key;
        if (s.Totals is { Since: > 0 } t)
        {
            key = t.Since;
        }
        else
        {
            if (!reachableLastBeat)
                episodeKey = MillisSinceUnix();
            key = episodeKey;
        }
        reachableLastBeat = true;

        var loaded = LoadedModelOf(s);
        timeline.Add((DateTime.UtcNow, loaded));
        TrimTimeline();

        var anything = false;
        lock (gate)
        {
            // A new engine session: whatever the old one still had open is now closed history.
            foreach (var book in books.Values)
                if (book.Open is { } acc && acc.Key != key)
                    CloseLocked(book);
            if (gappedForKey > 0 && gappedForKey != key)
                gappedForKey = 0;

            // Count first, and only then look for the gap: the gap is what the engine's totals hold that
            // the app counted, and the app is counting this very beat's requests right now. Asked one beat
            // too early, the gap would swallow the requests the app was just about to take.
            foreach (var r in s.Requests)         // the server's recent table, newest first
                anything |= CountNewLocked(s, key, r);
            anything |= GapForLocked(s, key, loaded);
        }

        if (anything && (dirty || (DateTime.Now - lastSaveAttempt).TotalSeconds >= 3))
        {
            dirty = true;
            if ((DateTime.Now - lastSaveAttempt).TotalSeconds >= 3)
                SaveNow();
        }
        if (dirty && (DateTime.Now - lastSaveAttempt).TotalSeconds >= 3)
        {
            SaveNow();
        }
    }

    private bool CountNewLocked(ServerStatus s, long key, RequestRow r)
    {
        var idName = key + ":" + r.Id;
        if (!seenRequests.TryAdd(idName, true))
            return false;
        if (seenRequests.Count > 4000)
            foreach (var old in seenRequests.Keys.Take(2000).ToList())
                seenRequests.Remove(old);

        var fallback = LoadedModelOf(s);
        var model = ModelAt(r.Time, fallback);
        if (model is not { Length: > 0 })
            return false;

        var book = BookFor(model);
        var acc = book.Open ??= new Acc { Key = key, Since = SessionStartOf(key), Context = context };

        acc.Requests++;
        acc.Written += r.OutputTokens;
        if (r.DecodeTokS > 0 && r.OutputTokens > 0)
            acc.DecodeMs += r.OutputTokens / r.DecodeTokS * 1000.0;
        if (r.DecodeTokS > acc.MaxDecode)
            acc.MaxDecode = r.DecodeTokS;
        if (r.PrefillTokS > acc.MaxPrefill)
            acc.MaxPrefill = r.PrefillTokS;
        var fresh = r.PromptTokens - r.Reused;
        if (r.PromptMs > 0 && fresh > 0)
        {
            acc.PrefillNew += fresh;
            acc.PrefillMs += r.PromptMs;
        }
        return true;
    }

    /// <summary>
    /// The engine's session totals say how much happened; the app's own counts say how much of that it
    /// watched. On the first beat of this session the difference is the time the app was away — one row,
    /// under the model that is loaded now, with the speeds the engine itself recorded. It is the honest
    /// shape of "I was not there": a gap, not blank space pretending nothing happened.
    /// </summary>
    private bool GapForLocked(ServerStatus s, long key, string model)
    {
        var totals = s.Totals;
        if (totals is null || model.Length == 0)
            return false;
        if ((DateTime.UtcNow - SessionStartOf(key)).TotalSeconds < 60)
            return false;                            // present since the start: nothing missed
        if (gappedForKey == key)
            return false;

        var book = BookFor(model);
        var open = book.Open;
        var missedRequests = totals.Requests - (open?.Requests ?? 0);
        if (missedRequests <= 0)
            return false;                            // everything seen; the clock simply ran long
        var missedPromptMs = totals.PromptMs - (open?.PrefillMs ?? 0);
        var missedDecodeMs = totals.DecodeMs - (open?.DecodeMs ?? 0);
        var missedPrefillNew = totals.Prompt - totals.Reused - (open?.PrefillNew ?? 0);
        var missedWritten = totals.Written - (open?.Written ?? 0);

        book.Closed.Add(new ModelSession(
            SessionStartOf(key),
            (int)missedRequests,
            missedDecodeMs > 0 ? missedWritten / (missedDecodeMs / 1000.0) : 0,
            s.Best?.DecodeTokS ?? 0,                  // the engine's own record for this run
            missedPrefillNew > 0 && missedPromptMs > 0 ? missedPrefillNew / (missedPromptMs / 1000.0) : 0,
            s.Best?.PrefillTokS ?? 0,
            Current: false,
            Gap: true,
            Config: context));
        TrimClosed(book);
        gappedForKey = key;
        return true;
    }

    /// <summary>The model a running server has loaded right now, from the server's own model table.</summary>
    internal static string LoadedModelOf(ServerStatus s)
    {
        var loaded = s.Models.FirstOrDefault(m => m.Status.Trim().Equals("loaded", StringComparison.OrdinalIgnoreCase));
        return loaded is not null ? loaded.Id : s.ModelName;
    }

    /// <summary>Which model was loaded when a server-stamped time passed: the timeline's own memory.</summary>
    private string? ModelAt(double serverSeconds, string fallback)
    {
        var list = timeline;
        if (serverSeconds <= 0 || list.Count == 0)
            return fallback;
        var when = DateTime.UnixEpoch.AddSeconds(serverSeconds);
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var (at, model) = list[i];
            if (at <= when && model.Length > 0)
                return model;
        }
        return fallback;
    }

    private void TrimTimeline()
    {
        var horizon = DateTime.UtcNow.AddMinutes(-30);
        while (timeline.Count > 0 && timeline[0].At < horizon)
            timeline.RemoveAt(0);
    }

    private ModelBook BookFor(string model)
    {
        if (books.TryGetValue(model, out var book))
            return book;
        book = new ModelBook { Model = model };
        books.Add(model, book);
        if (books.Count > 200)
        {
            var dead = books.Values.OrderBy(b => b.Closed.Count).First();
            books.Remove(dead.Model);
        }
        return book;
    }

    private static void CloseLocked(ModelBook book)
    {
        if (book.Open is not { } acc)
            return;
        book.Open = null;
        book.Closed.Add(SessionOf(acc));
        TrimClosed(book);
    }

    private static ModelSession SessionOf(Acc acc) => new(
        acc.Since,
        acc.Requests,
        acc.DecodeMs > 0 ? acc.Written / (acc.DecodeMs / 1000.0) : 0,
        acc.MaxDecode,
        acc.PrefillMs > 0 ? acc.PrefillNew / (acc.PrefillMs / 1000.0) : 0,
        acc.MaxPrefill,
        Current: false,
        Gap: false,
        Config: acc.Context);

    private static void TrimClosed(ModelBook book)
    {
        while (book.Closed.Count > 40)
            book.Closed.RemoveAt(0);
    }

    /// <summary>
    /// The session's own start: an engine that stamps it gives epoch seconds; a counter-less engine's
    /// session is the app's own millisecond of first contact, and the two never cross that boundary.
    /// </summary>
    private static DateTime SessionStartOf(long key)
        => key > 99_999_999_999 ? DateTime.UnixEpoch.AddMilliseconds(key) : DateTime.UnixEpoch.AddSeconds(key);

    // ---- what the screen asks --------------------------------------------------------------------------------
    /// <summary>What the model is doing in the session under way; null while it has nothing to show.</summary>
    public ModelSession? Current(string model)
    {
        lock (gate)
        {
            return books.TryGetValue(model, out var book) && book.Open is { } acc
                ? Snap(acc, current: true)
                : null;
        }
    }

    /// <summary>The model's whole memory: the live session first, then the closed ones, newest first.</summary>
    public IReadOnlyList<ModelSession> Sessions(string model)
    {
        lock (gate)
        {
            if (!books.TryGetValue(model, out var book))
                return [];
            var list = new List<ModelSession>(book.Closed.Count + 1);
            if (book.Open is { } acc)
                list.Add(Snap(acc, current: true));
            for (var i = book.Closed.Count - 1; i >= 0; i--)
                list.Add(book.Closed[i]);
            return list;
        }
    }

    /// <summary>Every model key the recorder holds, so a UI row can find its history even when the engine
    /// names the model by path and the card names it by file stem.</summary>
    public IReadOnlyList<string> ModelNames()
    {
        lock (gate)
            return [.. books.Keys];
    }

    public bool HasAnything(string model)
    {
        lock (gate)
            return books.TryGetValue(model, out var b) && (b.Open is not null || b.Closed.Count > 0);
    }

    private static ModelSession Snap(Acc acc, bool current) => new(
        acc.Since,
        acc.Requests,
        acc.DecodeMs > 0 ? acc.Written / (acc.DecodeMs / 1000.0) : 0,
        acc.MaxDecode,
        acc.PrefillMs > 0 ? acc.PrefillNew / (acc.PrefillMs / 1000.0) : 0,
        acc.MaxPrefill,
        current,
        false,
        acc.Context);

    /// <summary>
    /// The app is going away: whatever session is open is now history, and the file is written before the
    /// answer, because the process will not be around to finish a deferred one.
    /// </summary>
    public void CloseSessions()
    {
        lock (gate)
        {
            foreach (var book in books.Values)
                CloseLocked(book);
        }
        SaveNow();
    }

    // ---- the file: what survives the app and hands the next run the open session --------------------------------
    private void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ThroughputFile))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(AppPaths.ThroughputFile));
            var root = doc.RootElement;
            foreach (var m in root.Get("models").Arr())
            {
                var model = m.Get("model").S();
                if (model.Length == 0)
                    continue;
                var book = BookFor(model);
                foreach (var e in m.Get("sessions").Arr())
                {
                    if (e.Get("open").B())
                    {
                        book.Open = new Acc
                        {
                            Key = e.Get("key").L(),
                            Since = MillisToUtc(e.Get("since").L()),
                            Requests = (int)e.Get("requests").L(),
                            Written = e.Get("written").L(),
                            DecodeMs = e.Get("decode_ms").D(),
                            PrefillNew = e.Get("prefill_new").L(),
                            PrefillMs = e.Get("prefill_ms").D(),
                            MaxDecode = e.Get("max_decode").D(),
                            MaxPrefill = e.Get("max_prefill").D(),
                            Context = e.Get("config").S(),
                        };
                    }
                    else
                    {
                        book.Closed.Add(new ModelSession(
                            MillisToUtc(e.Get("since").L()),
                            (int)e.Get("requests").L(),
                            e.Get("avg_decode").D(),
                            e.Get("max_decode").D(),
                            e.Get("avg_prefill").D(),
                            e.Get("max_prefill").D(),
                            Current: false,
                            Gap: e.Get("gap").B(),
                            Config: e.Get("config").S()));
                    }
                }
            }
        }
        catch (Exception e)
        {
            // A file the app itself wrote should never be broken; if one is, the history starts again
            // rather than the app starting to apologise in a dialog.
            AppPaths.Log("model-throughput: " + e.Message);
        }
    }

    private void SaveNow()
    {
        JsonNode snapshot;
        lock (gate)
        {
            var models = new JsonArray();
            foreach (var book in books.Values)
            {
                var sessions = new JsonArray();
                foreach (var closed in book.Closed)
                    sessions.Add(SerializeRow(closed, open: false));
                if (book.Open is { } acc)
                    sessions.Add(SerializeOpen(book.Model, acc));
                if (sessions.Count == 0)
                    continue;
                models.Add(new JsonObject { ["model"] = book.Model, ["sessions"] = sessions });
            }
            snapshot = new JsonObject { ["version"] = 1, ["models"] = models };
        }

        try
        {
            AppPaths.Ensure();
            var tmp = AppPaths.ThroughputFile + ".tmp";
            File.WriteAllText(tmp, snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, AppPaths.ThroughputFile, overwrite: true);
            lastSaveAttempt = DateTime.Now;
            dirty = false;
        }
        catch (Exception e)
        {
            AppPaths.Log("model-throughput save: " + e.Message);
            lastSaveAttempt = DateTime.Now.AddSeconds(-2);   // try again on the next beat
        }
    }

    private static JsonObject SerializeRow(ModelSession s, bool open) => new()
    {
        ["key"] = 0,
        ["since"] = (long)(s.Since - DateTime.UnixEpoch).TotalMilliseconds,
        ["requests"] = s.Requests,
        ["avg_decode"] = s.AvgDecodeTokS,
        ["max_decode"] = s.MaxDecodeTokS,
        ["avg_prefill"] = s.AvgPrefillTokS,
        ["max_prefill"] = s.MaxPrefillTokS,
        ["open"] = open,
        ["gap"] = s.Gap,
        ["config"] = s.Config,
    };

    /// <summary>
    /// The open session goes to the file with its running counts, not its averages: the next run must be
    /// able to keep counting, and an average of the two halves cannot be joined back into a sum.
    /// </summary>
    private static JsonObject SerializeOpen(string model, Acc acc)
    {
        var row = SerializeRow(SessionOf(acc), open: true);
        row["key"] = acc.Key;
        row["written"] = acc.Written;
        row["decode_ms"] = acc.DecodeMs;
        row["prefill_new"] = acc.PrefillNew;
        row["prefill_ms"] = acc.PrefillMs;
        return row;
    }

    private static long MillisSinceUnix() => (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
    private static DateTime MillisToUtc(long ms) => DateTime.UnixEpoch.AddMilliseconds(ms);
}
