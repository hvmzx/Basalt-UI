using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BasaltUI.Core;

Console.OutputEncoding = Encoding.UTF8;

var configPath = args.FirstOrDefault(a => !a.StartsWith('-')) ?? @"C:\Users\neur0\Documents\basalt_config.json";

// ---- the config reader, on the real file, read-only ---------------------------------------------------
Console.WriteLine("== config ==");
var doc = ConfigDocument.Load(configPath);
Console.WriteLine($"  version={doc.Version} host={doc.Host} port={doc.Port} model entries={doc.ModelEntries().Count}");
Console.WriteLine($"  dirty={doc.Dirty} keyWouldBeRefused={doc.KeyWouldBeRefused}");
foreach (var entry in doc.ModelEntries())
{
    Console.WriteLine($"  entry: model={ConfigDocument.Text(entry, "model")} alias={ConfigDocument.Text(entry, "alias") ?? "-"} " +
                      $"gpu={ConfigDocument.Text(entry, "gpu") ?? "-"} kv={ConfigDocument.Text(entry, "kv") ?? "-"} " +
                      $"ctx={ConfigDocument.Text(entry, "max-context") ?? "-"}");
}
var issues = doc.Validate(SettingsCatalog.Shared);
Console.WriteLine($"  validation issues: {issues.Count}");
foreach (var issue in issues)
    Console.WriteLine($"    {(issue.Error ? "ERROR" : "note ")} {issue.Where}: {issue.Message}");

// ---- the two ways of measuring memory, printed side by side -----------------------------------------
// This is the "does the app agree with Task Manager" question, made checkable: the DXGI and
// GlobalMemoryStatusEx figures are the ones the UI now shows, nvidia-smi and basalt's /stats are what it
// used to lean on. Everything here is a read.
if (args.Contains("--memory"))
{
    Console.WriteLine("== memory ==");
    var sample = MachineMemory.Read(0);
    var m = sample.Memory;
    var v = sample.Video;

    Console.WriteLine($"  ram  os (what the app shows): usable={BasaltLocator.HumanBytes(m.Usable)} " +
                      $"in-use={BasaltLocator.HumanBytes(m.InUse)} available={BasaltLocator.HumanBytes(m.Available)} " +
                      $"installed={(m.Installed > 0 ? BasaltLocator.HumanBytes(m.Installed) : "unknown")}");

    var smi = MachineMemory.ReadVramFromNvidiaSmi();
    Console.WriteLine($"  vram os (what the app shows):  known={v.Known} source={v.Source} name=\"{v.Name}\" " +
                      $"total={BasaltLocator.HumanBytes(v.Total)} used={BasaltLocator.HumanBytes(v.InUse)} free={BasaltLocator.HumanBytes(v.Free)}");
    Console.WriteLine($"  vram nvidia-smi (old source):   known={smi.Known} name=\"{smi.Name}\" " +
                      $"total={BasaltLocator.HumanBytes(smi.Total)} used={BasaltLocator.HumanBytes(smi.InUse)} free={BasaltLocator.HumanBytes(smi.Free)}");
    Console.WriteLine($"  vram walk: {MachineMemory.DiagnoseVram(0)}");
    if (v.Source == "DXGI+PDH" && smi.Known)
        Console.WriteLine($"  vram os vs nvidia-smi: nvidia-smi's total is {(smi.Total - v.Total) / (1024 * 1024)} MiB larger, " +
                          $"usage differs by {(v.InUse - smi.InUse) / (1024 * 1024)} MiB " +
                          $"(nvidia-smi counts the driver's carve-out too: why it never matched Task Manager)");

    var probeClient = new BasaltClient();
    var status = await probeClient.MetricsAsync();
    if (status is { RamTotal: > 0 })
        Console.WriteLine($"  basalt /stats hw: ram_used={BasaltLocator.HumanBytes(status.RamUsed)} of {BasaltLocator.HumanBytes(status.RamTotal)} " +
                          $"(basalt's own view, no longer shown as the machine's)");
    foreach (var gpu in status?.Gpus ?? [])
        Console.WriteLine($"  basalt /stats gpu{gpu.Index}: {BasaltLocator.HumanBytes(gpu.MemUsed)} of {BasaltLocator.HumanBytes(gpu.MemTotal)} " +
                          $"nvml total, name=\"{gpu.Name}\"");

    // Key on the source, not on Known: the nvidia-smi fallback sets Known too, and a check
    // that passes while walking the fallback is a check that quietly stopped checking.
    Console.WriteLine(v.Source == "DXGI+PDH"
        ? "  ok: DXGI named the card and the performance counter filled it — the app reads GPU memory the way Task Manager does"
        : "  FAIL: the OS figures did not come through (source=" + v.Source + "), the app fell back to nvidia-smi and its numbers are not Task Manager's");
    Environment.Exit(v.Source == "DXGI+PDH" ? 0 : 1);
}

