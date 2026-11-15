using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace BasaltUI.Core;

/// <summary>
/// What loading one model would look like on this machine, before it is loaded. Basalt tiers a
/// mixture-of-experts model: the dense weights, the KV cache and the vision projector sit in VRAM,
/// the routed experts fill what VRAM is left, then what <c>resident-experts</c> keeps in system RAM,
/// and anything beyond that streams from the SSD on demand. The engine itself decides the exact split
/// when it loads — the <c>[server] auto</c> line in its output carries the final numbers — so this is
/// an honest estimate: exact where the file tells us, rounded where only the docs do.
///
/// The numbers it builds on:
///  - a <c>.basalt</c> file opens with a 64 KiB header whose section table names every part of the
///    model (dense, experts, vision, drafter…), so the VRAM side and the expert pool are read, not
///    guessed. A plain <c>.gguf</c> gets the typical MoE split (≈ 92 % experts) and says so.
///  - KV bytes per context cell, from basalt's own docs: Flash-Next int8 ≈ 1056, k8v4 ≈ 816,
///    fp16 ≈ 2112 (its default); DeepSeek fp8 ≈ 920 (its default), fp16 ≈ 3200. Sized by max-context ×
///    parallel — with one exception the docs are explicit about: above 32,768 cells the KV is elastic and
///    only the first 32,768 cells of a slot are backed at start, more being borrowed from the expert cache
///    while a long request is in flight (which is why a 262,144-cell context does not ask for 277 GB of
///    VRAM). <c>kv-resident</c> draws the line by hand instead: its cells stay in VRAM, the rest of the
///    context sits in pinned RAM. <c>vram-elastic</c> and a layer split switch elasticity off too, and the
///    whole context is then backed at start — not modelled here, because only the engine knows the split it
///    settled on.
///  - the engine keeps <c>vram-reserve-mib</c> (700 by default) of VRAM aside and, with
///    <c>resident-experts</c> on, budgets RAM down to <c>ram-reserve-mib</c> (2048) above free; since 0.3.2
///    a tier that leaves experts in the file also leaves the file cache at least 4 GiB of that RAM.
///  - a dynamic vision projector is worth roughly 2.2 GiB of working VRAM on top of its bytes.
/// </summary>
public static class LoadPreview
{
    private const long Mib = 1024L * 1024;
    private const long Gib = Mib * 1024;
    private const int HeaderBytes = 64 << 10;
    private const int MaxSections = (HeaderBytes - 64) / 128;   // kMaxSections in model_format.hpp
    private const long VramReserveBytes = 700 * Mib;             // vram-reserve-mib, the documented default
    private const long RamReserveBytes = 2048 * Mib;             // ram-reserve-mib, the documented default
    private const long VisionWorkingBytes = (long)(2.2 * Gib);   // docs' estimate for a dynamic projector
    private const long ElasticKvCells = 32_768;                   // cells of a slot backed at start above this context
    private const long FileCacheFloorBytes = 4 * Gib;             // RAM the file cache keeps when experts stay on disk

    // ---- what the outside world sees -------------------------------------------------------------------------

    /// <summary>The machine as Task Manager sees it: <c>VramTotal</c> is the card's own memory as DXGI
    /// describes it, <c>VramFree</c> what is left of it after the counter for what fills it, <c>RamTotal</c>
    /// the usable physical memory and <c>RamFree</c> the available part of it. <c>VramSource</c> names which
    /// pair was used, because "the OS" and "NVML" are two different measurements of one card.</summary>
    public sealed record Hardware(long VramFree, long VramTotal, bool VramKnown,
                                  long RamFree, long RamTotal, long DiskFree,
                                  string? VramName = null, string VramSource = "DXGI+PDH", long RamInstalled = 0);

    public sealed record Options(string Name, long FileBytes, string? Kv, long MaxContext, long Parallel,
                                 string? Vision, bool ResidentExperts, int GpuIndex, string? DiskRoot,
                                 long KvResidentCells = 0);

    public sealed record Plan(string Title, IReadOnlyList<(string Label, string Value)> Rows,
                              string Verdict, string Note, bool Ok);

