using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BasaltUI.Core;

/// <summary>
/// Outbound web calls live here, on one of two clients, so a slow or broken mirror never has to be
/// reasoned about twice. The local basalt server is never touched by this file — it has its own client
/// in <see cref="BasaltClient"/> with a very short timeout.
/// </summary>
public static class Net
{
    /// <summary>A name the endpoints can log. Hugging Face answers better to a real one.</summary>
    public const string UserAgent = "BasaltUI/0.1 (basalt windows manager)";

    private static readonly object Gate = new();
    private static string? appliedProxy;
    private static HttpClient json = Build(true, 4, TimeSpan.FromSeconds(30), null);
    private static HttpClient streams = Build(false, 32, Timeout.InfiniteTimeSpan, null);

    /// <summary>
    /// The proxy to use for the outside internet, from the Settings → Models field. Empty means whatever
    /// Windows itself is configured with, which is usually right; some networks need the address written out.
    /// </summary>
    public static void Configure(string? proxyUrl)
    {
        var want = string.IsNullOrWhiteSpace(proxyUrl) ? null : proxyUrl.Trim();
        lock (Gate)
        {
            if (json is not null && string.Equals(appliedProxy, want, StringComparison.OrdinalIgnoreCase))
                return;
            appliedProxy = want;

            // Deliberately not disposing the old clients: a download in flight keeps its client alive and
            // finishes on the proxy it started with, and both clients are rebuilt on the next app start.
            json = Build(true, 4, TimeSpan.FromSeconds(30), want);
            streams = Build(false, 32, Timeout.InfiniteTimeSpan, want);
        }
    }

    /// <summary>Short calls that return json: the GitHub release query and the Hugging Face search.</summary>
    public static HttpClient Json
    {
        get { lock (Gate) { return json; } }
    }

    /// <summary>
    /// Big file calls. No automatic decompression, because a byte range of a model has to come back byte
    /// for byte, and no overall timeout, because a 70 GiB download is allowed to take the hour it needs.
    /// Many connections per server, which is the whole trick behind a fast download.
    /// </summary>
    public static HttpClient Streams
    {
        get { lock (Gate) { return streams; } }
    }

    private static HttpClient Build(bool decompress, int perServer, TimeSpan timeout, string? proxy)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = perServer,
            AutomaticDecompression = decompress ? DecompressionMethods.GZip | DecompressionMethods.Deflate : DecompressionMethods.None,
        };
        if (proxy is not null)
            handler.Proxy = new WebProxy(proxy);

        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    // ---- the two shapes we ask for -------------------------------------------------------------------------
    public static async Task<JsonNode> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await GetAsync(url, ct).ConfigureAwait(false);
        await using var text = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(text, cancellationToken: ct).ConfigureAwait(false);
        return JsonNode.Parse(doc.RootElement.GetRawText()) ?? throw new InvalidDataException(url + " returned nothing");
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var response = await GetAsync(url, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.AcceptEncoding.ParseAdd("gzip, deflate");
        request.Headers.Accept.ParseAdd("application/json, */*");

        var response = await Json.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = "";
            try
            {
                detail = " " + await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // the status code is the part that matters
            }

            var status = (int)response.StatusCode;
            var reason = response.ReasonPhrase;
            response.Dispose();
            throw new HttpRequestException($"{status} {reason} for {url}{Trim(detail)}");
        }

        return response;
    }

    private static string Trim(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length == 0 ? "" : s.Length <= 300 ? " — " + s : " — " + s[..300] + "…";
    }

    /// <summary>The message we show when web traffic fails: usually not a bug, but an offline machine.</summary>
    public static string Explain(Exception e) => e switch
    {
        // A cancel the caller asked for says so on its token; HttpClient's own timeout arrives as a cancellation
        // nobody asked for, and calling that "cancelled" sends the user looking for a button they never pressed.
        OperationCanceledException oe when oe.CancellationToken.IsCancellationRequested => "cancelled",
        OperationCanceledException => "the web did not answer in time",
        HttpRequestException or IOException or UnauthorizedAccessException =>
            e.Message + " — this looks like a network or proxy problem; Settings → Basalt has a field for a proxy and one for a closer Hugging Face mirror",
        _ => e.Message,
    };
}