// ---- the writer, on a scratch copy only: never the user's file ---------------------------------------
if (args.Contains("--write"))
{
    Console.WriteLine("== config write (scratch copy only) ==");
    var scratch = Path.Combine(AppContext.BaseDirectory, "scratch");
    Directory.CreateDirectory(scratch);
    var copy = Path.Combine(scratch, "basalt_config.json");
    File.Copy(configPath, copy, overwrite: true);

    var w = ConfigDocument.Load(copy);
    w.Server["zz-not-a-real-key"] = "keep me";                        // a key the app has never heard of
    w.SetSwitch(w.Server, "request-lines", false);                    // a false switch must not be written
    w.SetNumber(w.Server, "port", "18080");
    w.SetList(w.Server, "cors-origin", new[] { "http://localhost:3000", "  " });
    w.SetNumber(w.Engine, "parallel", "2");                           // written as a JSON number, not a string
    w.SetNumber(w.Engine, "batch", "4");                              // legal, but it clashes with parallel
    w.SetSwitch(w.Server, "debug", true);                             // a true switch is written

    static string Errors(ConfigDocument d)
    {
        var bad = d.Validate(SettingsCatalog.Shared).Where(i => i.Error).ToList();
        return bad.Count == 0 ? "none" : string.Join(" || ", bad.Select(i => i.Where + ": " + i.Message));
    }

    Console.WriteLine($"  port read from the DOM just written (a CLR long, not a JSON element): {w.Port}");
    w.Save();

    var again = ConfigDocument.Load(copy);
    var portName = ConfigDocument.FindKey(again.Server, "port")!;
    Console.WriteLine($"  port wrote as JSON {again.Server[portName]!.ToJsonString()} read back {again.Port}");
    Console.WriteLine($"  unknown key survived: {ConfigDocument.Text(again.Server, "zz-not-a-real-key") ?? "LOST"}");
    Console.WriteLine($"  false switch written: {ConfigDocument.Has(again.Server, "request-lines")} " +
                      $"true switch written: {ConfigDocument.Text(again.Server, "debug") ?? "MISSING"}");
    Console.WriteLine($"  cors-origin: [{string.Join("|", ConfigDocument.List(again.Server, "cors-origin"))}]");
    var parName = ConfigDocument.FindKey(again.Engine, "parallel")!;
    Console.WriteLine($"  engine parallel read as {ConfigDocument.Text(again.Engine, "parallel") ?? "MISSING"}, " +
                      $"in JSON as {again.Engine[parName]?.ToJsonString() ?? "?"}");
    Console.WriteLine($"  dirty after load: {again.Dirty}, entries still {again.ModelEntries().Count}");
    Console.WriteLine($"  errors (planted key + parallel/batch): {Errors(again)}");

    var backups = Directory.Exists(AppPaths.BackupsDir)
        ? Directory.EnumerateFiles(AppPaths.BackupsDir, "*.json").Select(Path.GetFileName).ToList()
        : new List<string?>();
    Console.WriteLine($"  backups in {AppPaths.BackupsDir}: [{string.Join(", ", backups)}]");
    again.Save();                                                     // identical content: must not add a backup
    again.SetNumber(again.Server, "port", "18081");
    again.Save();                                                     // changed again, same second if possible
    Console.WriteLine($"  backups after two more saves: [{string.Join(", ", Directory.EnumerateFiles(AppPaths.BackupsDir, "*.json").Select(Path.GetFileName))}]");

    again.Root["notes"] = "stray";                                    // only version/server/engine/models may sit here
    again.Engine["port"] = 8081;                                      // a server option misplaced in the engine block
    again.Engine["gpu"] = "0";                                        // legal here: it reaches every model
    Console.WriteLine($"  errors (stray root key + engine port): {Errors(again)}");

    again.Root.Remove("notes");
    again.Engine.Remove("port");
    again.Engine.Remove("batch");
    again.Server.Remove("zz-not-a-real-key");
    var post = again.Validate(SettingsCatalog.Shared);
    Console.WriteLine($"  after cleanup: {post.Count} issue(s), errors: {Errors(again)}");
    Console.WriteLine("  notes: " + string.Join(" | ", post.Where(i => !i.Error).Select(i => i.Where + ": " + i.Message)));
    again.Save();
    Console.WriteLine($"  file is stable across a reload and a no-op save: {File.ReadAllText(copy) == ConfigDocument.Load(copy).ToJson()}");
}

