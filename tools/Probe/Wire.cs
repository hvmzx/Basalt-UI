using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// A fake GitHub and a fake Hugging Face sharing one loopback port. The app's own classes talk to it over real
// sockets: real HTTP, real byte ranges, real redirects, real half-finished connections. Everything about the
// internet except the part this machine is not allowed to reach.
//
// It is deliberately dumb — the test hands it documents to serve and files to serve in ranges — so that every
// judgement it provokes belongs to the code under test.
internal sealed class Wire : IDisposable
{
    private const string Resolve = "/resolve/main/";

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private readonly Dictionary<string, byte[]> files;
    private readonly Dictionary<string, string> documents;

    private int active;
    private int peak;
    private int rangeBodies;
    private int authless;
    private int cutBudget;
    private long served;

    /// <summary>How many range bodies to write half of and then drop, so the app must survive a dead connection.</summary>
    public int CutNext
    {
        get => Volatile.Read(ref cutBudget);
        set => Volatile.Write(ref cutBudget, value);
    }

    public Wire(IEnumerable<(string path, byte[] bytes)> files, IEnumerable<(string path, string json)> documents)
    {
        this.files = files.ToDictionary(f => f.path, f => f.bytes, StringComparer.Ordinal);
        this.documents = documents.ToDictionary(d => d.path, d => d.json, StringComparer.Ordinal);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }
    public string Base => "http://127.0.0.1:" + Port;

    /// <summary>Every request line the fake answered, in order — what the app really asked for.</summary>
    public List<string> Seen { get; } = [];

    /// <summary>The Authorization header the fake was last offered, if any.</summary>
    public string? Auth { get; private set; }

    /// <summary>What the address it redirected to was offered — in the real world, nothing.</summary>
    public string? CdnAuth { get; private set; }

    public long ServedBytes => Interlocked.Read(ref served);

    /// <summary>Requests that arrived complete and did not carry a token.</summary>
    public int Authless => Volatile.Read(ref authless);

    /// <summary>The most range bodies the fake was writing at the same instant.</summary>
    public int PeakParallel => Volatile.Read(ref peak);

    public int RangeBodies => Volatile.Read(ref rangeBodies);

    public void Reset()
    {
        Interlocked.Exchange(ref served, 0);
        Volatile.Write(ref rangeBodies, 0);
        Volatile.Write(ref authless, 0);
        Volatile.Write(ref peak, 0);
        Volatile.Write(ref cutBudget, 0);
        lock (Seen)
            Seen.Clear();
    }

    /// <summary>The request lines mentioning one path fragment — enough of them to read what happened.</summary>
    public string Trace(string contains)
    {
        List<string> hits;
        lock (Seen)
            hits = Seen.FindAll(l => l.Contains(contains, StringComparison.OrdinalIgnoreCase));
        if (hits.Count == 0)
            return "nothing mentioning " + contains;
        var tail = hits.Count <= 6 ? hits : hits.GetRange(hits.Count - 6, 6);
        return (hits.Count > 6 ? $"…{hits.Count - 6} earlier · " : "") + string.Join(" · ", tail);
    }

