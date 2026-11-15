using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BasaltUI.Core;

/// <summary>A model repo on Hugging Face, as the search answers it.</summary>
public sealed record HfRepo(string Id, long Downloads, int Likes, DateTime? Updated, bool Private,
                            IReadOnlyList<string> Tags)
{
    public string Owner => Id.Part(0, '/');
    public string Name => Id.Part(1, '/');
    public string NameFirst => Name.Length == 0 ? Id : Name;

    public string UpdatedText => Updated is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd") : "";

    /// <summary>The tags that say what the file is made of, without listing every label there is.</summary>
    public string FactsText
    {
        get
        {
            var keep = Tags.Where(t => t.Length > 0 && !Boring.Contains(t) && !t.Contains(':'))
                           .Take(4);
            return string.Join("  ", keep);
        }
    }

    private static readonly HashSet<string> Boring = new(StringComparer.OrdinalIgnoreCase)
    {
        "basalt", "model", "transformers", "base_model", "conversational", "text-generation", "not-for-production",
    };
}

/// <summary>One file inside a repo, with the size Hugging Face gives for it.</summary>
public sealed record HfFile(string Path, long Size, string Sha256)
{
    public string Name => Path.Contains('/') ? Path[(Path.LastIndexOf('/') + 1)..] : Path;
    public string Folder => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : "";

    /// <summary>The format basalt reads directly; a gguf shard sits next to it, so both are useful to see.</summary>
    public bool IsBasalt => Name.EndsWith(".basalt", StringComparison.OrdinalIgnoreCase);

    public bool IsGguf => Name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase);
    public bool IsLoadable => IsBasalt || IsGguf;

    /// <summary>The part between the name and the extension, which is where the quant usually lives.</summary>
    public string Detail => Name.Contains('.') ? Name[..Name.LastIndexOf('.')] : Name;

    public string SizeText => Size > 0 ? BasaltLocator.HumanBytes(Size) : "–";
}

/// <summary>
/// Reading Hugging Face: three calls, no dependencies. The list carries no sizes, so a repo has to be
/// opened to learn what is inside it — hence the shape of the browser window.
///
/// The base address is settings, not a constant: a mirror that is closer to the machine is usually the
/// single biggest thing a download speed can gain, and some networks cannot reach the canonical one at all.
/// </summary>
public static class HuggingFace
{
    public const string Canonical = "https://huggingface.co";

    /// <summary>The two stamps a repo carries; the newer of the two is the one worth showing.</summary>
    private static readonly string[] StampNames = ["lastModified", "createdAt"];

