using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BasaltUI.Core;

/// <summary>
/// Named configs: whole basalt config documents — their own models, their server, their engine, their
/// raw JSON — kept beside the file the engine reads. One of them can be the <see cref="Active"/> one,
/// meaning the config file on disk currently carries its document. Attaching a config to a model
/// applies it: the file is rewritten from the config's document, with the model it was attached from
/// first in line, and the config is remembered as the one that model runs with. Assignments are keyed
/// by the model file's name, the one word the config entry, the main page's row and the store all
/// share, and they survive the app.
/// </summary>
public sealed class ConfigProfiles
{
    private readonly List<Config> configs = [];
    private readonly Dictionary<string, string> assigned = new(StringComparer.OrdinalIgnoreCase);
    private string? active;

    /// <summary>Everything a named config owns: the plain engine document, plus the app's own words about
    /// how it is launched. None of these app fields ever go into the engine's config file.</summary>
    public sealed class Config
    {
        public required string Name { get; set; }
        public required JsonObject Doc { get; set; }
        public string FilePath { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string ExtraArgs { get; set; } = "";
        public bool StartOnLaunch { get; set; }
    }

    public ConfigProfiles() => Load();

    /// <summary>Where the named configs and the assignments live.</summary>
    public static string StorePath => Path.Combine(AppPaths.Root, "config-profiles.json");

    public IReadOnlyList<string> Names => [.. configs.Select(c => c.Name)];

    /// <summary>The config's whole document — the live object, so an editor can change it in place and
    /// call <see cref="Update"/> after. Null when no config wears the name.</summary>
    public JsonObject? Document(string name) => Of(name)?.Doc;

    private Config? Of(string name)
        => configs.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public bool Exists(string name) => Of(name) is not null;

    /// <summary>The file this config is written into and passed to basalt on the command line.</summary>
    public string? FilePath(string name) => Of(name)?.FilePath;

    public string ApiKey(string name) => Of(name)?.ApiKey ?? "";

    public string ExtraArgs(string name) => Of(name)?.ExtraArgs ?? "";

    public bool StartsOnLaunch(string name) => Of(name)?.StartOnLaunch == true;

    public void SetLaunch(string name, string? file = null, string? apiKey = null,
                          string? extraArgs = null, bool? startOnLaunch = null)
    {
        if (Of(name) is not { } config)
            return;
        if (file is not null)
            config.FilePath = file.Trim();
        if (apiKey is not null)
            config.ApiKey = apiKey.Trim();
        if (extraArgs is not null)
            config.ExtraArgs = extraArgs.Trim();
        if (startOnLaunch is not null)
            config.StartOnLaunch = startOnLaunch.Value;
        Save();
    }

