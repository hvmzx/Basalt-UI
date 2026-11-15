using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace BasaltUI.Core;

/// <summary>
/// Getting a newer basalt onto the machine: fetch the release zip with the same download engine the model
/// browser uses, look inside it, and only then let files near your installation change.
///
/// Nothing is deleted. The previous <c>basalt.exe</c> is kept next to where it was, as
/// <c>basalt.exe.previous</c>, and the zip stays in the updates folder — so if a release ever misbehaves,
/// renaming that file back is the whole rollback.
/// </summary>
public static class UpdateInstaller
{
    /// <summary>Fetch the windows zip of a release. Null on success, the reason otherwise.</summary>
    public static async Task<string?> DownloadAsync(BasaltRelease release, AppSettings settings,
                                                    IProgress<DownloadProgress>? progress, Action<string>? trace,
                                                    CancellationToken ct)
    {
        var asset = release.WindowsAsset;
        if (asset is null || asset.Url.Length == 0)
            return "this release does not carry a windows zip to download";

        var job = new DownloadJob(new Uri(asset.Url), Path.Combine(AppPaths.UpdateDir, asset.Name),
                                  asset.Sha256, asset.Size, asset.Name);
        return await SegmentedDownload.RunAsync(job, settings.DownloadConnections, progress, trace, ct).ConfigureAwait(false);
    }

    public static string LocalZipFor(BasaltRelease release) => Path.Combine(AppPaths.UpdateDir, release.WindowsAsset?.Name ?? "");

    /// <summary>
    /// Lay the release over the installation folder. Call this only with the server stopped: the engine
    /// holds its own files open, and a half-replaced install is worse than an old one.
    /// </summary>
    public static async Task<string?> ApplyAsync(BasaltRelease release, string installRoot, Action<string>? trace,
                                                 CancellationToken ct)
    {
        var zip = LocalZipFor(release);
        if (!File.Exists(zip))
            return "the download is not here any more";
        if (!Directory.Exists(installRoot))
            return "the basalt folder is not where the app thinks it is: " + installRoot;

        var stage = Path.Combine(AppPaths.UpdateDir, "staged");
        try
        {
            trace?.Invoke("unpacking " + Path.GetFileName(zip));
            await Task.Run(() => Unpack(zip, stage), ct).ConfigureAwait(false);

            var exe = FindExe(stage);
            if (exe is null)
            return "the zip does not contain a basalt.exe to run, so nothing was changed";

            var (payload, target, exeRelative) = Map(exe, installRoot);
            if (payload is null || target is null)
                return "the zip does not look like a basalt install, so nothing was changed";

            trace?.Invoke($"{payload} → {target}");
            var error = await Task.Run(() => Swap(payload, target, exeRelative, trace), ct).ConfigureAwait(false);
            if (error is null)
                trace?.Invoke($"basalt {release.Tag.TrimStart('v')} is installed — start the server to run it");
            return error;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception e)
        {
            trace?.Invoke("update: " + e.Message);
            return Net.Explain(e);
        }
    }

    // ---- inside the zip ---------------------------------------------------------------------------------------
    private static void Unpack(string zip, string stage)
    {
        if (Directory.Exists(stage))
            Directory.Delete(stage, true);
        Directory.CreateDirectory(stage);
        ZipFile.ExtractToDirectory(zip, stage, true);
    }

