using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BasaltUI.Core;

/// <summary>One basalt field: the documented key, where it lives, what it takes, and what it means.</summary>
public sealed record SettingDef(string Key, string[] Scope, string Type, JsonNode? Default, string Model,
                                string Ui, string Group, string Summary, double? Min, double? Max, string[] Values)
{
    public bool IsCommandOnly => Scope.Contains("command_line_only");
    public bool IsNumeric => Type is "int" or "mib" or "gib" or "seconds" or "float";
    public bool IsList => Type is "list" or "multi";
    public bool IsPath => Type is "path" or "dir";

    public string ScopeText => string.Join(", ", Scope.Select(s => s switch
    {
        "server" => "server",
        "engine" => "engine",
        "entry" => "model entry",
        "command_line_only" => "command line only",
        _ => s,
    }));

    public string DefaultText => Default is null
        ? "not set"
        : Default is JsonValue v && v.TryGetValue(out string? s) ? s : Default.ToJsonString();

    /// <summary>What a tooltip shows: the doc summary, the default, the allowed values, any range, the model limit.</summary>
    public string Help
    {
        get
        {
            var parts = new List<string> { Summary, "Default: " + DefaultText };
            if (Values.Length > 0)
                parts.Add("Values: " + string.Join(", ", Values));
            if (Min is double lo && Max is double hi)
                parts.Add($"Range: {lo:0.###} to {hi:0.###}");
            else if (Min is double l)
                parts.Add($"Minimum: {l:0.###}");
            else if (Max is double h)
                parts.Add($"Maximum: {h:0.###}");
            if (Model != "both")
                parts.Add(Model == "flashnext" ? "Flash-Next only" : "DeepSeek only");
            return string.Join("\n", parts);
        }
    }
}

/// <summary>
/// Every basalt setting, read once from the catalog compiled out of the project's docs and serve/config.py.
/// The settings window builds its fields from this, so a new basalt option becomes editable by editing JSON.
/// </summary>
public sealed class SettingsCatalog
{
    private static SettingsCatalog? shared;
    public static SettingsCatalog Shared => shared ??= Load();

    public IReadOnlyList<SettingDef> All { get; private init; } = [];
    public IReadOnlyList<string> Notes { get; private init; } = [];
    private IReadOnlyDictionary<string, SettingDef> byKey = new Dictionary<string, SettingDef>(StringComparer.OrdinalIgnoreCase);

    public SettingDef? Find(string key) => byKey.TryGetValue(key, out var d) ? d : null;

    public IEnumerable<SettingDef> InScope(string scope) => All.Where(d => d.Scope.Contains(scope));
    public IEnumerable<SettingDef> EntrySettings => InScope("entry");
    public IEnumerable<SettingDef> EngineSettings => InScope("engine");
    public IEnumerable<SettingDef> ServerSettings => InScope("server");

    public IEnumerable<IGrouping<string, SettingDef>> Grouped(IEnumerable<SettingDef> defs)
        => defs.GroupBy(d => d.Group).OrderBy(g => Rank(g.Key)).ThenBy(g => g.Key, StringComparer.Ordinal);

    private static int Rank(string group) => group switch
    {
        "Server" => 0, "GPU" => 1, "Context & KV" => 2, "Expert cache" => 3, "Slots" => 4,
        "Prompt" => 5, "Drafting" => 6, "Sampling" => 7, "Vision" => 8, "Conversation cache" => 9,
        "Control vectors" => 10, "Diagnostics" => 11, _ => 20,
    };

    private static SettingsCatalog Load()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                          .FirstOrDefault(n => n.EndsWith("settings-catalog.json", StringComparison.OrdinalIgnoreCase));
            if (name is null)
                return new SettingsCatalog();

            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var root = doc.RootElement;

            var list = new List<SettingDef>();
            foreach (var s in root.Get("settings").Arr())
            {
                list.Add(new SettingDef(
                    Key: s.Get("key").S(),
                    Scope: s.Get("scope").Arr().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToArray(),
                    Type: s.Get("type").S("string"),
                    Default: s.Get("default") is { } d ? JsonNode.Parse(d.GetRawText()) : null,
                    Model: s.Get("model").S("both"),
                    Ui: s.Get("ui").S("text"),
                    Group: s.Get("group").S("Other"),
                    Summary: s.Get("summary").S(),
                    Min: s.Path("range", "min").DN(),
                    Max: s.Path("range", "max").DN(),
                    Values: s.Get("values").Arr().Select(e => e.ToString().Trim('"')).ToArray()));
            }

            return new SettingsCatalog
            {
                All = list,
                Notes = root.Get("notes").Arr().Select(e => e.S()).Where(n => n.Length > 0).ToArray(),
                byKey = list.GroupBy(d => d.Key, StringComparer.OrdinalIgnoreCase)
                            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
            };
        }
        catch (Exception e)
        {
            AppPaths.Log("catalog load failed: " + e.Message);
            return new SettingsCatalog();
        }
    }
}