    /// <summary>A local file: the section table is read, so the split is as exact as the file allows.</summary>
    public static Plan ForFile(string path, Options o)
    {
        try
        {
            var info = new FileInfo(path);
            var hw = Measure(info.Directory?.Root.FullName ?? o.DiskRoot, o.GpuIndex);
            var split = ReadSplit(path, info.Length);
            return Build(o with { FileBytes = info.Length }, hw, split, "this file");
        }
        catch (Exception e)
        {
            return new Plan(o.Name, [], "preview unavailable", Net.Explain(e), false);
        }
    }

    /// <summary>A file still on Hugging Face: only the published size is known, so the split is the
    /// typical MoE estimate and the note says exactly that.</summary>
    public static Plan ForRemote(string name, long size, Options o)
    {
        var hw = Measure(o.DiskRoot, o.GpuIndex);
        return Build(o with { Name = name, FileBytes = size }, hw, null, "this pack");
    }

    // ---- the plan --------------------------------------------------------------------------------------------

    private static Plan Build(Options o, Hardware hw, Split? split, string subject)
    {
        var deepseek = o.Name.Contains("deepseek", StringComparison.OrdinalIgnoreCase);
        var kvBytes = KvBytes(o.Kv, deepseek);
        var context = Math.Max(0, o.MaxContext);
        var parallel = Math.Max(1, o.Parallel);
        var residentCells = Math.Max(0, o.KvResidentCells);

        // Above 32,768 cells the KV is elastic, and the docs say what that costs at start: the first 32,768
        // cells of each slot, no more — a longer request borrows the expert cache's VRAM for as long as it
        // runs and gives it back. kv-resident draws the line by hand instead, and elasticity is off then.
        var elastic = residentCells == 0 && context > ElasticKvCells;
        var vramCells = elastic ? ElasticKvCells
                      : residentCells > 0 ? Math.Min(residentCells, context)
                      : context;
        var ramCells = residentCells > 0 ? context - vramCells : 0;
        var kvInVram = vramCells * kvBytes * parallel;
        var kvInRam = ramCells * kvBytes * parallel;

        long dense, experts, visionBytes;
        bool exact;
        if (split is { Parsed: true })
        {
            exact = true;
            dense = split.Dense;
            experts = split.Experts;
            visionBytes = split.Vision;
        }
        else
        {
            // A gguf has no section table; the routed experts are the overwhelming share of a MoE pack.
            exact = false;
            dense = o.FileBytes * 8 / 100;
            experts = o.FileBytes * 92 / 100;
            visionBytes = 0;
        }

        var visionOn = !string.Equals(o.Vision, "off", StringComparison.OrdinalIgnoreCase) &&
                       (o.Vision is { Length: > 0 } || visionBytes > 0);
        var visionTotal = visionOn ? visionBytes + (IsDynamicVision(o.Vision, visionBytes) ? VisionWorkingBytes : 0) : 0;

        var vramBudget = hw.VramKnown ? Math.Max(0, hw.VramFree - VramReserveBytes) : 0;
        var fixedVram = kvInVram + dense + visionTotal;
        var roomInVram = Math.Max(0, vramBudget - fixedVram);
        var expertsInVram = Math.Min(experts, roomInVram);
        var rest = experts - expertsInVram;

        // Page-locked experts, then the room the file cache keeps for itself when some of them stay on disk.
        var ramBudget = o.ResidentExperts ? Math.Max(0, hw.RamFree - RamReserveBytes - kvInRam) : 0;
        var expertsInRam = Math.Min(rest, ramBudget);
        if (expertsInRam < rest)
        {
            ramBudget = Math.Max(0, ramBudget - FileCacheFloorBytes);
            expertsInRam = Math.Min(rest, ramBudget);
        }
        var fromDisk = rest - expertsInRam;

        var bytes = BasaltLocator.HumanBytes;
        var rows = new List<(string, string)>
        {
            ("file", $"{o.Name} — {bytes(o.FileBytes)}"),
            ("dense + attention weights", $"{bytes(dense)} in VRAM" + (exact ? "" : " (est.)")),
            ("routed experts", bytes(experts) + (exact ? "" : " (est.)")),
        };

        var kvName = o.Kv is { Length: > 0 } k ? k.Trim() : deepseek ? "fp8 default" : "fp16 default";
        var kvLabel = $"KV cache ({context:N0} cells × {parallel} slot{(parallel == 1 ? "" : "s")}, {kvName})";
        rows.Add(context <= 0
            ? (kvLabel, "context not set")
            : kvInRam > 0
                ? (kvLabel, $"{bytes(kvInVram)} in VRAM, {bytes(kvInRam)} in pinned RAM (kv-resident)")
                : elastic
                    ? (kvLabel, $"{bytes(kvInVram)} at start — above 32,768 cells the KV is elastic, so a slot starts with 32,768 cells and borrows the expert cache's VRAM while a long request runs")
                    : (kvLabel, $"{bytes(kvInVram)} in VRAM"));
        if (visionOn)
            rows.Add(("vision projector", bytes(visionTotal) +
                      (IsDynamicVision(o.Vision, visionBytes) ? " (dynamic working set)" : "")));

        rows.Add(("experts where they can be read fastest",
                  expertsInVram > 0 && expertsInRam > 0
                      ? $"≈{bytes(expertsInVram)} in VRAM, ≈{bytes(expertsInRam)} in RAM"
                      : expertsInVram > 0 ? $"≈{bytes(expertsInVram)} in VRAM"
                      : expertsInRam > 0 ? $"≈{bytes(expertsInRam)} in RAM"
                      : "none fit"));

        // The two lines the user can check against Task Manager, in the same words and from the same source.
        rows.Add(("GPU memory right now", hw.VramKnown
            ? $"{hw.VramName ?? "the card"} — {bytes(hw.VramTotal - hw.VramFree)} in use of {bytes(hw.VramTotal)}, {bytes(hw.VramFree)} left"
            : $"{hw.VramSource} did not answer"));
        if (hw.RamTotal > 0)
            rows.Add(("RAM right now", $"{bytes(hw.RamTotal - hw.RamFree)} in use of {bytes(hw.RamTotal)} usable" +
                      (hw.RamInstalled > hw.RamTotal ? $" ({bytes(hw.RamInstalled)} installed)" : "")));

        rows.Add(("VRAM it would take", hw.VramKnown
            ? $"≈{bytes(fixedVram + expertsInVram)} of the {bytes(hw.VramFree)} free right now"
            : "GPU not measured"));
        if (o.ResidentExperts)
            rows.Add(("RAM it would take", $"≈{bytes(expertsInRam)} of the {bytes(hw.RamFree)} free right now"));
        rows.Add(("from the SSD", fromDisk > 0 ? $"≈{bytes(fromDisk)} streams on demand" : "nothing"));

        var verdict = !hw.VramKnown
            ? $"nothing reported the GPU ({hw.VramSource}) — this preview is for the NVIDIA cards basalt runs on"
            : fromDisk > 0
                ? $"~{bytes(fromDisk)} of experts would stream from the SSD — replies slow down"
                : expertsInRam > 0
                    ? $"fits — ≈{bytes(expertsInVram)} of experts in VRAM, ≈{bytes(expertsInRam)} in RAM"
                    : "fits — everything, experts included, in VRAM";

        var note = $"memory read the way Task Manager reads it: the card's own memory from DXGI and the counter its GPU page samples for what fills it, Windows' available physical memory for the RAM. " +
                   "The engine sets the final split when it loads — the [server] auto line in its output has the exact numbers";
        if (!exact)
            note = "split estimated from the file size; a .basalt pack states it exactly. " + note;
        if (hw.VramKnown && hw.VramTotal > 0 && hw.VramFree < hw.VramTotal * 9 / 10)
            note = "measured against VRAM free right now — anything else using the GPU lowers this. " + note;

        return new Plan(subject, rows, verdict, note, true);
    }