    private static string? FindExe(string stage)
        => Directory.EnumerateFiles(stage, "basalt.exe", SearchOption.AllDirectories)
                    .OrderByDescending(f => string.Equals(Path.GetFileName(Path.GetDirectoryName(f)), "bin", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

    /// <summary>
    /// Where the new files start inside the zip, where they belong on this machine, and where the
    /// launcher ends up relative to the target. A release that ships a <c>bin</c> folder is meant to land
    /// on the basalt folder itself; one that ships a bare <c>basalt.exe</c> is meant to land in <c>bin</c>.
    /// </summary>
    private static (string? Payload, string? Target, string ExeRelative) Map(string extractedExe, string installRoot)
    {
        var inside = Path.GetDirectoryName(extractedExe) ?? "";
        var bin = Path.Combine(installRoot, "bin");
        var targetBin = Directory.Exists(bin) ? bin : installRoot;

        if (string.Equals(Path.GetFileName(inside), "bin", StringComparison.OrdinalIgnoreCase))
        {
            // When the exe the app knows about lives in a folder called bin — the usual layout, where
            // BasaltExePath points at the exe itself — that folder is what the release's bin maps onto,
            // so the release root goes one level above it, not onto bin again.
            var root = IsBinLeaf(installRoot) ? Path.GetDirectoryName(installRoot) : installRoot;
            return (Path.GetDirectoryName(inside), root ?? installRoot, Path.Combine("bin", "basalt.exe"));
        }

        return (inside, targetBin, "basalt.exe");
    }

    private static bool IsBinLeaf(string path)
        => string.Equals(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                         "bin", StringComparison.OrdinalIgnoreCase);

    // ---- the part that changes something -----------------------------------------------------------------------
    private static string? Swap(string payload, string target, string exeRelative, Action<string>? trace)
    {
        // Where the launcher belongs follows from the mapping, not from what happens to exist: the exe
        // path is known before any file moves, so the check afterwards is about the copy, not a guess.
        var exe = Path.Combine(target, exeRelative);

        // The same engine under another name: leave it exactly where it is. Renaming it aside would keep a
        // copy of itself as the rollback and rewrite a hundred-odd megabytes for nothing.
        var already = SameFile(exe, Path.Combine(payload, exeRelative));

        string? previous = null;
        if (File.Exists(exe) && !already)
        {
            previous = exe + ".previous";
            try
            {
                if (File.Exists(previous))
                    File.Delete(previous);
                File.Move(exe, previous);
            }
            catch (Exception e)
            {
                return "basalt.exe is still in use, so the update stopped before changing anything: " + e.Message;
            }
        }

        try
        {
            var files = CopyTree(payload, target, trace);
            var kept = previous is not null ? "; the previous exe is kept as basalt.exe.previous" : "";
            trace?.Invoke($"{files} file{(files == 1 ? "" : "s")} laid in place" +
                          (already ? "; the engine already here is the one this release carries" : kept));

            // the launcher is the thing that has to be executable — if it is not there, undo what we did
            if (!File.Exists(exe))
            {
                if (previous is not null)
                    File.Move(previous, exe, true);
                return "the update did not produce a basalt.exe — the previous one has been put back";
            }

            return null;
        }
        catch (Exception e)
        {
            try
            {
                if (previous is not null)
                    File.Move(previous, exe, true);
                trace?.Invoke("put the previous basalt.exe back");
            }
            catch (Exception back)
            {
                trace?.Invoke("and the previous exe could not be put back: " + back.Message);
                return $"{e.Message} — and basalt.exe.previous holds the file that used to run; rename it back";
            }

            return "the update stopped halfway: " + e.Message;
        }
    }

    private static int CopyTree(string from, string to, Action<string>? trace)
    {
        Directory.CreateDirectory(to);
        var count = 0;

        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));

        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".previous", StringComparison.OrdinalIgnoreCase))
                continue;

            var put = Path.Combine(to, Path.GetRelativePath(from, file));
            var info = new FileInfo(file);
            if (File.Exists(put))
            {
                var existing = new FileInfo(put);
                if (existing.Length == info.Length && existing.LastWriteTimeUtc == info.LastWriteTimeUtc)
                    continue;
            }

            File.Copy(file, put, true);
            File.SetLastWriteTimeUtc(put, info.LastWriteTimeUtc);
            count++;
        }

        return count;
    }

    /// The same test CopyTree uses to decide a file is already where it belongs: a release carries the
    /// timestamp of every entry, so matching length and timestamp means the file has not been changed since.
    private static bool SameFile(string here, string fromTheRelease)
    {
        if (!File.Exists(here) || !File.Exists(fromTheRelease))
            return false;

        var a = new FileInfo(here);
        var b = new FileInfo(fromTheRelease);
        return a.Length == b.Length && a.LastWriteTimeUtc == b.LastWriteTimeUtc;
    }
}
