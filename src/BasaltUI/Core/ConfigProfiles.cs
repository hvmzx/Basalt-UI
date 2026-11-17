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
    private readonly List<(string Name, JsonObject Doc)> configs = [];
    private readonly Dictionary<string, string> assigned = new(StringComparer.OrdinalIgnoreCase);
    private string? active;

    public ConfigProfiles() => Load();

    /// <summary>Where the named configs and the assignments live.</summary>
    public static string StorePath => Path.Combine(AppPaths.Root, "config-profiles.json");

    public IReadOnlyList<string> Names => [.. configs.Select(c => c.Name)];

    /// <summary>The config's whole document — the live object, so an editor can change it in place and
    /// call <see cref="Update"/> after. Null when no config wears the name.</summary>
    public JsonObject? Document(string name)
        => configs.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Doc;

    public bool Exists(string name)
        => configs.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

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
        configs.Add((name, seed is null ? BlankDoc() : (JsonObject)seed.DeepClone()));
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
        configs[at] = (configs[at].Name, (JsonObject)root.DeepClone());
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
        configs[at] = (name, configs[at].Doc);
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
                configs.Add((name, JsonNode.Parse(document.Value.GetRawText()) as JsonObject ?? BlankDoc()));
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

    public void Save()
    {
        JsonNode snapshot;
        lock (gate)
        {
            var list = new JsonArray();
            foreach (var (name, document) in configs)
                list.Add(new JsonObject
                {
                    ["name"] = name,
                    ["doc"] = JsonNode.Parse(document.ToJsonString()),
                });
            var worn = new JsonArray();
            foreach (var kv in assigned)
                worn.Add(new JsonObject { ["file"] = kv.Key, ["config"] = kv.Value });
            snapshot = new JsonObject
            {
                ["version"] = 2,
                ["active"] = active,
                ["configs"] = list,
                ["assigned"] = worn,
            };
        }

        try
        {
            AppPaths.Ensure();
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception e)
        {
            AppPaths.Log("config-profiles save: " + e.Message);
        }
    }

    private readonly object gate = new();
}