    private static bool IsDynamicVision(string? vision, long visionBytes) =>
        visionBytes > 0 || vision is null || vision.Length == 0 ||
        vision.Contains("dynamic", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// KV bytes per context cell, from basalt's documentation, per family and kv setting. Left unset the
    /// engine keeps its own default, which is fp16 for Flash-Next and fp8 for DeepSeek — worth stating,
    /// because between those two the same context doubles or halves in size.
    /// </summary>
    private static long KvBytes(string? kv, bool deepseek) => (kv?.Trim().ToLowerInvariant()) switch
    {
        "fp16" or "bf16" => deepseek ? 3200 : 2112,
        "k8v4" when !deepseek => 816,
        "int8" or "q4_0" when !deepseek => 1056,
        "fp8" when deepseek => 920,
        _ => deepseek ? 920 : 2112,          // the documented defaults
    };

    // ---- the .basalt section table -----------------------------------------------------------------------------

    private sealed class Split
    {
        public long Dense;      // dense pool, gguf tensors, hyper-connections, drafter: always mapped in VRAM
        public long Experts;    // routed experts and their layout: tiered across VRAM / RAM / SSD
        public long Vision;     // the projector and whatever else the vision sidecar reads
        public long Other;      // meta, tokenizer, engram: mapped, never more than a few MiB
        public bool Parsed;
    }

    private static readonly byte[] Magic = "BASALTMF"u8.ToArray();

    /// <summary>
    /// Reads the section table from the 64 KiB header (model_format.hpp: FileHeader 64 B, then 128-byte
    /// entries — NUL-padded name, offset, bytes). Every value is checked against the real file length;
    /// anything unexpected means no preview rather than a wrong preview.
    /// </summary>
    private static Split? ReadSplit(string path, long fileLength)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < HeaderBytes)
                return null;
            var buf = new byte[HeaderBytes];
            stream.ReadExactly(buf);

