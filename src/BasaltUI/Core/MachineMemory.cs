using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace BasaltUI.Core;

/// <summary>
/// Memory read the way Windows reads it, so that a number in this app is the number on the Task Manager
/// page next to it.
///
/// RAM comes from <c>GlobalMemoryStatusEx</c>: its usable total and available physical memory are exactly
/// what Task Manager's memory page divides into "in use" and "available", and `GetPhysicallyInstalledSystemMemory`
/// adds the "installed" line under it (a 64 GB machine usually reports 61.something GB usable, and that
/// difference is a fact to show, not a bug to hide).
///
/// GPU memory is two halves, taken from the two places the GPU page of Task Manager reads. The card's own
/// memory comes from DXGI — <c>IDXGIAdapter2::GetDesc2</c>, whose <c>DedicatedVideoMemory</c> is the graphics
/// memory of the card — and what fills it comes from the <c>GPU Adapter Memory</c> performance counter set,
/// which is the counter Task Manager samples for its "Dedicated GPU memory" bar. DXGI's own
/// <c>QueryVideoMemoryInfo</c> is deliberately <em>not</em> used for the contents: its <c>CurrentUsage</c> is
/// what the calling process has committed, so for this app — which draws no graphics and holds nothing — it
/// reads 0 while the engine below it holds 31 GB, and a bar built on it would be a polite lie.
///
/// That difference is also why an NVML number (nvidia-smi, and the engine's own <c>/stats</c>) can never
/// match Task Manager: it counts the driver's own carve-out on top and reports a bigger total. So NVML stays
/// only as a labelled fallback when either OS half cannot be reached, and the two are never mixed into one
/// figure — a used figure from one source inside a total from another is how two honest programs end up
/// looking like they disagree.
///
/// Both APIs are reached by hand — DXGI through vtable slots, PDH through plain exports — rather than a COM
/// reference or a NuGet interop assembly, so the app still ships with one package. The slot numbers and
/// struct offsets come from the order things are declared in the public headers (dxgi.h, pdh.h). Everything
/// is guarded: a step that fails is named in the diagnose trace, and a figure that is not plausible makes the
/// reader fall back instead of showing it.
/// </summary>
public static class MachineMemory
{
    private const long Kib = 1024L;
    private const long Mib = Kib * 1024;
    private const long Gib = Mib * 1024;

    // vtable positions, counting from IUnknown at 0.
    private const int SlotRelease = 2;
    private const int SlotEnumAdapters1 = 12;   // IUnknown 3 + IDXGIObject 4 + IDXGIFactory 5
    private const int SlotGetDesc2 = 11;        // + IDXGIAdapter 3 (EnumOutputs, GetDesc, CheckInterfaceSupport) + GetDesc1

    // Byte offsets inside DXGI_ADAPTER_DESC2 as it arrives on x64: a 128-character wide name, four ids,
    // then the three size_t memory figures and the LUID — which is the plain 8-byte { LowPart, HighPart },
    // not the 12-byte form with Data1/Data2, so it starts right after SharedSystemMemory. The trace proves
    // them: the card name and the described 32187 MiB land where these say, and the LowPart matches the
    // luid the usage counter names this card by.
    private const int OffsetDedicatedVideoMemory = 272;
    private const int OffsetAdapterLuidLow = 296;
    private const int OffsetAdapterLuidHigh = 300;

    // Performance counters (pdh.h, pdhmsg.h). PDH_FMT_LARGE asks for a 64-bit integer, PDH_FMT_NOCAP100
    // stops PDH clipping a value to a ceiling the way it does with percentages, and PDH_MORE_DATA is the
    // answer to "how big a buffer do you need" — a step of the call, not a failure of it. (It is 0x800007D2;
    // 0x800007D3 is ITEM_NOT_VALIDATED, one away, and mistyping it turns a working read into a silent
    // fallback, which is why the raw code is printed in the trace.)
    private const uint PdhFmtLarge = 0x00000400;
    private const uint PdhFmtNoCap100 = 0x00008000;
    private const int PdhMoreData = unchecked((int)0x800007D2);

    // Resolved by PDH in the current Windows display language, which is a known limit: a localized Windows
    // would answer "no such counter" here and the reader would fall back to nvidia-smi, labelled as such —
    // the alternative, translating the object and counter names through PdhLookupPerfNameByIndex first, is a
    // question worth answering on a machine where it actually fails, not a guess made in advance.
    private const string PdhDedicatedUsagePath = "\\GPU Adapter Memory(*)\\Dedicated Usage";

