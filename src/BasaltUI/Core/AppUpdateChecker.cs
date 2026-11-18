using System;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;

namespace BasaltUI.Core;

/// <summary>One published Basalt UI release, as the releases API describes it.</summary>
public sealed record AppRelease(string Tag, string HtmlUrl, string Notes, string PublishedAt,
                                IReadOnlyList<UpdateAsset> Assets)
{
    public Version? Version => UpdateChecker.ParseVersion(Tag);

    /// <summary>The installer is preferred: it keeps the Start menu entry and uninstall record in step.</summary>
    public UpdateAsset? SetupAsset => Assets.FirstOrDefault(a =>
        a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>Used when a release only published the bare single-file exe.</summary>
    public UpdateAsset? PortableAsset => Assets.FirstOrDefault(a =>
        a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
        !a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase));

    public UpdateAsset? Asset => SetupAsset ?? PortableAsset;

    public string AssetText => Asset is { } a ? $"{a.Name} - {a.SizeText}" : "this release carries no Windows exe";
}

/// <summary>
/// The app asking whether it is behind, using its own repository. The shape of the answer is the same one
/// <see cref="UpdateChecker"/> already reads for basalt: a tag, the release notes and a list of assets. Only
/// the repository and the preferred asset differ, so the parsing helpers are shared.
/// </summary>
public static class AppUpdateChecker
{
    public const string Repo = "hvmzx/Basalt-UI";
    public const string ReleasePage = "https://github.com/" + Repo + "/releases";

    private const string LatestPath = "https://api.github.com/repos/" + Repo + "/releases/latest";
    private const string AllPath = "https://api.github.com/repos/" + Repo + "/releases?per_page=30";

    public static readonly (string Name, string Url)[] Sources =
    [
        ("github.com", LatestPath),
        ("gh-proxy.com", "https://gh-proxy.com/" + LatestPath),
    ];

    /// <summary>The version stamped into this very exe by the build or the release tag.</summary>
    public static string OwnVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }

    public static async Task<AppRelease> LatestAsync(CancellationToken ct)
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
                    AppPaths.Log("the Basalt UI update check came from " + name);
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
        throw new IOException("no answer about newer Basalt UI releases — " + string.Join(" | ", attempts));
    }

    public static async Task<IReadOnlyList<AppRelease>> ReleasesAsync(CancellationToken ct)
    {
        var attempts = new List<string>();
        foreach (var mirror in new[] { "", "https://gh-proxy.com/" })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var root = await Net.GetJsonAsync(mirror + AllPath, ct).ConfigureAwait(false);
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
        throw new IOException("no answer about Basalt UI releases — " + string.Join(" | ", attempts));
    }

    internal static AppRelease Parse(JsonObject o)
    {
        var assets = new List<UpdateAsset>();
        foreach (var item in o["assets"]?.AsArray() ?? [])
        {
            if (item is not JsonObject a)
                continue;
            assets.Add(new UpdateAsset(
                Name: UpdateChecker.Str(a, "name"),
                Size: a["size"] is JsonValue sz && sz.TryGetValue(out long bytes) ? bytes : 0,
                Url: UpdateChecker.Str(a, "browser_download_url"),
                Sha256: UpdateChecker.Digest(a["digest"])));
        }
        return new AppRelease(
            Tag: UpdateChecker.Str(o, "tag_name"),
            HtmlUrl: UpdateChecker.Str(o, "html_url"),
            Notes: UpdateChecker.Str(o, "body"),
            PublishedAt: UpdateChecker.Str(o, "published_at"),
            Assets: assets);
    }

    /// <summary>1 when the tag is newer than this copy, 0 the same, -1 older, int.MinValue when we cannot tell.</summary>
    public static int Compare(string? installed, string? tag)
    {
        var have = Pad(UpdateChecker.ParseVersion(installed));
        var want = Pad(UpdateChecker.ParseVersion(tag));
        return have is null || want is null ? int.MinValue : want.CompareTo(have);
    }

    /// <summary>“0.2.0” has to beat “0.2.0.0”, so both sides are compared as three-part numbers.</summary>
    private static Version? Pad(Version? v) => v is null ? null : new Version(v.Major, v.Minor, Math.Max(0, v.Build));

    /// <summary>The notes of every release between this copy and the newest, newest first, ready for the
    /// changelog window's small markdown renderer.</summary>
    public static string Changelog(string current, IReadOnlyList<AppRelease> releases, string toTag)
    {
        var between = releases
            .Where(r => Compare(current, r.Tag) > 0)
            .OrderByDescending(r => r.Version)
            .ToList();
        var text = $"# Changes from v{current} to {toTag}\n\n";
        foreach (var r in between)
            text += $"## {r.Tag} — {(r.PublishedAt.Length >= 10 ? r.PublishedAt[..10] : r.PublishedAt)}\n\n" +
                    (string.IsNullOrWhiteSpace(r.Notes) ? "(no notes were written for this release)" : r.Notes) + "\n\n";
        if (between.Count == 0)
            text += "(no release notes were published between these versions)\n";
        return text;
    }
}