    // ---- accepting ---------------------------------------------------------------------------------------------
    private async Task AcceptLoopAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                break;
            }

            client.NoDelay = true;            // a response goes out the moment it is ready, not when a buffer fills
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);

                var request = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(request))
                    return;

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var complete = false;
                while (true)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                        break;                      // the client hung up mid-request: its business, not a fault
                    if (line.Length == 0)
                    {
                        complete = true;
                        break;
                    }

                    var colon = line.IndexOf(':');
                    if (colon > 0)
                        headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }

                var parts = request.Split(' ');
                if (parts.Length < 2)
                    return;

                // A request line carries "/api/models?search=cat": the address is looked up without its query,
                // but the trouble-raising terms live in that query.
                var target = parts[1];
                var split = target.IndexOf('?');
                var query = split >= 0 ? target[split..] : "";
                var path = Uri.UnescapeDataString(split >= 0 ? target[..split] : target);
                if (!path.StartsWith('/'))
                    path = "/" + path;

                lock (Seen)
                    Seen.Add(request);

                // Which machine the app believes it is talking to, and what that machine was offered. The
                // redirect sends the app on to "localhost" as a stand-in for Hugging Face's CDN: somewhere
                // else, which should not be handed the token.
                var host = headers.TryGetValue("Host", out var where) ? where : "";
                var carried = headers.TryGetValue("Authorization", out var offered) ? offered : null;
                if (host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
                    CdnAuth = carried;
                else
                {
                    if (carried is not null)
                        Auth = carried;
                    else if (complete)
                        Interlocked.Increment(ref authless);
                }

                await AnswerAsync(stream, path, query, target, headers).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            // a client that hung up first is normal
        }
        catch (SocketException)
        {
            // as above
        }
        catch (ObjectDisposedException)
        {
            // the fake was shut down mid-answer
        }
    }

    // ---- answering -------------------------------------------------------------------------------------------
    private async Task AnswerAsync(Stream stream, string path, string query, string raw, Dictionary<string, string> headers)
    {
        // A search whose term asks for trouble answers with trouble, so the app's error sentences can be read.
        foreach (var trouble in new[] { 403, 404, 429, 500 })
            if (query.Contains("raise-" + trouble, StringComparison.OrdinalIgnoreCase))
            {
                await SendAsync(stream, trouble, "application/json", "{\"error\":\"the fake is refusing on purpose\"}").ConfigureAwait(false);
                return;
            }

        if (documents.TryGetValue(path, out var json))
        {
            // A document may name __BASE__ for an address it wants the app to come back to.
            await SendAsync(stream, 200, "application/json", json.Replace("__BASE__", Base)).ConfigureAwait(false);
            return;
        }

        // The address the app asked for is not where the bytes live, exactly as Hugging Face does it. It even
        // uses a different machine's name, so "localhost" stands in for its CDN: a redirect the app must follow
        // without carrying its token onto somebody else's server.
        var hop = raw.IndexOf(Resolve, StringComparison.OrdinalIgnoreCase);
        if (hop >= 0)
        {
            var tail = raw[(hop + Resolve.Length)..];          // still escaped, as it arrived
            await SendAsync(stream, 302, "text/plain", "",
                extra: $"Location: http://localhost:{Port}/cdn/{tail}\r\n").ConfigureAwait(false);
            return;
        }

        if (!files.TryGetValue(path, out var bytes))
        {
            await SendAsync(stream, 404, "application/json", "{\"error\":\"the fake has no such file\"}").ConfigureAwait(false);
            return;
        }

        await SendRangesAsync(stream, bytes, headers).ConfigureAwait(false);
    }

    private async Task SendRangesAsync(Stream stream, byte[] bytes, Dictionary<string, string> headers)
    {
        long total = bytes.LongLength;
        var partial = headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase);
        long from = 0;
        var upTo = total - 1;
        if (partial && range is not null)
        {
            var spec = range["bytes=".Length..].Split('-');
            if (!long.TryParse(spec[0].Trim(), out from))
                from = 0;
            if (spec.Length > 1 && long.TryParse(spec[1].Trim(), out var end))
                upTo = Math.Min(end, total - 1);
        }

        if (from >= total)
        {
            await SendAsync(stream, 416, "text/plain", "", extra: $"Content-Range: bytes */{total}\r\n").ConfigureAwait(false);
            return;
        }

        // The app's own first ask is one byte to learn the size; it is not a body, so it moves no counters and
        // is never a cut candidate.
        var big = partial && upTo - from > (64 << 10);
        var length = upTo - from + 1;
        var cut = false;
        if (big)
            for (var budget = Volatile.Read(ref cutBudget); budget > 0; budget = Volatile.Read(ref cutBudget))
                if (Interlocked.CompareExchange(ref cutBudget, budget - 1, budget) == budget)
                {
                    cut = true;
                    break;
                }

        // Exactly one Content-Length for the body. Two of them is a conversation nobody can have.
        var head =
            $"HTTP/1.1 {(partial ? 206 : 200)} {(partial ? "Partial Content" : "OK")}\r\n" +
            "Accept-Ranges: bytes\r\n" +
            (partial ? $"Content-Range: bytes {from}-{upTo}/{total}\r\n" : "") +
            "Content-Type: application/octet-stream\r\n" +
            $"Content-Length: {length}\r\nConnection: close\r\n\r\n";
        await WriteHeadAsync(stream, head).ConfigureAwait(false);

        var left = cut ? Math.Max(1L, length / 2) : length;
        if (big)
        {
            var live = Interlocked.Increment(ref active);
            for (var seen = Volatile.Read(ref peak); live > seen; seen = Volatile.Read(ref peak))
                if (Interlocked.CompareExchange(ref peak, live, seen) == seen)
                    break;
            Interlocked.Increment(ref rangeBodies);
        }

        try
        {
            var at = from;
            while (left > 0)
            {
                var take = (int)Math.Min(1 << 18, left);
                await stream.WriteAsync(bytes.AsMemory((int)at, take)).ConfigureAwait(false);
                at += take;
                left -= take;
                Interlocked.Add(ref served, take);
                await Task.Yield();           // give the other connections a turn on the wire
            }
        }
        finally
        {
            if (big)
                Interlocked.Decrement(ref active);
        }
    }

    private static async Task WriteHeadAsync(Stream stream, string head)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private static async Task SendAsync(Stream stream, int code, string type, string body, string extra = "")
    {
        var label = code switch
        {
            206 => "Partial Content",
            302 => "Found",
            403 => "Forbidden",
            404 => "Not Found",
            416 => "Range Not Satisfiable",
            429 => "Too Many Requests",
            >= 500 => "Internal Server Error",
            _ => "OK",
        };
        var head = $"HTTP/1.1 {code} {label}\r\nContent-Type: {type}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n{extra}Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head)).ConfigureAwait(false);
        if (body.Length > 0)
            await stream.WriteAsync(Encoding.UTF8.GetBytes(body)).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }
}
