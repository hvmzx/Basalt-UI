using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BasaltUI.Core;

/// <summary>
/// Named configs, and which model wears which. The engine's config file carries one plain entry per model
/// and knows nothing of names, so the names live beside it: a config here is a set of entry-level fields,
/// and attaching one writes its fields into that model's entry in the file — the entry becomes exactly
/// what wearing the config says. Editing a config is therefore editing every model that wears it, and the
/// file on disk stays the engine's plain self, with nothing app-shaped inside it.
/// </summary>
/// <remarks>
/// Assignments are keyed by the model file's name, the one word the config entry, the main page's row and
/// the store all share. The open assignments are in the file too, so they survive the app.
/// </remarks>
public sealed class ConfigProfiles
{
    private readonly List<(string Name, JsonObject Fields)> configs = [];
    private readonly Dictionary<string, string> assigned = new(StringComparer.OrdinalIgnoreCase);

    public ConfigProfiles() => Load();

    /// <summary>Where the named configs and the assignments live.</summary>
    public static string StorePath => Path.Combine(AppPaths.Root, "config-profiles.json");

    public IReadOnlyList<string> Names => [.. configs.Select(c => c.Name)];

    /// <summary>The config's fields themselves — the live object, so an editor can change them in place
    /// and call <see cref="Save"/> after. Null when no config wears the name.</summary>
    public JsonObject? Fields(string name)
        => configs.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Fields;

    public bool Exists(string name)
        => configs.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A new config with a name that is not taken: "config", then "config 2", "config 3"…</summary>
    public string Create(string baseName)
    {
        var name = baseName;
        for (var i = 2; Exists(name); i++)
            name = $"{baseName} {i}";
        configs.Add((name, []));
        Save();
        return name;
    }

    public void Delete(string name)
    {
        configs.RemoveAll(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var file in AttachedTo(name))
            assigned.Remove(Key(file));
        Save();
    }

    /// <summary>A new name for a config; the models wearing it keep wearing it under the new name.</summary>
    public void Rename(string old, string name)
    {
        var at = configs.FindIndex(c => c.Name.Equals(old, StringComparison.OrdinalIgnoreCase));
        if (at < 0 || name.Length == 0 || Exists(name) && !name.Equals(old, StringComparison.OrdinalIgnoreCase))
            return;
        configs[at] = (name, configs[at].Fields);
        foreach (var key in assigned.Where(kv => kv.Value.Equals(old, StringComparison.OrdinalIgnoreCase))
                                     .Select(kv => kv.Key)
                                     .ToList())
            assigned[key] = name;
        Save();
    }

    /// <summary>Which config a model file wears; null when none, or when the one it wore is gone.</summary>
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

    /// <summary>The model files that wear a config, in no special order.</summary>
    public IReadOnlyList<string> AttachedTo(string name)
        => [.. assigned.Where(kv => kv.Value.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key)];

    /// <summary>The entry's own settings become the config's: every field the config defines is written in,
    /// every field it does not define is gone. The model path is the entry's own and is never touched; the
    /// values are deep-copied so the config and the file never share a node.</summary>
    public static void Apply(JsonObject entry, JsonObject fields)
    {
        foreach (var key in entry.Select(kv => kv.Key).Where(k => !k.Equals("model", StringComparison.OrdinalIgnoreCase)).ToList())
            entry.Remove(key);
        foreach (var kv in fields)
        {
            if (kv.Key.Equals("model", StringComparison.OrdinalIgnoreCase) || kv.Value is null)
                continue;
            entry[kv.Key] = JsonNode.Parse(kv.Value.ToJsonString());
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
                if (name.Length == 0)
                    continue;
                var fields = new JsonObject();
                foreach (var (key, value) in c.Get("fields").Props())
                {
                    if (key.Length == 0 || value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                        continue;                       // a field is a name and a plain value, never a block
                    var node = value.ValueKind switch
                    {
                        JsonValueKind.Number => JsonValue.Create(value.GetDouble()),
                        JsonValueKind.True => JsonValue.Create(true),
                        JsonValueKind.False => JsonValue.Create(false),
                        _ => JsonValue.Create(value.S()),
                    };
                    if (node is not null)
                        fields[key] = node;
                }
                configs.Add((name, fields));
            }
            foreach (var a in root.Get("assigned").Arr())
            {
                var file = a.Get("file").S();
                var name = a.Get("config").S();
                if (file.Length > 0 && name.Length > 0)
                    assigned[Key(file)] = name;
            }
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
            foreach (var (name, fields) in configs)
            {
                var copy = new JsonObject();
                foreach (var kv in fields)
                    if (kv.Value is not null)
                        copy[kv.Key] = JsonNode.Parse(kv.Value.ToJsonString());
                list.Add(new JsonObject { ["name"] = name, ["fields"] = copy });
            }
            var worn = new JsonArray();
            foreach (var kv in assigned)
                worn.Add(new JsonObject { ["file"] = kv.Key, ["config"] = kv.Value });
            snapshot = new JsonObject { ["version"] = 1, ["configs"] = list, ["assigned"] = worn };
        }

        try
        {
            AppPaths.Ensure();
            var tmp = StorePath + ".tmp";
            File.WriteAllText(tmp, snapshot.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, StorePath, overwrite: true);
        }
        catch (Exception e)
        {
            AppPaths.Log("config-profiles save: " + e.Message);
        }
    }

    private readonly object gate = new();
}