    /// <summary>A repo that needs an account sends the token people already keep for their own tooling.</summary>
    public static string? Token
        => NonEmpty(Environment.GetEnvironmentVariable("HF_TOKEN")) ?? NonEmpty(Environment.GetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN"));

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static string Base(string? mirror)
    {
        var text = NonEmpty(mirror);
        if (text is null)
            return Canonical;
        text = text.TrimEnd('/');
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;
        return text.TrimEnd('/');
    }

    // ---- search -------------------------------------------------------------------------------------------------
    public static async Task<IReadOnlyList<HfRepo>> SearchAsync(string? mirror, string query, int limit, CancellationToken ct)
    {
        var url = $"{Base(mirror)}/api/models?search={Uri.EscapeDataString(query.Or("basalt"))}" +
                  $"&sort=downloads&direction=-1&full=true&limit={Math.Clamp(limit, 1, 100)}";

        var rows = new List<HfRepo>();
        foreach (var item in (await GetAsync(url, ct).ConfigureAwait(false)).Arr())
        {
            var id = item.Get("id").S();
            if (id.Length == 0)
                continue;

            var stamps = StampNames.Select(n => item.Get(n).SR())
                                                              .Select(s => s is not null && DateTime.TryParse(s, out var dt) ? dt : (DateTime?)null);
            rows.Add(new HfRepo(
                Id: id,
                Downloads: item.Get("downloads").L(),
                Likes: item.Get("likes").I(),
                Updated: stamps.FirstOrDefault(d => d is not null),
                Private: item.Get("private").B(),
                Tags: item.Get("tags").Arr().Select(e => e.S()).Where(t => t.Length > 0).ToArray()));
        }

        return rows;
    }

    /// <summary>
    /// Everything in one repo. The file names and sizes only exist here, and a sha256 comes with each LFS
    /// file, which is what lets a big download be checked instead of trusted.
    /// </summary>
    public static async Task<IReadOnlyList<HfFile>> FilesAsync(string? mirror, string repoId, CancellationToken ct)
    {
        var url = $"{Base(mirror)}/api/models/{repoId.Split('/').Select(Uri.EscapeDataString).Join()}?blobs=true";

        var rows = new List<HfFile>();
        foreach (var file in (await GetAsync(url, ct).ConfigureAwait(false)).Path("siblings").Arr())
        {
            var path = file.Get("rfilename").S();
            if (path.Length == 0)
                continue;

            // An LFS file's "size" in the listing is the pointer; the real length and its sha256 are under "lfs".
            var sha = file.Path("lfs", "sha256").S().ToLowerInvariant();
            var size = file.Path("lfs", "size").L(-1);
            if (size <= 0)
                size = file.Get("size").L(-1);

            rows.Add(new HfFile(path, size, sha.Length == 64 && sha.All(Uri.IsHexDigit) ? sha : ""));
        }

        return rows.OrderByDescending(f => f.IsLoadable).ThenByDescending(f => f.Size).ToList();
    }

    public static Uri DownloadUrl(string? mirror, string repoId, string path)
        => new($"{Base(mirror)}/{repoId.Split('/').Select(Uri.EscapeDataString).Join()}/resolve/main/{path.Split('/').Select(Uri.EscapeDataString).Join()}");

    /// <summary>
    /// <c>?blobs=true</c> is what makes the file list carry sizes and sha256s; without it the listing only
    /// gives names. It is also the cheapest way to tell a gated repo from a missing one.
    /// </summary>
    private static async Task<JsonElement> GetAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        ApplyAuth(request);

        var response = await Net.Json.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var detail = "";
                try
                {
                    detail = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    // the status is the part worth repeating
                }

                var note = code switch
                {
                    401 or 403 => "this repo needs an account, or its terms have not been accepted" +
                                  (Token is null ? " — a token in the HF_TOKEN environment variable is used automatically" : ""),
                    404 => "Hugging Face does not know this address",
                    429 => "Hugging Face is asking the app to wait a moment",
                    >= 500 => "Hugging Face is having trouble; try again shortly",
                    _ => "",
                };
                var shortDetail = detail.Length == 0 ? "" : " — " + detail[..Math.Min(200, detail.Length)];
                throw new HttpRequestException($"{code} from {new Uri(url).AbsolutePath}{(note.Length == 0 ? shortDetail : " " + note)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.Clone();
        }
    }

    /// <summary>
    /// Also used by the downloader: Hugging Face answers a range request with a signed address on its own
    /// CDN, and that one wants the same token the API call carried.
    /// </summary>
    public static void ApplyAuth(HttpRequestMessage request)
    {
        if (Token is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}

/// <summary>Small text helpers, so the calls above read as prose.</summary>
internal static class HfText
{
    /// <summary>The piece of “owner/name” the caller wants.</summary>
    public static string Part(this string text, int index, char separator)
    {
        var bits = text.Split(separator);
        return index < bits.Length ? bits[index] : "";
    }

    public static string Join(this IEnumerable<string> parts) => string.Join('/', parts);

    /// <summary>A search box is allowed to be empty; “basalt” is the only sensible thing to look for.</summary>
    public static string Or(this string text, string fallback)
        => string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
    
}