    /// <summary>A sensible file for a new config: its own name in the app's configs folder.</summary>
    public static string DefaultFileFor(string name)
    {
        var safe = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));
        return Path.Combine(AppPaths.ConfigsDir, safe + ".json");
    }

    /// <summary>A starting point for a config that has nothing yet: the shape basalt reads.</summary>
    public static JsonObject BlankDoc() => new()
    {
        ["version"] = 2,
        ["models"] = new JsonArray(),
        ["engine"] = new JsonObject(),
        ["server"] = new JsonObject(),
    };

    /// <summary>A new config with a name that is not taken — "config", then "config 2", "config 3"… —
    /// seeded with a deep copy of <paramref name="seed"/>, or the blank shape when it is null.</summary>
    public string Create(string baseName, JsonObject? seed)
    {
        var name = baseName;
        for (var i = 2; Exists(name); i++)
            name = $"{baseName} {i}";
        configs.Add(new Config
        {
            Name = name,
            Doc = seed is null ? BlankDoc() : (JsonObject)seed.DeepClone(),
            FilePath = DefaultFileFor(name),
        });
        Save();
        return name;
    }

    /// <summary>The named config's document becomes <paramref name="root"/>; a deep copy is kept, so the
    /// editor's object and the store's never share a node.</summary>
    public void Update(string name, JsonObject root)
    {
        var at = configs.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
            return;
        configs[at].Doc = (JsonObject)root.DeepClone();
        Save();
    }

    public void Delete(string name)
    {
        configs.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var file in AttachedTo(name))
            assigned.Remove(Key(file));
        if (name.Equals(active, StringComparison.OrdinalIgnoreCase))
            active = null;
        Save();
    }

    /// <summary>A new name for a config; the models running with it keep running with it, and it stays
    /// the active one under the new name.</summary>
    public void Rename(string old, string name)
    {
        var at = configs.FindIndex(c => c.Name.Equals(old, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || name.Length == 0 || Exists(name) && !name.Equals(old, StringComparison.OrdinalIgnoreCase))
            return;
        configs[at].Name = name;
        foreach (var key in assigned.Where(kv => kv.Value.Equals(old, StringComparison.OrdinalIgnoreCase))
                                     .Select(kv => kv.Key)
                                     .ToList())
            assigned[key] = name;
        if (old.Equals(active, StringComparison.OrdinalIgnoreCase))
            active = name;
        Save();
    }

    /// <summary>Which config a model file runs with; null when none, or when the one it ran with is gone.</summary>
    public string? AssignedTo(string file)
        => assigned.TryGetValue(Key(file), out var name) && Exists(name) ? name : null;

    public void Assign(string file, string? name)
    {
        if (name is null)
            assigned.Remove(Key(file));
        else if (Exists(name))
            assigned[Key(file)] = name;
        Save();
    }

    /// <summary>The model files that run with a config, in no special order.</summary>
    public IReadOnlyList<string> AttachedTo(string name)
        => [.. assigned.Where(kv => kv.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key)];

    /// <summary>The config whose document the config file on disk currently carries — the one an edit in
    /// the editor re-writes into the file. Null when the file was last shaped by hand or by another tool.</summary>
    public string? Active
    {
        get => active is { } a && Exists(a) ? a : null;
        set
        {
            active = value is { } v && Exists(v) ? v : null;
            Save();
        }
    }

    private static string Key(string file) => Path.GetFileName(file).ToLowerInvariant();

    // ---- the file ------------------------------------------------------------------------------------------
    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(StorePath));
            var root = doc.RootElement;
            foreach (var c in root.Get("configs").Arr())
            {
                var name = c.Get("name").S();
                var document = c.Get("doc");
                if (name.Length == 0 || document is not { ValueKind: JsonValueKind.Object })
                    continue;
                configs.Add(new Config
                {
                    Name = name,
                    Doc = JsonNode.Parse(document.Value.GetRawText()) as JsonObject ?? BlankDoc(),
                    FilePath = c.Get("file").S(),
                    ApiKey = c.Get("apiKey").S(),
                    ExtraArgs = c.Get("extraArgs").S(),
                    StartOnLaunch = c.Get("startOnLaunch").B(),
                });
            }
            foreach (var a in root.Get("assigned").Arr())
            {
                var file = a.Get("file").S();
                var name = a.Get("config").S();
                if (file.Length > 0 && name.Length > 0)
                    assigned[Key(file)] = name;
            }
            var at = root.Get("active").S();
            if (at.Length > 0)
                active = at;
        }
        catch (Exception e)
        {
            // A file the app itself wrote should never be broken; if one is, the names start again
            // rather than the app starting to apologise in a dialog.
            AppPaths.Log("config-profiles: " + e.Message);
        }
    }

    /// <summary>The whole store as JSON — the export writes this beside the app's settings.</summary>
    public JsonObject ToJson()
    {
        var list = new JsonArray();
        foreach (var c in configs)
            list.Add(new JsonObject
            {
                ["name"] = c.Name,
                ["doc"] = JsonNode.Parse(c.Doc.ToJsonString()),
                ["file"] = c.FilePath,
                ["apiKey"] = c.ApiKey,
                ["extraArgs"] = c.ExtraArgs,
                ["startOnLaunch"] = c.StartOnLaunch,
            });
        var worn = new JsonArray();
        foreach (var kv in assigned)
            worn.Add(new JsonObject { ["file"] = kv.Key, ["config"] = kv.Value });
        return new JsonObject
        {
            ["version"] = 3,
            ["active"] = active,
            ["configs"] = list,
            ["assigned"] = worn,
        };
    }

    public void Save()
    {
        try
        {
            AppPaths.Ensure();
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, ToJson().ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception e)
        {
            AppPaths.Log("config-profiles save: " + e.Message);
        }
    }

    private readonly object gate = new();
}
