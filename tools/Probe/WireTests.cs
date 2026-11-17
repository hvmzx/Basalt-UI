using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BasaltUI.Core;

// Everything the internet-facing code does, run against the fake in Wire.cs. It sits apart from the
// probe's other modes because a suite that threw would take the process down with it, and a Windows
// error-reporting husk holds its own exe until it is let go.
internal static class WireTests
{
    public static async Task<int> RunAsync()
    {
        // The internet-facing code against a fake GitHub and a fake Hugging Face on one loopback port: real HTTP,
        // real byte ranges, real redirects, real connections cut in half. The install root it writes into is a
        // folder under this program's own output — never a real basalt installation.
        Console.WriteLine("== wire (the app against a fake internet) ==");
        var fails = 0;
        void W(string what, bool good, string? detail = null)
        {
            if (!good) fails++;
            Console.WriteLine($"  {(good ? "ok  " : "FAIL")} {what}{(detail is null ? "" : ": " + detail)}");
        }

        static string Sha(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        var scratch = Path.Combine(AppContext.BaseDirectory, "wire");
        if (Directory.Exists(scratch))
            Directory.Delete(scratch, recursive: true);
        Directory.CreateDirectory(scratch);

        // A model nobody could mistake for an empty file, big enough that four connections have something to do.
        const long size = 160L << 20;
        var model = new byte[size];
        for (var i = 0; i < model.Length; i++)
            model[i] = (byte)((i * 7 + (i >> 20)) & 0xFF);
        var sha = Convert.ToHexString(SHA256.HashData(model)).ToLowerInvariant();

        var newExe = Encoding.UTF8.GetBytes("NEW ENGINE " + new string('x', 400));
        var oldExe = Encoding.UTF8.GetBytes("OLD ENGINE");
        var zipPath = Path.Combine(scratch, "basalt-v0.9.9-windows-x64.zip");
        using (var zipStream = File.Create(zipPath))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            void Entry(string name, byte[] content)
            {
                var entry = zip.CreateEntry("basalt-v0.9.9-windows-x64/" + name);
                entry.LastWriteTime = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
                using var writer = entry.Open();
                writer.Write(content);
            }

            Entry("bin/basalt.exe", newExe);
            Entry("bin/basalt-bench.exe", Encoding.UTF8.GetBytes("a second file in bin"));
            Entry("README.md", Encoding.UTF8.GetBytes("the new engine"));
        }

        var zipBytes = File.ReadAllBytes(zipPath);
        var zipSha = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();

        var listing = """
            {"id":"fair/model-one","siblings":[
              {"rfilename":"model.basalt","size":__SIZE__,"lfs":{"sha256":"__SHA__","size":__SIZE__}},
              {"rfilename":"sub dir/model file.gguf","size":__SIZE__,"lfs":{"sha256":"__SHA__","size":__SIZE__}},
              {"rfilename":"README.md","size":12}]}
            """
            .Replace("__SIZE__", size.ToString())
            .Replace("__SHA__", sha);

        // __BASE__ stands for "whatever address the fake turns out to be on"; the fake fills it in as it answers.
        var releaseJson = """
            {"tag_name":"v0.9.9","name":"basalt 0.9.9","html_url":"__BASE__/releases/v0.9.9",
             "body":"## what changed\r\n- answer-room is now on by default\r\n- faster expert loading",
             "published_at":"2026-11-10T00:00:00Z","assets":[
               {"name":"basalt-v0.9.9-windows-x64.zip","size":__SIZE__,"digest":"sha256:__ZIPSHA__",
                "browser_download_url":"__BASE__/dl/basalt-v0.9.9-windows-x64.zip"},
               {"name":"basalt-v0.9.9-linux-x64.tar.gz","size":1,"browser_download_url":"__BASE__/dl/linux.tar.gz"}]}
            """
            .Replace("__SIZE__", zipBytes.Length.ToString())
            .Replace("__ZIPSHA__", zipSha);

        Environment.SetEnvironmentVariable("HF_TOKEN", "hf_wire_token");
        using var wire = new Wire(
            files: new (string, byte[])[]
            {
                ("/cdn/model.basalt", model),
                ("/cdn/sub dir/model file.gguf", model),
                ("/dl/basalt-v0.9.9-windows-x64.zip", zipBytes),
            },
            documents: new (string, string)[]
            {
                ("/api/models", """
                    [{"id":"fair/model-one","downloads":12345,"likes":7,"lastModified":"2026-11-02T10:00:00Z",
                      "private":false,"tags":["gguf","basalt","quantized:4-bit","region:us"]},
                     {"id":"freebsd/other-thing","downloads":9,"likes":0,"createdAt":"2026-09-01T00:00:00Z",
                      "private":true,"tags":["model","transformers"]}]
                    """),
                ("/api/models/fair/model-one", listing),
                ("/repos/jesdga95/basalt/releases/latest", releaseJson),
            });
        var Base = wire.Base;

        // ---- what the browser asks for ---------------------------------------------------------------------------------
        var repos = await HuggingFace.SearchAsync(Base, "model", 20, default);
        W("a search answers with rows", repos.Count == 2, string.Join(", ", repos.Select(r => r.Id)));
        W("a row splits into owner, name, numbers and a date",
          repos[0].Owner == "fair" && repos[0].Name == "model-one" && repos[0].Downloads == 12345 &&
          repos[0].UpdatedText.Length == 10, $"{repos[0].Owner}/{repos[0].Name} {repos[0].Downloads} {repos[0].UpdatedText}");
        W("the tags a person would not read are hidden from them",
          repos[0].FactsText == "gguf" && repos[1].FactsText == "",
          $"'{repos[0].FactsText}' then '{repos[1].FactsText}' of [{string.Join("|", repos[0].Tags)}]");
        W("a token in HF_TOKEN is offered to Hugging Face", wire.Auth == "Bearer hf_wire_token", wire.Auth ?? "nothing offered");

        foreach (var (code, expects) in new[] { (403, "needs an account"), (404, "does not know this address"),
                                                (429, "wait a moment"), (500, "having trouble") })
        {
            try
            {
                await HuggingFace.SearchAsync(Base, "raise-" + code, 5, default);
                W($"a {code} from the API becomes a sentence", false, "the call did not fail");
            }
            catch (Exception e) when (e is System.Net.Http.HttpRequestException or IOException)
            {
                W($"a {code} from the API becomes a sentence", e.Message.Contains(expects), e.Message);
            }
        }

        var files = await HuggingFace.FilesAsync(Base, "fair/model-one", default);
        W("a file list carries sizes and sha256s, loadable ones first",
          files.Count == 3 && files[0].IsLoadable && files[0].Sha256 == sha && files[0].Size == size &&
          !files[2].IsLoadable, $"{files.Count}: {string.Join(", ", files.Select(f => f.Name))}");
        W("a folder inside a repo is kept as it is spelled",
          files.Any(f => f.Folder == "sub dir" && f.Name == "model file.gguf"),
          string.Join(", ", files.Select(f => f.Folder + "/" + f.Name)));

        var beats = new List<(int active, string text)>();
        var beatsLock = new object();
        var progress = new Progress<DownloadProgress>(p => { lock (beatsLock) beats.Add((p.ActiveConnections, p.Text)); });
        async Task AwaitBeats()
        {
            for (var i = 0; i < 30 && beats.Count == 0; i++)
                await Task.Delay(50);
        }

        var trace = new List<string>();
        void T(string line) => trace.Add(line);

        // ---- a whole model, the way the model browser pulls one --------------------------------------------------------
        wire.Reset();
        var dest = Path.Combine(scratch, "model.basalt");
        var job = new DownloadJob(HuggingFace.DownloadUrl(Base, "fair/model-one", "model.basalt"), dest, sha, size, "model.basalt");
        var error = await SegmentedDownload.RunAsync(job, 4, progress, T, default);
        await AwaitBeats();
        W("a whole model comes down over four connections", error is null, error ?? "arrived");
        W("the file is byte for byte what the repo published", File.Exists(dest) && Sha(dest) == sha,
          $"{(File.Exists(dest) ? new FileInfo(dest).Length : 0)} bytes");
        W("nothing of the pause machinery is left once the file is in place",
          !File.Exists(job.PartPath) && !File.Exists(job.CheckpointPath));
        W("several connections were in flight at the same instant", wire.PeakParallel >= 2, $"{wire.PeakParallel} at once");
        W("the file was asked for in byte ranges", wire.RangeBodies >= 8, $"{wire.RangeBodies} bodies, {wire.ServedBytes} bytes");
        W("the CDN hop the real Hugging Face answers with is followed",
          wire.Seen.Exists(l => l.Contains("/resolve/main/")) && wire.Seen.Exists(l => l.Contains("/cdn/")),
          wire.Trace("GET /"));
        W("every ask of Hugging Face carried the token",
          wire.Auth == "Bearer hf_wire_token" && wire.Authless == 0,
          $"offered “{wire.Auth ?? "nothing"}”, {wire.Authless} ask(s) came without it");
        W("the token was not handed on to the machine the redirect pointed at",
          wire.CdnAuth is null, "that one was offered “" + wire.CdnAuth + "”");
        lock (beatsLock)
        {
            var spoken = beats.Count > 0 ? beats[^1].text : "nothing at all";
            W("the progress line the window shows is fed while it runs",
              beats.Count > 0 && beats.Exists(b => b.active >= 1) && beats.TrueForAll(b => b.text.Length > 0),
              $"{beats.Count} beats, last “{spoken}”");
        }

        // ---- a path with spaces in it -----------------------------------------------------------------------------------
        var spaced = Path.Combine(scratch, "model file.gguf");
        var spacedJob = new DownloadJob(HuggingFace.DownloadUrl(Base, "fair/model-one", "sub dir/model file.gguf"),
                                        spaced, sha, size, "model file.gguf");
        var spacedError = await SegmentedDownload.RunAsync(spacedJob, 3, null, T, default);
        W("a repo path with spaces survives being turned into an address",
          spacedError is null && File.Exists(spaced) && Sha(spaced) == sha, spacedError ?? spaced);

        // ---- what happens when a connection dies halfway ----------------------------------------------------------------
        var cutDest = Path.Combine(scratch, "resume.gguf");
        var cutJob = new DownloadJob(HuggingFace.DownloadUrl(Base, "fair/model-one", "model.basalt"), cutDest, sha, size, "resume.gguf");

        wire.Reset();
        wire.CutNext = 1;
        var cutError = await SegmentedDownload.RunAsync(cutJob, 3, null, T, default);
        var downAt = wire.ServedBytes;
        W("a connection cut in half is an error, never a short file", cutError is not null && !File.Exists(cutDest),
          cutError ?? "no error: a broken line was swallowed");
        W("what came down is kept, with a map of where it stopped",
          File.Exists(cutJob.PartPath) && File.Exists(cutJob.CheckpointPath));
        W("a second try carries on instead of starting over", downAt < size, $"{downAt} of {size} offered in the first try");

        wire.Reset();
        var carry = await SegmentedDownload.RunAsync(cutJob, 5, null, T, default);
        W("the model arrives at a different number of connections", carry is null && Sha(cutDest) == sha, carry ?? "arrived");
        W("only what was missing came down", wire.ServedBytes * 3 < downAt, $"{wire.ServedBytes} vs {downAt} down so far");
        W("the pause file is gone once the model is in place",
          !File.Exists(cutJob.PartPath) && !File.Exists(cutJob.CheckpointPath));

        wire.Reset();
        wire.CutNext = 1;
        var one = new DownloadJob(cutJob.Url, Path.Combine(scratch, "one.gguf"), cutJob.ExpectedSha256,
                                  cutJob.ExpectedSize, "one.gguf");
        var oneError = await SegmentedDownload.RunAsync(one, 2, null, T, default);
        var oneDown = wire.ServedBytes;
        wire.Reset();
        var last = await SegmentedDownload.RunAsync(one, 1, null, T, default);
        W("one connection still carries on from where several left off",
          oneError is not null && last is null && Sha(one.FinalPath) == sha, $"{oneError} → {last ?? "arrived"}");
        W("and does not ask for bytes it already has", oneDown > 0 && wire.ServedBytes < oneDown,
          $"{wire.ServedBytes} of the {oneDown} already down");

        // ---- a file that is not what the repo says it is -----------------------------------------------------------------
        var liar = new DownloadJob(HuggingFace.DownloadUrl(Base, "fair/model-one", "model.basalt"),
                                   Path.Combine(scratch, "bad.basalt"), new string('a', 64), size, "bad.basalt");
        var liarError = await SegmentedDownload.RunAsync(liar, 2, null, T, default);
        W("a file whose sha256 does not match is refused",
          liarError is not null && liarError.Contains("not what the repo published") && !File.Exists(liar.FinalPath),
          liarError ?? "it was accepted");
        W("what was fetched is left behind as a part, not renamed", File.Exists(liar.PartPath) && !File.Exists(liar.FinalPath));

        // ---- the update check, against a fake GitHub ---------------------------------------------------------------------
        // Sources is an array, so the elements can be pointed at the fake; this block exits before anything else runs.
        var original = UpdateChecker.Sources[0];
        UpdateChecker.Sources[0] = ("fake github", Base + "/repos/jesdga95/basalt/releases/latest");
        var release = await UpdateChecker.LatestAsync(default);
        W("the newest release is read from a GitHub-shaped answer",
          release.Tag == "v0.9.9" && release.Name == "basalt 0.9.9" && release.Version == new Version(0, 9, 9), release.Tag);
        W("the windows zip is the one picked, with its size and its sha256",
          release.WindowsAsset is { Name: "basalt-v0.9.9-windows-x64.zip", Size: > 0 } a && a.Sha256 == zipSha,
          release.WindowsAsset is { } z ? $"{z.Name} {z.SizeText} {z.Sha256[..Math.Min(12, z.Sha256.Length)]}…" : "none");
        W("release notes reach a person as one or two lines",
          release.ShortNotes.Contains("answer-room") && !release.ShortNotes.Contains("##"), release.ShortNotes);

        UpdateChecker.Sources[0] = ("dead", "http://127.0.0.1:9/nothing");
        var second = UpdateChecker.Sources[1];
        UpdateChecker.Sources[1] = ("fake mirror", Base + "/repos/jesdga95/basalt/releases/latest");
        var fallback = await UpdateChecker.LatestAsync(default);
        W("when the first source does not answer, the second is asked", fallback.Tag == "v0.9.9", fallback.Tag);

        UpdateChecker.Sources[1] = ("dead mirror", "http://127.0.0.1:9/nothing");
        try
        {
            await UpdateChecker.LatestAsync(default);
            W("when nothing answers, the app says so in so many words", false, "the call did not fail");
        }
        catch (IOException e)
        {
            W("when nothing answers, the app says so in so many words", e.Message.Contains("no answer"), e.Message);
        }

        UpdateChecker.Sources[0] = original;
        UpdateChecker.Sources[1] = second;

        // ---- putting a release into a folder that only pretends to be basalt ----------------------------------------------
        // The folder the basalt this machine actually runs from. A real, updated install may carry a
        // basalt.exe.previous from its last genuine update, so the thing to demand of the fake install is
        // not "no .previous at all" but "nothing in this folder was added, removed, or touched."
        var realRoot = @"C:\Users\neur0\Documents\basalt\bin";
        static string Snapshot(string dir) => string.Join("\n",
            new DirectoryInfo(dir).EnumerateFiles().OrderBy(f => f.Name)
                .Select(f => $"{f.Name} {f.Length} {f.LastWriteTimeUtc:o}"));
        var realBefore = Snapshot(realRoot);

        var fakeRoot = Path.Combine(scratch, "installroot");   // inside the scratch folder, which was wiped above
        Directory.CreateDirectory(Path.Combine(fakeRoot, "bin"));
        File.WriteAllBytes(Path.Combine(fakeRoot, "bin", "basalt.exe"), oldExe);

        var settings = new AppSettings { DownloadConnections = 3 };
        var got = await UpdateInstaller.DownloadAsync(release, settings, null, T, default);
        W("the release zip comes down the same engine as a model", got is null && File.Exists(UpdateInstaller.LocalZipFor(release)),
          got ?? UpdateInstaller.LocalZipFor(release));
        W("the zip is what the release said it was", Sha(UpdateInstaller.LocalZipFor(release)) == zipSha);

        var put = new List<string>();
        var laid = await UpdateInstaller.ApplyAsync(release, fakeRoot, m => put.Add(m), default);
        W("a new engine is laid over the folder the app was told about", laid is null, laid);
        W("the exe that runs is the one from the zip",
          File.ReadAllBytes(Path.Combine(fakeRoot, "bin", "basalt.exe")).AsSpan().SequenceEqual(newExe));
        W("what it replaced is kept, so it can be renamed back",
          File.Exists(Path.Combine(fakeRoot, "bin", "basalt.exe.previous")) &&
          File.ReadAllBytes(Path.Combine(fakeRoot, "bin", "basalt.exe.previous")).AsSpan().SequenceEqual(oldExe));
        W("the other files in the release come with it", File.Exists(Path.Combine(fakeRoot, "README.md")) &&
          File.Exists(Path.Combine(fakeRoot, "bin", "basalt-bench.exe")));
        W("everything in the release was laid the first time",
          put.Exists(l => l.Contains("3 files laid in place")), string.Join(" | ", put));

        put.Clear();
        var again = await UpdateInstaller.ApplyAsync(release, fakeRoot, m => put.Add(m), default);
        W("putting the same release back again changes nothing",
          again is null && put.Exists(l => l.Contains("0 files laid in place; the engine already here")), string.Join(" | ", put));
        W("and what the first install moved aside is still there to rename back",
          File.ReadAllBytes(Path.Combine(fakeRoot, "bin", "basalt.exe")).AsSpan().SequenceEqual(newExe) &&
          File.ReadAllBytes(Path.Combine(fakeRoot, "bin", "basalt.exe.previous")).AsSpan().SequenceEqual(oldExe));

        File.Delete(Path.Combine(fakeRoot, "README.md"));
        put.Clear();
        var mended = await UpdateInstaller.ApplyAsync(release, fakeRoot, m => put.Add(m), default);
        W("a release run again still mends a file that went missing since",
          mended is null && File.Exists(Path.Combine(fakeRoot, "README.md")) &&
          put.Exists(l => l.Contains("1 file laid in place")), string.Join(" | ", put));

        W("none of it went near the basalt this machine actually runs",
          Snapshot(realRoot) == realBefore,
          "the real install's folder changed: " + Snapshot(realRoot).Replace("\n", " | "));

        Console.WriteLine($"  {(fails == 0 ? "every wire check passed" : fails + " wire check(s) FAILED")}");
        Console.WriteLine($"  the fake install root was {fakeRoot}");
        return fails == 0 ? 0 : 1;
    }
}