            var span = buf.AsSpan();
            if (!span[..8].SequenceEqual(Magic) || BinaryPrimitives.ReadUInt32LittleEndian(span[8..]) != 2)
                return null;
            var count = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
            if (count > MaxSections)
                return null;

            var split = new Split();
            for (var i = 0; i < count; i++)
            {
                var at = 64 + i * 128;
                var end = span[at..(at + 64)].IndexOf((byte)0);
                var name = System.Text.Encoding.UTF8.GetString(span[at..(at + (end < 0 ? 64 : end))]);
                var offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(span[(at + 64)..]);
                var bytes = (long)BinaryPrimitives.ReadUInt64LittleEndian(span[(at + 72)..]);
                if (offset > fileLength || bytes > fileLength - offset)
                    return null;

                switch (name)
                {
                    case "dense" or "dense.index" or "gguf" or "hc8" or "engram" or "dspark/gguf" or "mtp/dense" or "mtp/dense.index":
                        split.Dense += bytes;
                        break;
                    case "experts" or "experts.layout" or "mtp/experts":
                        split.Experts += bytes;
                        break;
                    default:
                        if (name.StartsWith("vision/", StringComparison.Ordinal))
                            split.Vision += bytes;
                        else
                            split.Other += bytes;
                        break;
                }
            }
            split.Parsed = true;
            return split;
        }
        catch
        {
            return null;    // a locked or half-written file previews from the size alone
        }
    }

    // ---- the machine -------------------------------------------------------------------------------------------

    /// <summary>The machine as Task Manager reports it: the card's budget and usage from DXGI (nvidia-smi if
    /// that is all there is), usable and available RAM from Windows, and free disk on the models drive.</summary>
    public static Hardware Measure(string? diskRoot, int gpuIndex = 0)
    {
        var ram = MachineMemory.ReadRam();
        var gpu = MachineMemory.ReadVram(gpuIndex);
        long disk = 0;
        try
        {
            var root = diskRoot is { Length: > 0 } ? Path.GetPathRoot(Path.GetFullPath(diskRoot)) : null;
            if (root is not null)
                disk = new DriveInfo(root).AvailableFreeSpace;
        }
        catch
        {
            // no drive info is not worth a broken preview over
        }
        return new Hardware(gpu.Free, gpu.Total, gpu.Known, ram.Available, ram.Usable, disk,
                            gpu.Name, gpu.Source, ram.Installed);
    }
}