/// <summary>The app's own settings, in %APPDATA%\BasaltUI\settings.json — basalt's options stay in basalt's file.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public string BasaltExePath { get; set; } = "";
    public string ConfigPath { get; set; } = "";
    public string ModelsFolder { get; set; } = "";
    public string ExtraArgs { get; set; } = "";
    public string ApiKey { get; set; } = "";

    public bool StartServerOnLaunch { get; set; }
    public bool StartMinimized { get; set; }
    public bool RunAtLogon { get; set; }
    public bool MinimiseToTrayOnClose { get; set; } = true;
    public bool StopServerOnAppExit { get; set; } = true;
    public int StopGraceSeconds { get; set; } = 60;
    public int PollMs { get; set; } = 1000;
    public bool OpenWebUiOnReady { get; set; }

    // ---- pulling things in from the internet -----------------------------------------------------------------
    /// <summary>How many byte ranges to fetch at once. The line, not the number, usually decides the ceiling.</summary>
    public int DownloadConnections { get; set; } = 8;

    /// <summary>Where to ask Hugging Face. A mirror closer to the machine is faster; blank is the usual address.</summary>
    public string HfMirror { get; set; } = "";

    /// <summary>Blank means whatever Windows itself is configured to use.</summary>
    public string ProxyUrl { get; set; } = "";

    public bool CheckForUpdates { get; set; } = true;

    /// <summary>A release this version of the app has already been told about and chose not to mention again.</summary>
    public string DismissedUpdateTag { get; set; } = "";

    public DateTime LastUpdateCheckUtc { get; set; }

    /// <summary>
    /// basalt.exe carries no version resource of its own, so the number it announced while running is what the
    /// app remembers about the copy on disk. The key ties that number to that exact file: change the exe, or
    /// point the app at a different one, and the memory stops counting.
    /// </summary>
    public string EngineVersionMemo { get; set; } = "";
    public string EngineVersionMemoKey { get; set; } = "";

    // Where the window sat last. Kept nullable rather than NaN on purpose: System.Text.Json refuses to write
    // a NaN at all, so a NaN default meant the settings file was never written and the app remembered nothing.
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1060;
    public double WindowHeight { get; set; } = 760;
    public bool WindowMaximised { get; set; }
    public List<string> RecentModelPaths { get; set; } = [];

    public string ExeFallbackDir => Path.GetDirectoryName(BasaltExePath) ?? "";

    public void AddRecentModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        path = Path.GetFullPath(path);
        RecentModelPaths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentModelPaths.Insert(0, path);
        if (RecentModelPaths.Count > 12)
            RecentModelPaths.RemoveAt(RecentModelPaths.Count - 1);
    }

    public static AppSettings Load()
    {
        try
        {
            AppPaths.Ensure();
            if (File.Exists(AppPaths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Json);
                if (loaded is not null)
                    return loaded;
            }
        }
        catch (Exception e)
        {
            AppPaths.Log("settings load failed: " + e.Message);
        }

        var fresh = new AppSettings();
        var found = BasaltLocator.Discover();
        fresh.BasaltExePath = found.Exe;
        fresh.ConfigPath = found.Config;
        fresh.ModelsFolder = BasaltLocator.GuessModelsFolder();
        return fresh;
    }

    public void Save()
    {
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception e)
        {
            AppPaths.Log("settings save failed: " + e.Message);
        }
    }
}

/// <summary>Best guesses for a first run, so opening the app is not a configuration exercise.</summary>
public static class BasaltLocator
{
    public static (string Exe, string Config) Discover() => (FindExe() ?? "", FindConfig() ?? "");

    private static IEnumerable<string> Bases()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        foreach (var b in new[] { documents, profile, desktop, Environment.CurrentDirectory })
            if (!string.IsNullOrEmpty(b))
                yield return b;
    }

    private static string? FindExe()
    {
        foreach (var baseDir in Bases())
        foreach (var candidate in new[]
                 {
                     Path.Combine(baseDir, "basalt", "bin", "basalt.exe"),
                     Path.Combine(baseDir, "basalt", "basalt.exe"),
                     Path.Combine(baseDir, "bin", "basalt.exe"),
                 })
        {
            try
            {
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // an odd path is simply not a candidate
            }
        }

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in pathDirs)
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "basalt.exe");
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // ignore unparsable PATH entries
            }
        }
        return null;
    }

    private static string? FindConfig()
    {
        foreach (var baseDir in Bases())
        foreach (var name in new[] { "basalt_config.json", "basalt-config.json", "config.json" })
        {
            try
            {
                var candidate = Path.Combine(baseDir, name);
                if (File.Exists(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch
            {
                // ignore
            }
        }
        return null;
    }

    public static string GuessModelsFolder()
    {
        foreach (var candidate in new[]
                 {
                     @"C:\models",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "models"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "models"),
                 })
        {
            if (Directory.Exists(candidate))
                return candidate;
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>
    /// A size in the words Windows uses: the steps are 1024, but they are written KB / MB / GB the way
    /// Task Manager, the file properties dialog and Hugging Face all write them. Saying GiB for the same
    /// 31.3 billion bytes is how two honest programs end up looking like they disagree.
    /// </summary>
    public static string HumanBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }
        return $"{v:0.#} {units[i]}";
    }

    public static string HumanMib(double mib)
    {
        if (mib <= 0)
            return "0 GB";
        return mib >= 1024 ? $"{mib / 1024:0.##} GB" : $"{mib:0} MB";
    }
}