if (args.Contains("--checks"))
{
    // The parts of the internet-facing code that can be judged without the internet: a releases payload
    // written the way GitHub sends one, version arithmetic, address building, and the downloader's file names.
    Console.WriteLine("== checks (offline) ==");
    var fails = 0;
    void Check(string what, bool good, string? detail = null)
    {
        if (!good) fails++;
        Console.WriteLine($"  {(good ? "ok  " : "FAIL")} {what}{(detail is null ? "" : ": " + detail)}");
    }

    const string payload = """
    {
      "tag_name": "v0.3.3",
      "name": "basalt 0.3.3",
      "html_url": "https://github.com/jesdga95/basalt/releases/tag/v0.3.3",
      "published_at": "2026-11-10T18:04:00Z",
      "body": "## What is new\n\n- answer-room now takes auto or a number\n\n- /props carries the version and capabilities\n\n- a longer line that will not be shown",
      "assets": [
        { "name": "basalt-v0.3.3-macos-arm64.tar.gz", "size": 40000000, "digest": null,
          "browser_download_url": "https://github.com/x/y/releases/download/v0.3.3/basalt-v0.3.3-macos-arm64.tar.gz" },
        { "name": "basalt-v0.3.3-windows-x64.zip", "size": 62613806,
          "digest": "sha256:3505D587db8473a1522e7046ec3336dc8cfd6a488197936ee36b605f7a0bf196",
          "browser_download_url": "https://github.com/x/y/releases/download/v0.3.3/basalt-v0.3.3-windows-x64.zip" },
        { "name": "basalt-v0.3.3-checksums.txt", "size": 220, "digest": "sha256:not even hex",
          "browser_download_url": "https://github.com/x/y/releases/download/v0.3.3/basalt-v0.3.3-checksums.txt" }
      ]
    }
    """;

    var release = UpdateChecker.Parse(System.Text.Json.Nodes.JsonNode.Parse(payload)!.AsObject());
    Check("release tag and number", release.Tag == "v0.3.3" && release.Version == new Version(0, 3, 3),
          $"{release.Tag} / {release.Version} / published {release.PublishedAt}");
    Check("the windows zip is the one picked", release.WindowsAsset?.Name == "basalt-v0.3.3-windows-x64.zip",
          release.WindowsAsset?.Name);
    Check("its sha256 is read and lowercased",
          release.WindowsAsset?.Sha256 == "3505d587db8473a1522e7046ec3336dc8cfd6a488197936ee36b605f7a0bf196",
          release.WindowsAsset?.Sha256);
    Check("a digest that is not a sha256 is dropped",
          release.Assets.First(a => a.Name.EndsWith(".txt")).Sha256.Length == 0);
    Check("an asset with no digest is fine", release.Assets.First(a => a.Name.EndsWith(".tar.gz")).Sha256.Length == 0);
    Check("size reads as bytes and says it as text",
          release.WindowsAsset!.Size == 62_613_806 && release.WindowsAsset.SizeText.Length > 0,
          release.WindowsAsset.SizeText);
    Check("a banner shows the notes without the heading",
          release.ShortNotes.Contains("answer-room") && !release.ShortNotes.Contains('#'), release.ShortNotes);

    Check("a newer release beats what is installed", UpdateChecker.Compare("0.3.2", release) == 1);
    Check("the same release does not", UpdateChecker.Compare("0.3.3", release) == 0);
    Check("a four-part number does not invent a difference", UpdateChecker.Compare("0.3.3.0", release) == 0);
    Check("an installed build ahead of it says nothing", UpdateChecker.Compare("0.4.0", release) == -1);
    Check("a number inside a sentence is still a number", UpdateChecker.Compare("basalt 0.3.2 (1a2b3c)", release) == 1);
    Check("no number at all is not the same as being behind", UpdateChecker.Compare("", release) == int.MinValue);
    Check("0.3 parses as a version", UpdateChecker.ParseVersion("0.3") == new Version(0, 3));
    Check("text without a number parses as nothing", UpdateChecker.ParseVersion("development") is null);
    Check("nothing answering and no exe is an empty answer",
          UpdateChecker.InstalledVersionText(null, @"C:\does\not\exist\basalt.exe").Length == 0);

    // basalt.exe carries no version resource, so the number the engine announces while running is the only one
    // this machine has: it stands in for the file, and only for as long as it describes that exact copy.
    const string realExe = @"C:\Users\neur0\Documents\basalt\bin\basalt.exe";
    var bare = UpdateChecker.InstalledVersionText(null, realExe);
    var realKey = UpdateChecker.ExeVersionKey(realExe);
    Check("a number remembered for this exact copy stands in for it",
          UpdateChecker.InstalledVersionText(null, realExe, "0.9.9", realKey) == "0.9.9",
          $"the file alone says {(bare.Length == 0 ? "nothing at all" : bare)}, so a memory is what the app has to go on");
    Check("a number remembered for a different copy does not",
          UpdateChecker.InstalledVersionText(null, realExe, "0.9.9", realKey + " changed") == bare,
          "still " + (bare.Length == 0 ? "nothing" : bare));
    var ownExe = Environment.ProcessPath ?? "";
    var ownSays = UpdateChecker.InstalledVersionText(null, ownExe);
    Check("a file that can speak for itself is not overruled by a memory",
          UpdateChecker.InstalledVersionText(null, ownExe, "0.9.9", UpdateChecker.ExeVersionKey(ownExe)) != "0.9.9",
          Path.GetFileName(ownExe) + " says " + (ownSays.Length == 0 ? "nothing" : "“" + ownSays + "”"));
    Check("a file that is not there is described by no memory",
          UpdateChecker.ExeVersionKey(@"C:\does\not\exist.exe").EndsWith("missing")
          && UpdateChecker.InstalledVersionText(null, @"C:\does\not\exist.exe", "0.9.9", realKey).Length == 0);
    Check("the zip the installer would reuse sits under the updates folder",
          Path.GetFileName(UpdateInstaller.LocalZipFor(release)) == "basalt-v0.3.3-windows-x64.zip"
          && UpdateInstaller.LocalZipFor(release).StartsWith(AppPaths.UpdateDir, StringComparison.OrdinalIgnoreCase),
          UpdateInstaller.LocalZipFor(release));

    Check("no mirror means the canonical address",
          HuggingFace.Base(null) == HuggingFace.Canonical && HuggingFace.Base("   ") == HuggingFace.Canonical);
    Check("a bare mirror host gets https and no trailing slash",
          HuggingFace.Base("hf-mirror.com/") == "https://hf-mirror.com", HuggingFace.Base("hf-mirror.com/"));
    Check("a mirror that already carries a scheme is kept",
          HuggingFace.Base("https://mirror.example/dir/") == "https://mirror.example/dir", HuggingFace.Base("https://mirror.example/dir/"));

    // AbsoluteUri, not ToString: Uri.ToString() hands back the unescaped form, which is a display string.
    var url = HuggingFace.DownloadUrl(null, "Some Owner/Model Name", "inner dir/model file.gguf").AbsoluteUri;
    Check("a download address is escaped where a browser needs it",
          url == "https://huggingface.co/Some%20Owner/Model%20Name/resolve/main/inner%20dir/model%20file.gguf", url);
    Check("the mirror is honoured in the same address",
          HuggingFace.DownloadUrl("mirror.example", "o/r", "f.gguf").AbsoluteUri == "https://mirror.example/o/r/resolve/main/f.gguf");

    var shard = new HfFile("Qwen/Model/Q4_K_M-00001-of-00002.gguf", 4_999_999_999, "");
    Check("a shard is named, placed, sized and offered",
          shard is { IsGguf: true, IsLoadable: true, Name: "Q4_K_M-00001-of-00002.gguf", Folder: "Qwen/Model", Detail: "Q4_K_M-00001-of-00002" }
          && shard.SizeText.Length > 0, $"{shard.Name} · {shard.SizeText}");
    Check("a file with no folder is still a file", new HfFile("model.gguf", 0, "").Folder.Length == 0 && new HfFile("model.gguf", 0, "").SizeText == "–");
    Check("only .basalt and .gguf are offered to load",
          new HfFile("a/model.safetensors", 10, "").IsLoadable == false && new HfFile("a/model.basalt", 10, "").IsLoadable);

    var repo = new HfRepo("Some Owner/Model Name", 123456, 77, DateTime.Parse("2026-11-01T10:00:00Z"), false,
                          ["gguf", "basalt", "text-generation", "quantized:4-bit", "llama"]);
    Check("a repo splits into owner and name", repo.Owner == "Some Owner" && repo.Name == "Model Name", $"{repo.Owner} / {repo.Name}");
    Check("the facts line keeps what a person reads", repo.FactsText == "gguf  llama", repo.FactsText);
    Check("the update date is shown as a date", repo.UpdatedText.Length == 10, repo.UpdatedText);

    var job = new DownloadJob(new Uri("https://example/o/r/resolve/main/a file.gguf"), @"C:\models\a file.gguf");
    Check("a pause can be left next to the file it belongs to",
          job.PartPath == @"C:\models\a file.gguf.part" && job.CheckpointPath == @"C:\models\a file.gguf.part.json"
          && job.Name == "a file.gguf", $"{job.Name} → {Path.GetFileName(job.CheckpointPath)}");

    foreach (var e in new Exception[]
             {
                 new System.Net.Http.HttpRequestException("The SSL connection could not be established"),
                 new TaskCanceledException("The request was canceled due to the configured timeout"),
                 new IOException("no such host"),
             })
        Check("an ugly failure still becomes a sentence " + e.GetType().Name,
              !string.IsNullOrWhiteSpace(Net.Explain(e)), Net.Explain(e));

    // The settings file is the one place where a value no screen ever shows decides whether the app
    // remembers anything: System.Text.Json refuses to write a NaN at all, and an NaN default used to mean
    // settings.json was never created — silently, with the reason only in a log line. Probed here, on an
    // in-memory instance, so nobody's real file is touched.
    var written = "";
    string? trouble = null;
    try
    {
        written = System.Text.Json.JsonSerializer.Serialize(new AppSettings(),
                                                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
    catch (Exception e)
    {
        trouble = e.Message;
    }

    Check("a fresh settings file can actually be written", trouble is null, trouble ?? written.Length + " chars");
    Check("and a window that was never placed is written as nothing", written.Contains("\"WindowLeft\": null") &&
          !written.Contains("NaN"),
          written.Contains("WindowLeft")
              ? written[written.IndexOf("WindowLeft", StringComparison.Ordinal)..].Split(',')[0].Trim()
              : "(no window left in the file at all)");

    Check("the update decision: nothing answering means the files can go in",
          UpdatePlan.Decide(answers: false, ours: true, null, 8080) is { Clear: true, AskFirst: false, Blocked: false } p && p.Pid == 0);
    Check("the update decision: our own server is stopped without a ceremony",
          UpdatePlan.Decide(answers: true, ours: true, [4321], 8080) is { StopOurs: true, AskFirst: false, Blocked: false });
    Check("the update decision: a server from elsewhere is asked about, by pid",
          UpdatePlan.Decide(answers: true, ours: false, [9001, 9002], 8080) is { AskFirst: true, StopOurs: false, Blocked: false } q && q.Pid == 9001
          && q.Question("v0.3.3").Contains("9001") && q.Question("v0.3.3").Contains("8080"));
    Check("the update decision: something answering with no name is a dead end",
          UpdatePlan.Decide(answers: true, ours: false, [], 8080) is { Blocked: true, AskFirst: false } z && z.Question("v0.3.3").Contains("Stop that server yourself"));
    Check("the update decision: an empty listeners list is the same as no listeners",
          UpdatePlan.Decide(answers: false, ours: false, [], 18085).Clear);
    Check("and our own server is never called a stranger, even with no pid to name",
          UpdatePlan.Decide(answers: true, ours: true, null, 8080) is { Blocked: false, StopOurs: true } o
          && o.Question("v0.3.3").Contains("basalt has to stop"));

    Console.WriteLine($"  {(fails == 0 ? "all offline checks passed" : fails + " check(s) FAILED")}");
    Environment.Exit(fails == 0 ? 0 : 1);
}

if (args.Contains("--paths"))
{
    // The answer to "where does it keep its files, and why did that fail": the app's own AppPaths class,
    // with the app's own fallbacks, plus a real write to prove the folder takes something. Nothing is changed.
    Console.WriteLine("== where Basalt UI keeps its files ==");
    Console.WriteLine("  using:  " + AppPaths.Root);
    Console.WriteLine("  note:   " + (AppPaths.Trouble ?? "the first place it wanted worked"));
    foreach (var (what, where) in new[]
             {
                 ("settings", AppPaths.SettingsFile), ("log", AppPaths.LogFile),
                 ("config backups", AppPaths.BackupsDir), ("release zips", AppPaths.UpdateDir),
             })
    {
        var info = new FileInfo(where);
        var line = info.Exists ? $"last write {info.LastWriteTimeUtc.ToLocalTime():yyyy-MM-dd HH:mm}, {info.Length} bytes"
              : Directory.GetParent(where)?.Exists == true ? "the folder is there, nothing in it yet"
              : "no such folder";
        Console.WriteLine("  " + what.PadRight(15) + where);
        Console.WriteLine(" " + new string(' ', 17) + line);
    }

    Console.WriteLine("  temp:        " + Path.GetTempPath() + (AppPaths.ScratchDir is null
                                                                    ? "  (the system one; the app could not make its own)"
                                                                    : "  — inside the app's own folder, so WPF has somewhere for its font scratch"));
    if (AppPaths.ScratchDir is not null)
        Console.WriteLine("               it was " + AppPaths.OriginalTemp + " before the app moved it");

    // Where WPF itself unpacks fonts while drawing text. A protective filter that dislikes the system temp
    // says no here, and the window comes down — which is why the app moves TMP/TEMP when it can.
    var wpfTemp = Path.Combine(Path.GetTempPath(), "WPF");
    string? fontRefused = null;
    try
    {
        Directory.CreateDirectory(wpfTemp);
        var probe = Path.Combine(wpfTemp, "write-test.tmp");
        File.WriteAllText(probe, "a word");
        File.Delete(probe);
    }
    catch (Exception e)
    {
        fontRefused = e.Message;
    }

    Console.WriteLine(fontRefused is null
                          ? "  WPF scratch: fine in " + wpfTemp
                          : "  WPF scratch: REFUSED in " + wpfTemp + " — " + fontRefused);

    var test = Path.Combine(AppPaths.Root, "write-test.tmp");
    string? refused = null;
    try
    {
        File.WriteAllText(test, "a word");
    }
    catch (Exception e)
    {
        refused = e.Message;
    }

    if (refused is null)
    {
        try
        {
            refused = File.ReadAllText(test) == "a word" ? null : "wrote something and could not read it back";
        }
        finally
        {
            try { File.Delete(test); } catch { /* a leftover temp file is not worth a fuss */ }
        }
    }

    Console.WriteLine(refused is null ? "  writing there: fine" : "  writing there: REFUSED — " + refused);
    Console.WriteLine(refused is null
                          ? "  the app can keep its settings there, so a missing settings.json means it was never asked to save"
                          : "  the app cannot keep its settings there: that is why nothing is remembered and the log stays empty");
    Environment.Exit(refused is null ? 0 : 1);
}

if (args.Contains("--wire"))
{
    // Kept inside a try on purpose: an unhandled exception in the middle of a suite would leave this
    // process as a husk holding its own exe, and nothing downstream could be built again.
    try
    {
        Environment.Exit(await WireTests.RunAsync());
    }
    catch (Exception e)
    {
        Console.WriteLine("  FAIL the wire run stopped: " + e.Message);
        Environment.Exit(1);
    }
}

Console.WriteLine("== catalog ==");
Console.WriteLine($"  settings={SettingsCatalog.Shared.All.Count} notes={SettingsCatalog.Shared.Notes.Count} " +
                  $"server={SettingsCatalog.Shared.ServerSettings.Count()} engine={SettingsCatalog.Shared.EngineSettings.Count()} " +
                  $"entry={SettingsCatalog.Shared.EntrySettings.Count()} groups={SettingsCatalog.Shared.Grouped(SettingsCatalog.Shared.All).Count()}");
Console.WriteLine($"  command-only={SettingsCatalog.Shared.All.Count(d => d.IsCommandOnly)} " +
                  $"paths={SettingsCatalog.Shared.All.Count(d => d.IsPath)} " +
                  $"lists={SettingsCatalog.Shared.All.Count(d => d.IsList)}");
var missing = new[] { "host", "port", "api-key", "gpu", "kv", "max-context", "resident-experts", "parallel" }
              .Where(k => SettingsCatalog.Shared.Find(k) is null);
Console.WriteLine("  keys the UI looks for that the catalog does not have: " +
                  (missing.Any() ? string.Join(", ", missing) : "none"));

// ---- the same poller the window uses, pointed at the live server --------------------------------------
Console.WriteLine($"== poller at http://127.0.0.1:{doc.Port} (GET only) ==");
var client = new BasaltClient();
var poller = new StatusPoller(client);
poller.Configure(doc.Port, "127.0.0.1", apiKey: null, readServerLog: true);

var batches = 0;
var gotLines = 0;
string? lastLine = null;
poller.ServerLogLines += lines =>
{
    batches++;
    gotLines += lines.Count;
    lastLine = lines[^1];
};

await poller.BeatAsync();
await poller.BeatAsync();          // second beat takes the full /metrics path
await poller.BeatAsync();          // third beat pulls the server's own log buffer
await poller.BeatAsync();
var s = poller.Current!;

Console.WriteLine($"  seen={s.SeenAt:HH:mm:ss} reachable={s.Reachable} error={s.Error ?? "-"} busy={s.Busy} warning={s.Warning} " +
                  $"state={s.State} loaded={s.Loaded} model={s.ModelName}");
Console.WriteLine($"  build={s.BuildInfo} sleeping={s.Sleeping} path={s.ModelPath}");
Console.WriteLine($"  engine v{s.EngineVersion} quant={s.Quant} kv={s.Kv} gpuIds={s.GpuIds} ctx={s.MaxContext} images={s.Images} " +
                  $"spec={s.Spec} pool={s.PoolWorkers} pcie={s.PcieFrac:0.###} spill={s.Spill ?? "-"}");
Console.WriteLine($"  experts slots={s.ExpertSlots}/{s.ExpertTotal} cacheMiB={s.ExpertCacheMib} vramFreeMiB={s.VramFreeMib} arenaMiB={s.ArenaMib}");
Console.WriteLine($"  live state={s.LiveState} queued={s.Queued} slots={s.SlotsActive}/{s.SlotsTotal} depth={s.QueueDepth} " +
                  $"tok/s={s.TokS?.ToString("0.##") ?? "-"} prefill={s.PrefillTokS?.ToString("0.##") ?? "-"} " +
                  $"elapsed={s.ElapsedS:0.#}s prompt={s.LivePromptTokens} gen={s.LiveGenerated}");
Console.WriteLine($"  cpu={s.CpuPercent:0.#}% ({s.Cores}c/{s.Threads}t) ram={s.RamUsed / 1073741824.0:0.##}/{s.RamTotal / 1073741824.0:0.##} GiB " +
                  $"driver={s.Driver} cuda={s.Cuda} disk={s.DiskReadMb:0.##} MB/s");
foreach (var g in s.Gpus)
    Console.WriteLine($"  gpu{g.Index} {g.Name ?? "?"} util={g.Util:0.#}% mem={g.MemUsed / 1048576.0:0.##}/{g.MemTotal / 1048576.0:0.##} GiB " +
                      $"temp={g.Temp:0}C power={g.Power:0}/{g.PowerLimit:0} W gen={g.PcieGen:0.#} rx={g.PcieRx:0.#} tx={g.PcieTx:0.#}");
Console.WriteLine($"  models={s.Models.Count} requests={s.Requests.Count} queue={s.Queue.Count}");
foreach (var m in s.Models)
    Console.WriteLine($"    model {m.Id}: {m.Status} asleep={m.Asleep} ctx={m.NCtx} vision={m.Vision}");
foreach (var r in s.Requests.Take(3))
    Console.WriteLine($"    req {r.Id} {r.Path} {r.DurationS:0.##}s tok {r.PromptTokens}+{r.OutputTokens} reused={r.Reused} " +
                      $"reuse={100.0 * r.Reused / Math.Max(1, r.PromptTokens):0.#}% decode={r.DecodeTokS:0.#} " +
                      $"expert hit={r.HitRate:P0} ttft={r.TtftS:0.###}s pcie={r.PcieShare:P0} ssd={(r.SsdShare.HasValue ? r.SsdShare.Value.ToString("P0") : "-")} " +
                      $"drafts={r.DraftsAccepted}/{r.DraftsOffered}");
Console.WriteLine($"  history: tok/s={s.TokSHistory.Length} prefill={s.PrefillHistory.Length} " +
                  $"slots={s.SlotHistories.Length}x{s.SlotHistories.FirstOrDefault()?.Length ?? 0} " +
                  $"gpuUtil={s.GpuUtilHistory.Length} gpuW={s.GpuPowerHistory.Length} gpuC={s.GpuTempHistory.Length}");
Console.WriteLine($"  last decode samples: {string.Join(" ", s.TokSHistory.TakeLast(10).Select(v => v.ToString("0.#")))}");
Console.WriteLine($"  server log events: {batches} batches, {gotLines} lines, newest: {lastLine}");

// Which process owns which console: basalt's graceful stop is a Ctrl+C, and a console event never travels
// into another console — so a stop has to find the server's processes first, and then the console they share.
static void ShowTree(string label, IEnumerable<int> pids)
{
    var rows = NativeMethods.ProcessTable();
    Console.WriteLine("  " + label);
    foreach (var pid in pids.Distinct())
    {
        var row = rows.FirstOrDefault(r => r.Pid == pid);
        var sb = new StringBuilder($"    pid {pid,6} ");
        sb.Append(row.Pid == 0 ? "already gone" : $"{row.Name} started by {row.ParentPid}");
        var kids = rows.Where(r => r.ParentPid == pid).Select(r => $"{r.Pid} {r.Name}").ToList();
        sb.Append(kids.Count == 0 ? "   no children" : "   children [" + string.Join(", ", kids) + "]");
        var share = NativeMethods.ConsolePids(pid);
        sb.Append(share is null ? "   · no console we can attach to" : "   · console shared with [" + string.Join(", ", share) + "]");
        Console.WriteLine(sb.ToString());
    }
}

if (args.Contains("--serve"))
{
    Console.WriteLine("== start and stop, mock engine on a spare port (no model, no VRAM) ==");
    const int mockPort = 18085;
    var busy = NativeMethods.ListenersOnPort((ushort)mockPort);
    if (busy.Length > 0)
    {
        Console.WriteLine($"  port {mockPort} is already answered by pid {string.Join(", ", busy)} — skipping");
    }
    else
    {
        var sdir = Path.Combine(AppContext.BaseDirectory, "serve");
        Directory.CreateDirectory(sdir);
        var mockPath = Path.Combine(sdir, "mock_config.json");
        File.Copy(configPath, mockPath, overwrite: true);
        var mock = ConfigDocument.Load(mockPath);
        mock.SetText(mock.Server, "host", "127.0.0.1");
        mock.SetNumber(mock.Server, "port", mockPort.ToString());
        mock.Save(takeBackup: false);
        var portKey = ConfigDocument.FindKey(mock.Server, "port");
        Console.WriteLine($"  scratch config {mockPath}: host={mock.Host} port={mock.Port} errors={mock.Validate(SettingsCatalog.Shared).Count(i => i.Error)}");
        var portJson = portKey is null ? "-" : mock.Server[portKey]?.ToJsonString() ?? "null";
        Console.WriteLine($"  port in memory: key = {portKey ?? "MISSING"}, json = {portJson}");
        Console.WriteLine("  file lines mentioning port: " +
                          string.Join(" | ", File.ReadAllLines(mockPath).Where(l => l.Contains("port", StringComparison.OrdinalIgnoreCase)).Select(l => l.Trim())));

        var spec = new LaunchSpec
        {
            ExePath = @"C:\Users\neur0\Documents\basalt\bin\basalt.exe",
            ConfigPath = mockPath,
            ExtraArgs = "--mock-engine",
            Port = mockPort,
        };
        Console.WriteLine("  arguments: " + spec.BuildArguments());

        var proc = new BasaltProcess();
        var console = new List<string>();
        proc.ConsoleLine += l => { lock (console) console.Add(l); };
        proc.Notice += n => Console.WriteLine("  notice: " + n);
        proc.StateChanged += (st, d) => Console.WriteLine($"  state: {st} — {d}");

        if (!proc.Start(spec))
        {
            Console.WriteLine("  start failed: " + proc.LaunchError);
        }
        else
        {
            Console.WriteLine($"  started: pid={proc.Pid} mine={proc.StartedByUs} alive={proc.IsAlive}");

            ShowTree($"the mock server on {mockPort}, found from our own pid {proc.Pid}",
                     NativeMethods.ServerTree(proc.Pid, mockPort));

            var hc = new BasaltClient();
            hc.Configure(mockPort, "127.0.0.1", null);
            HealthResult? health = null;
            var t0 = DateTime.UtcNow;
            while (DateTime.UtcNow - t0 < TimeSpan.FromSeconds(120))
            {
                health = await hc.HealthAsync();
                if (health is not null)
                    break;
                await Task.Delay(500);
            }
            Console.WriteLine($"  /health after {(DateTime.UtcNow - t0).TotalSeconds:0.#}s: " +
                              (health is null ? "no answer"
                                  : $"ok={health.Ok} state={health.State} model={health.Model} loaded={health.Loaded} percent={health.Percent}"));

            var mockPoller = new StatusPoller(hc);
            mockPoller.Configure(mockPort, "127.0.0.1", null, readServerLog: true);
            var mirrorLines = 0;
            mockPoller.ServerLogLines += l => mirrorLines += l.Count;
            await mockPoller.BeatAsync();
            await mockPoller.BeatAsync();
            var ms = mockPoller.Current!;
            Console.WriteLine($"  mock dashboard: reachable={ms.Reachable} state={ms.State} loaded={ms.Loaded} model={ms.ModelName} " +
                              $"busy={ms.Busy} warning={ms.Warning} err={ms.Error ?? "-"} gpus={ms.Gpus.Count} reqs={ms.Requests.Count} " +
                              $"hist={ms.TokSHistory.Length} build={ms.BuildInfo ?? "-"} sleep={ms.Sleeping}");
            var props = await hc.PropsAsync();
            Console.WriteLine($"  /props: build={props?.BuildInfo ?? "-"} path={props?.ModelPath ?? "-"} sleeping={props?.Sleeping}");
            var logs = await hc.LogsAsync();
            Console.WriteLine($"  /api/logs: {logs.Lines.Count} lines, cursor={logs.Last}, restarted={logs.Restarted}, newest={(logs.Lines.Count > 0 ? logs.Lines[^1] : "-")}");
            Console.WriteLine($"  mirrored {mirrorLines} server log lines through the poller");
            mockPoller.Dispose();


            var ts = DateTime.UtcNow;
            var stopped = await proc.StopAsync(45);
            Console.WriteLine($"  StopAsync -> {stopped} in {(DateTime.UtcNow - ts).TotalSeconds:0.#}s; " +
                              $"alive={proc.IsAlive} exit={proc.LastExitCode} state={proc.State} ({proc.StateDetail})");
        }

        Console.WriteLine($"  console lines captured: {console.Count}");
        lock (console)
        {
            foreach (var (line, i) in console.Select((line, i) => (line, i)))
                if (i < 8 || i >= console.Count - 12)
                    Console.WriteLine($"    {i:000} | {line}");
            var serverWords = console.Where(l => l.Contains("[server]")).Select(l => l.Trim()).ToList();
            Console.WriteLine("  basalt's own [server] words: " + (serverWords.Count == 0 ? "none" : string.Join(" | ", serverWords)));
        }
        proc.Dispose();
        Console.WriteLine($"  listeners still on {mockPort}: [{string.Join(", ", NativeMethods.ListenersOnPort((ushort)mockPort))}]");
    }
}

if (args.Contains("--adopt"))
{
    // the same shape the window uses when a server is answering but we did not start it
    var pids = NativeMethods.ListenersOnPort((ushort)doc.Port);
    Console.WriteLine($"  listeners on {doc.Port}: [{string.Join(", ", pids)}] " +
                      string.Join(" ", pids.Select(p => NativeMethods.MainWindowTitleOf(p) is { Length: > 0 } t ? $"pid {p} “{t}”" : $"pid {p}")) );
    Console.WriteLine($"  children of first listener: [{string.Join(", ", pids.Length > 0 ? NativeMethods.ChildProcesses(pids[0]).Take(8) : [])}]");
}

if (args.Contains("--tree"))
{
    // a read-only look at a running server: who is who, and whose console a Ctrl+C would have to reach
    var listeners = NativeMethods.ListenersOnPort((ushort)doc.Port);
    Console.WriteLine($"== who runs the server on {doc.Port} (nothing is signalled) ==");
    Console.WriteLine($"  process snapshot rows: {NativeMethods.ProcessTable().Count}");
    ShowTree($"found by port {doc.Port}", NativeMethods.ServerTree(0, doc.Port));
    foreach (var listener in listeners)
        ShowTree($"the tree under the listener pid {listener}", new[] { listener }.Concat(NativeMethods.ChildProcesses(listener)));
}

poller.Dispose();
Environment.Exit(0);       // do not wait on anything the pooled HTTP client leaves behind