    public sealed record Ram(long Installed, long Usable, long InUse, long Available)
    {
        public bool Known => Usable > 0;
    }

    /// <summary>
    /// The card's own memory. <c>Total</c> is what the card carries for graphics, <c>InUse</c> what sits in
    /// it right now — and both halves come from one source, because a figure from NVML divided into an OS
    /// figure is exactly the kind of disagreement this class exists to end. <c>Known</c> is only true when
    /// both halves are real, so callers can fall back (to the engine's own rows) instead of drawing a bar
    /// that is half empty because one side failed.
    /// </summary>
    public sealed record Vram(string? Name, long Total, long InUse, bool Known, string Source)
    {
        public long Free => Math.Max(0, Total - InUse);

        public static Vram Unknown(string source) => new(null, 0, 0, false, source);
    }

    /// <summary>The one adapter DXGI enumerated, with the LUID that names it in the performance counters.</summary>
    private sealed record Adapter(string? Name, long Dedicated, uint LuidLow, uint LuidHigh);

    public sealed record Sample(Ram Memory, Vram Video);

    /// <summary>One reading of the whole machine. Cheap enough to call on every poll beat.</summary>
    public static Sample Read(int gpuIndex = 0) => new(ReadRam(), ReadVram(gpuIndex));

    // ---- RAM ---------------------------------------------------------------------------------------------------

