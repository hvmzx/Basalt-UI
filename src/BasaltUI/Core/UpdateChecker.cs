using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BasaltUI.Core;

public sealed record UpdateAsset(string Name, long Size, string Url, string Sha256)
{
    public string SizeText => BasaltLocator.HumanBytes(Size);
}

/// <summary>One GitHub release of basalt, as the releases API describes it.</summary>
public sealed record BasaltRelease(string Tag, string Name, string HtmlUrl, string Notes, string PublishedAt,
                                   IReadOnlyList<UpdateAsset> Assets)
{
    public Version? Version => UpdateChecker.ParseVersion(Tag);

    /// <summary>The zip that belongs on this machine.</summary>
    public UpdateAsset? WindowsAsset
        => Assets.FirstOrDefault(a => a.Name.Contains("windows", StringComparison.OrdinalIgnoreCase)
                                      && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
           ?? Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

    /// <summary>The release notes, cut at the first blank line run so a banner stays a banner.</summary>
    public string ShortNotes
    {
        get
        {
            var lines = Notes.Replace("\r", "").Split('\n')
                             .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("##"))
                             .Take(4);
            return string.Join(" ", lines);
        }
    }
}

/// <summary>
/// “Is my basalt behind?” The installed number comes from the server that answers on the port
/// (<c>/props</c> → <c>engine.version</c>, then <c>build_info</c>) and when nothing is answering, from the exe —
/// its own version resource if it has one, otherwise from what it announced the last time it ran. The newest
/// number comes from the GitHub releases API.
/// </summary>
public static class UpdateChecker
{
    public const string Repo = "jesdga95/basalt";
    public const string ReleasePage = "https://github.com/" + Repo + "/releases";

    private const string ApiPath = "https://api.github.com/repos/" + Repo + "/releases/latest";

    /// <summary>
    /// GitHub first, then a public mirror in front of the same call. One of the two usually answers even
    /// when github.com itself is far away or blocked from the network the machine is on.
    /// </summary>
    public static readonly (string Name, string Url)[] Sources =
    [
        ("github.com", ApiPath),
        ("gh-proxy.com", "https://gh-proxy.com/" + ApiPath),
    ];

    private static readonly Regex Number = new(@"\d+(\.\d+){1,3}", RegexOptions.Compiled);