    public static Ram ReadRam()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status))
            return new Ram(0, 0, 0, 0);

        var usable = (long)status.TotalPhys;
        var available = (long)status.AvailPhys;

        long installed = 0;
        try
        {
            if (GetPhysicallyInstalledSystemMemory(out var kilobytes) && kilobytes > 0)
                installed = (long)kilobytes * Kib;
        }
        catch
        {
            // the usable figure is enough on its own; the installed line is a nice-to-have
        }
        return new Ram(installed, usable, Math.Max(0, usable - available), available);
    }

    // ---- VRAM --------------------------------------------------------------------------------------------------

    /// The GPU figure the UI shows: the card's own memory from DXGI, and what fills it from the very
    /// performance counter Task Manager samples for its "Dedicated GPU memory" row.
    ///
    /// Two sources, and they are the two the GPU page uses: DXGI for the card, the <c>GPU Adapter Memory</c>
    /// counter set for the contents. DXGI's own <c>QueryVideoMemoryInfo</c> cannot be used for the contents —
    /// its CurrentUsage is what the <em>calling process</em> has committed, which for this app is nothing at
    /// all while the engine holds 31 GB, so a bar built on it would read 0. NVML (nvidia-smi) stays as the
    /// fallback for the whole pair when either half is unreachable, labelled as such in every string that
    /// shows it.
    public static Vram ReadVram(int gpuIndex = 0) => ReadVramDetailed(gpuIndex).Value;

    /// <summary>Only the NVML view, for comparison — its totals include the driver's own carve-out, so they
    /// are larger than the card figure the GPU page and this app show.</summary>
    public static Vram ReadVramFromNvidiaSmi(int gpuIndex = 0) => ReadVramNvidiaSmi(Math.Max(0, gpuIndex));

    /// <summary>The walk with its step-by-step results, so the probe can say exactly where it stopped.</summary>
    public static string DiagnoseVram(int gpuIndex = 0) => ReadVramDetailed(gpuIndex).Trace;

    private static (Vram Value, string Trace) ReadVramDetailed(int gpuIndex)
    {
        var adapter = TryReadAdapter(Math.Max(0, gpuIndex), out var trace);
        if (adapter is null)
            return (ReadVramNvidiaSmi(gpuIndex), trace + " -> no adapter from DXGI, using nvidia-smi");

        var used = TryReadUsagePdh(adapter.LuidLow, adapter.LuidHigh, out var pdhTrace);
        trace += pdhTrace;
        if (used is long bytes)
            return (new Vram(adapter.Name, adapter.Dedicated, Math.Min(bytes, adapter.Dedicated), true, "DXGI+PDH"), trace);

        // No usage figure of the GPU page's kind: keep the pair consistent by taking NVML's pair wholesale.
        return (ReadVramNvidiaSmi(gpuIndex), trace + " -> no usage counter, using nvidia-smi for both");
    }

    private static Adapter? TryReadAdapter(int gpuIndex, out string trace)
    {
        var log = new StringBuilder("dxgi");
        var adapter = IntPtr.Zero;
        var factory = IntPtr.Zero;
        var descBuffer = IntPtr.Zero;
        try
        {
            if (!NativeLibrary.TryLoad("dxgi.dll", typeof(MachineMemory).Assembly, DllImportSearchPath.SafeDirectories, out var dxgi))
            {
                log.Append(": load failed");
                return null;
            }
            if (!NativeLibrary.TryGetExport(dxgi, "CreateDXGIFactory1", out var createAt))
            {
                log.Append(": no CreateDXGIFactory1");
                return null;
            }

            var create = Marshal.GetDelegateForFunctionPointer<CreateFactory>(createAt);
            // IID_IDXGIFactory1, copied from the DXGI header. Get one digit of this wrong and
            // CreateDXGIFactory1 answers E_NOINTERFACE (0x80004002) even though dxgi.dll loaded fine —
            // which is exactly the bug this comment exists to prevent.
            var factoryIid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
            var hrCreate = create(ref factoryIid, out factory);
            log.Append($" create=0x{hrCreate:X8}");
            if (hrCreate != 0 || factory == IntPtr.Zero)
                return null;

            // DXGI lists the display-attached card first, which on the single-card machines basalt is built
            // for is the same card nvidia-smi calls 0. Walk to the configured one.
            var enumerate = Slot<EnumAdapters1>(factory, SlotEnumAdapters1);
            for (var wanted = 0; wanted <= gpuIndex; wanted++)
            {
                var candidate = IntPtr.Zero;
                var hrEnum = enumerate(factory, (uint)wanted, out candidate);
                log.Append($" enum[{wanted}]=0x{hrEnum:X8}");
                if (hrEnum != 0 || candidate == IntPtr.Zero)
                    break;
                if (adapter != IntPtr.Zero)
                    Slot<Release>(adapter, SlotRelease)(adapter);
                adapter = candidate;
            }
            if (adapter == IntPtr.Zero)
                return null;

            // The description gives the card's name and, before anything is shown, proves the slot
            // arithmetic: a readable "NVIDIA GeForce …" means the offsets line up with the real struct.
            descBuffer = Marshal.AllocHGlobal(4096);
            var hrDesc = Slot<GetDesc2>(adapter, SlotGetDesc2)(adapter, descBuffer);
            if (hrDesc != 0)
            {
                log.Append($" desc=0x{hrDesc:X8}");
                return null;
            }
            var name = Marshal.PtrToStringUni(descBuffer, 128)?.Split('\0', 2)[0];
            var dedicated = Marshal.ReadInt64(descBuffer, OffsetDedicatedVideoMemory);
            // LUID: LowPart then HighPart. The performance-counter instance for an adapter is named after
            // this pair, which is how the usage figure below is matched to this card. The three uints that
            // sit before it are printed too, because a struct offset guessed from a header is exactly the
            // kind of thing that needs to be readable in a trace, not trusted.
            var luidLow = unchecked((uint)Marshal.ReadInt32(descBuffer, OffsetAdapterLuidLow));
            var luidHigh = unchecked((uint)Marshal.ReadInt32(descBuffer, OffsetAdapterLuidHigh));
            log.Append($" desc=0x00000000 dedicated={dedicated / Mib} MiB" +
                       $" words[288..312]={unchecked((uint)Marshal.ReadInt32(descBuffer, 288)):X8}," +
                       $"{unchecked((uint)Marshal.ReadInt32(descBuffer, 292)):X8}," +
                       $"{luidLow:X8},{luidHigh:X8}");

            if (dedicated < 128 * Mib || dedicated > 4 * 1024 * Gib)
            {
                log.Append(" implausible");
                return null;
            }
            return new Adapter(string.IsNullOrWhiteSpace(name) ? null : name.Trim(), dedicated, luidLow, luidHigh);
        }
        catch (Exception e)
        {
            log.Append($" threw {e.GetType().Name}");
            return null;
        }
        finally
        {
            if (adapter != IntPtr.Zero)
                Slot<Release>(adapter, SlotRelease)(adapter);
            if (factory != IntPtr.Zero)
                Slot<Release>(factory, SlotRelease)(factory);
            if (descBuffer != IntPtr.Zero)
                Marshal.FreeHGlobal(descBuffer);
            trace = log.ToString();
        }
    }

    // One query, kept open: this runs on every poll beat, so rebuilding it and resolving the counter path
    // every second would be the most expensive thing the beat does.
    private static readonly object PdhLock = new();
    private static IntPtr pdhQuery;
    private static IntPtr pdhCounter;
    private static string? pdhPath;

    /// <summary>
    /// What one card's own memory is holding, from <c>\GPU Adapter Memory(luid_…)\Dedicated Usage</c> — the
    /// counter set Task Manager's GPU page is built on, so this is the figure its bar grows with.
    ///
    /// The set has one instance per card, each named after that card's LUID, so the card DXGI enumerated is
    /// picked out by letting PDH expand <c>(*)</c> for the counter and keeping the path whose instance
    /// carries this LUID. If no instance carries it, nothing is reported rather than a guess: a figure for
    /// the wrong card would look exactly like a figure for the right one.
    /// </summary>
    private static long? TryReadUsagePdh(uint luidLow, uint luidHigh, out string trace)
    {
        var log = new StringBuilder("| pdh");
        try
        {
            lock (PdhLock)
            {
                if (!EnsureCounter(luidLow, luidHigh, log))
                    return null;

                // A PDH_FMT_COUNTERVALUE is 16 bytes; the rest is room for a driver that writes a longer
                // version of the struct than the one whose layout was read off the header.
                var value = Marshal.AllocHGlobal(64);
                try
                {
                    var hrCollect = PdhCollectQueryData(pdhQuery);
                    var hrValue = PdhGetFormattedCounterValue(pdhCounter, PdhFmtLarge | PdhFmtNoCap100, IntPtr.Zero, value);
                    if (hrCollect != 0 || hrValue != 0)
                    {
                        log.Append($" collect=0x{hrCollect:X8} value=0x{hrValue:X8}");
                        DropQuery();
                        return null;
                    }
                    // PDH tags the reading itself: 0 "valid data", 1 "new data" (valid, first time the
                    // instance was seen — which is what a freshly opened query reports).
                    var status = unchecked((uint)Marshal.ReadInt32(value, 0));
                    var bytes = Marshal.ReadInt64(value, 8);      // CStatus, then the value union, 64-bit aligned
                    log.Append($" status=0x{status:X8} bytes={bytes / Mib} MiB");
                    if (status != 0 && status != 1)
                    {
                        DropQuery();
                        return null;
                    }
                    if (bytes < 0 || bytes > 512 * Gib)
                    {
                        log.Append(" implausible");
                        return null;
                    }
                    return bytes;
                }
                finally
                {
                    Marshal.FreeHGlobal(value);
                }
            }
        }
        catch (Exception e)
        {
            log.Append($" threw {e.GetType().Name}");
            DropQuery();
            return null;
        }
        finally
        {
            trace = log.ToString();
        }
    }

    /// <summary>Have a query that counts this card's memory, opening one only when the card asked for is not
    /// the card the open query is about.</summary>
    private static bool EnsureCounter(uint luidLow, uint luidHigh, StringBuilder log)
    {
        if (pdhQuery != IntPtr.Zero && pdhPath is not null && Matches(pdhPath, luidLow, luidHigh))
            return true;

        var wanted = FindUsagePath(luidLow, luidHigh, log);
        if (wanted is null)
            return false;
        if (pdhQuery != IntPtr.Zero && pdhPath == wanted)
            return true;

        DropQuery();
        var hrOpen = PdhOpenQueryW(IntPtr.Zero, IntPtr.Zero, out var query);
        if (query == IntPtr.Zero)
        {
            log.Append($" open=0x{hrOpen:X8}");
            return false;
        }
        // The path arrived out of PDH's own expansion, so its names are already in this Windows' language —
        // handing it to the English-adding call would be the wrong one to ask.
        var hrAdd = PdhAddCounterW(query, wanted, IntPtr.Zero, out var counter);
        if (counter == IntPtr.Zero)
        {
            log.Append($" add=0x{hrAdd:X8}");
            _ = PdhCloseQuery(query);        // nothing useful to do with a query that failed to close
            return false;
        }
        log.Append($" counter={Instance(wanted)}");
        pdhQuery = query;
        pdhCounter = counter;
        pdhPath = wanted;
        return true;
    }

    /// <summary>The concrete path for this card's usage counter, out of the instances PDH itself lists.</summary>
    private static string? FindUsagePath(uint luidLow, uint luidHigh, StringBuilder log)
    {
        var chars = 8192;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(chars * 2);
            try
            {
                // Zeroed, then read as a block of the length promised: the answer is a list of wide strings
                // ending in a double null, and a bounded block read leaves no string to overrun the buffer.
                Marshal.Copy(new byte[chars * 2], 0, buffer, chars * 2);
                var length = chars;                       // this API counts characters, not bytes
                var hr = PdhExpandWildCardPathW(IntPtr.Zero, PdhDedicatedUsagePath, buffer, ref length, 0);
                if (hr == PdhMoreData)
                {
                    chars = Math.Clamp(length, chars + 1024, 262_144);
                    continue;
                }
                if (hr != 0)
                {
                    log.Append($" expand=0x{hr:X8}");
                    return null;
                }

                var paths = ReadList(buffer, chars);
                if (paths.Count == 0)
                {
                    log.Append(" expand=none");
                    return null;
                }
                var match = paths.Find(p => Matches(p, luidLow, luidHigh));
                log.Append(match is not null ? $" expand={paths.Count}"
                    : $" expand={paths.Count} of {string.Join(",", paths.ConvertAll(Instance))} carries no 0x{luidLow:X8}/0x{luidHigh:X8}");
                return match;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        log.Append($" expand=too many instances to fit in {chars}");
        return null;
    }

    private static List<string> ReadList(IntPtr buffer, int chars) =>
        (Marshal.PtrToStringUni(buffer, chars) ?? string.Empty)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();

    /// <summary>The instance part of a counter path — what is inside the parentheses — for the trace.</summary>
    private static string Instance(string path)
    {
        var open = path.LastIndexOf('(');
        var close = path.LastIndexOf(')');
        return open >= 0 && close > open ? path[(open + 1)..close] : path;
    }

    /// <summary>Forget the query so the next read builds it again — a machine that gains or loses a card, or
    /// a counter set that was briefly unavailable, should not be stuck for the life of the process.</summary>
    private static void DropQuery()
    {
        lock (PdhLock)
        {
            if (pdhQuery != IntPtr.Zero)
                _ = PdhCloseQuery(pdhQuery);
            pdhQuery = IntPtr.Zero;
            pdhCounter = IntPtr.Zero;
            pdhPath = null;
        }
    }

    /// <summary>Instance names look like <c>luid_0x00000000_0x0001166a_phys_0</c>. Both halves of the LUID
    /// are matched in either order, because the pair's order in the name is a driver detail, not a promise.</summary>
    private static bool Matches(string instance, uint luidLow, uint luidHigh)
    {
        var parts = instance.Split('_');
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            if (!unsigned(parts[i], out var a) || !unsigned(parts[i + 1], out var b))
                continue;
            if ((a == luidLow && b == luidHigh) || (a == luidHigh && b == luidLow))
                return true;
        }
        return false;
    }

    private static bool unsigned(string text, out uint value)
    {
        value = 0;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
               uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    private static Vram ReadVramNvidiaSmi(int gpuIndex)
    {
        try
        {
            using var app = Process.Start(new ProcessStartInfo("nvidia-smi",
                "--query-gpu=name,memory.total,memory.used --format=csv,noheader,nounits")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (app is null)
                return Vram.Unknown("nvidia-smi");
            var text = app.StandardOutput.ReadToEnd();
            app.WaitForExit(3000);

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0)
                return Vram.Unknown("nvidia-smi");
            var line = lines[Math.Min(Math.Max(0, gpuIndex), lines.Length - 1)];
            var parts = line.Split(',');
            if (parts.Length < 3 || !long.TryParse(parts[^1].Trim(), out var used) ||
                !long.TryParse(parts[^2].Trim(), out var total))
                return Vram.Unknown("nvidia-smi");

            return new Vram(parts[..^2].Length > 0 ? string.Join(",", parts[..^2]).Trim() : null,
                            total * Mib, Math.Min(used * Mib, total * Mib), true, "nvidia-smi");
        }
        catch
        {
            return Vram.Unknown("nvidia-smi");
        }
    }

    // ---- interop -----------------------------------------------------------------------------------------------

    private static T Slot<T>(IntPtr comObject, int index) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, index * IntPtr.Size));
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateFactory(ref Guid riid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EnumAdapters1(IntPtr self, uint index, out IntPtr adapter);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDesc2(IntPtr self, IntPtr desc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint Release(IntPtr self);

    // PDH, the performance-counter API. ExactSpelling plus the W entry points: a counter path is wide text,
    // and the ANSI fallback would quietly mangle the instance names it has to match against.
    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern int PdhOpenQueryW(IntPtr dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int PdhAddCounterW(IntPtr query, string counterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int PdhExpandWildCardPathW(IntPtr dataSource, string wildCardPath,
                                                     IntPtr pathList, ref int characterCount, uint flags);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern int PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr dataType, IntPtr value);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern int PdhCloseQuery(IntPtr query);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKB);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx status);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}