    /// <summary>The newest published release, from whichever source answers first.</summary>
    public static async Task<BasaltRelease> LatestAsync(CancellationToken ct)
    {
        var attempts = new List<string>();
        foreach (var (name, url) in Sources)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = await Net.GetJsonAsync(url, ct).ConfigureAwait(false);
                if (root is not JsonObject o)
                    throw new InvalidDataException("the releases API did not return an object");
                var release = Parse(o);
                if (release.Tag.Length == 0)
                    throw new InvalidDataException(name + " answered without a tag");
                if (name != "github.com")
                    AppPaths.Log("the update check came from " + name);
                return release;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                attempts.Add(name + ": " + e.Message);
            }
        }

        throw new IOException("no answer about newer releases — " + string.Join(" | ", attempts));
    }

    /// <summary>Every published release, newest first — the changelog between two versions is the notes
    /// of the releases in between, so the popup can show the whole story rather than just the last one.</summary>
    public static async Task<IReadOnlyList<BasaltRelease>> ReleasesAsync(CancellationToken ct)
    {
        var path = "https://api.github.com/repos/" + Repo + "/releases?per_page=30";
        var attempts = new List<string>();
        foreach (var mirror in new[] { "", "https://gh-proxy.com/" })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = await Net.GetJsonAsync(mirror + path, ct).ConfigureAwait(false);
                if (root is not JsonArray list)
                    throw new InvalidDataException("the releases API did not return a list");
                return [.. list.OfType<JsonObject>().Select(Parse).Where(r => r.Tag.Length > 0)];
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                attempts.Add((mirror.Length == 0 ? "github.com" : "gh-proxy.com") + ": " + e.Message);
            }
        }
        throw new IOException("no answer about releases — " + string.Join(" | ", attempts));
    }

    internal static BasaltRelease Parse(JsonObject o)
    {
        var assets = new List<UpdateAsset>();
        foreach (var item in o["assets"]?.AsArray() ?? [])
        {
            if (item is not JsonObject a)
                continue;
            assets.Add(new UpdateAsset(
                Name: Str(a, "name"),
                Size: a["size"] is JsonValue sz && sz.TryGetValue(out long bytes) ? bytes : 0,
                Url: Str(a, "browser_download_url"),
                Sha256: Digest(a["digest"])));
        }

        return new BasaltRelease(
            Tag: Str(o, "tag_name"),
            Name: Str(o, "name"),
            HtmlUrl: Str(o, "html_url"),
            Notes: Str(o, "body"),
            PublishedAt: Str(o, "published_at"),
            Assets: assets);
    }

    private static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out string? s) ? s : "";

    /// <summary>GitHub now carries an asset digest as <c>"sha256:…"</c>; older releases carry nothing.</summary>
    private static string Digest(JsonNode? node)
    {
        if (node is not JsonValue v || !v.TryGetValue(out string? text))
            return "";
        var cut = text.IndexOf(':');
        var hex = (cut < 0 ? text : text[(cut + 1)..]).Trim();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : "";
    }

    // ---- the installed side ---------------------------------------------------------------------------------
    /// <summary>Any of “0.3.3”, “v0.3.3”, “basalt 0.3.3”, “0.3.3+abc” → a Version; null when there is no number in it.</summary>
    public static Version? ParseVersion(string? text)
    {
        var m = string.IsNullOrWhiteSpace(text) ? null : Number.Match(text);
        return m is null ? null : Version.TryParse(m.Value, out var v) ? v : null;
    }

    /// <summary>
    /// What the engine on the port says it is, or what the exe file says, or what the engine said about this
    /// exact copy of the exe the last time it ran. basalt.exe carries no version resource, so that memory is
    /// what lets the app talk about versions while the server is stopped — which is most of the time.
    /// </summary>
    public static string InstalledVersionText(ServerStatus? status, string exePath, string memo = "", string memoKey = "")
    {
        if (status is { Reachable: true })
        {
            if (ParseVersion(status.EngineVersion) is not null)
                return status.EngineVersion.Trim();
            if (ParseVersion(status.BuildInfo) is { } fromBuild)
                return fromBuild.ToString();
        }

        var fromFile = ExeVersionText(exePath);
        if (ParseVersion(fromFile) is { } v)
            return v.ToString();

        // Nothing readable in the file: the remembered number counts only while it describes this very copy.
        if (memoKey.Length > 0 && memoKey == ExeVersionKey(exePath) && ParseVersion(memo) is { } remembered)
            return remembered.ToString();
        return "";
    }

    /// <summary>Which copy of the exe a remembered version number would be about.</summary>
    public static string ExeVersionKey(string exePath)
    {
        string stamp;
        try
        {
            stamp = File.Exists(exePath) ? File.GetLastWriteTimeUtc(exePath).Ticks.ToString() : "missing";
        }
        catch (Exception e)
        {
            AppPaths.Log("exe stamp: " + e.Message);
            stamp = "unreadable";
        }

        return exePath + "|" + stamp;
    }

    private static string ExeVersionText(string exePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                return "";
            var info = FileVersionInfo.GetVersionInfo(exePath);
            return info.FileVersion ?? info.ProductVersion ?? "";
        }
        catch (Exception e)
        {
            AppPaths.Log("exe version: " + e.Message);
            return "";
        }
    }

    /// <summary>1 when the release is newer than what is installed, 0 the same, -1 older, int.MinValue when we cannot tell.</summary>
    public static int Compare(string? installed, BasaltRelease? latest)
    {
        var have = Pad(ParseVersion(installed));
        var want = Pad(latest?.Version);
        if (have is null || want is null)
            return int.MinValue;
        return want.CompareTo(have);
    }

    /// <summary>“0.3.4” has to beat “0.3.4.0”, so both sides are compared as three-part numbers.</summary>
    private static Version? Pad(Version? v) => v is null ? null : new Version(v.Major, v.Minor, Math.Max(0, v.Build));
}
