using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BasaltUI.Core;

/// <summary>
/// A basalt config file kept as a JSON document rather than a typed object: everything the file already
/// holds (odd keys, comments removed by parsing but unknown names, ordering) survives an edit, and a key the
/// app does not know is written back untouched.
/// </summary>
public sealed class ConfigDocument
{
    public const int SupportedVersion = 2;

    public string FilePath { get; }
    public JsonObject Root { get; }
    public bool Dirty { get; private set; }
    public string OriginalText { get; private set; }

    public ConfigDocument(string filePath, JsonObject root, string originalText)
    {
        FilePath = filePath;
        Root = root;
        OriginalText = originalText;
    }

    public static ConfigDocument Load(string filePath)
    {
        var text = File.ReadAllText(filePath);
        var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
        if (node is not JsonObject obj)
            throw new InvalidDataException("the config file must contain one JSON object");
        return new ConfigDocument(filePath, obj, text);
    }

    /// <summary>A starting point when no config file exists yet.</summary>
    public static ConfigDocument Blank(string filePath, string modelPath = "")
    {
        var root = new JsonObject
        {
            ["version"] = SupportedVersion,
            ["models"] = new JsonArray
            {
                new JsonObject
                {
                    ["model"] = modelPath,
                    ["gpu"] = "0",
                    ["kv"] = "int8",
                    ["max-context"] = 262144,
                    ["vision"] = "dynamic",
                },
            },
            ["engine"] = new JsonObject { ["resident-experts"] = true },
            ["server"] = new JsonObject { ["host"] = "127.0.0.1", ["port"] = 8080 },
        };
        var doc = new ConfigDocument(filePath, root, "");
        doc.Dirty = true;
        return doc;
    }

    // ---- blocks ------------------------------------------------------------------------------------------
    public JsonObject Block(string name)
    {
        if (Root[name] is JsonObject obj)
            return obj;
        var created = new JsonObject();
        Root[name] = created;
        Dirty = true;
        return created;
    }

    public JsonObject Server => Block("server");
    public JsonObject Engine => Block("engine");
    public JsonArray Models => Root["models"] as JsonArray ?? CreateModels();

    private JsonArray CreateModels()
    {
        var arr = new JsonArray();
        Root["models"] = arr;
        Dirty = true;
        return arr;
    }

    public List<JsonObject> ModelEntries() => Models.OfType<JsonObject>().ToList();

    public int Version
    {
        get => Number(Root, "version") is double v ? (int)v : 0;
        set { Root["version"] = value; Dirty = true; }
    }

    public string Host => Text(Server, "host") ?? "127.0.0.1";

    /// <summary>The port to talk to: basalt's own default when the file says nothing usable about it.</summary>
    public int Port => Number(Server, "port") is double n and > 0 and < 65536 ? (int)n : 8080;

    /// <summary>Every object the file holds, in the order basalt reads them.</summary>
    public IEnumerable<JsonObject> Blocks()
    {
        yield return Root;
        if (Root["server"] is JsonObject server)
            yield return server;
        if (Root["engine"] is JsonObject engine)
            yield return engine;
        foreach (var entry in ModelEntries())
            yield return entry;
    }

    /// <summary>An api key cannot live in the file, in any block, under any spelling of its name; this is the one
    /// the user gave the app so it can pass <c>--api-key</c> on the command line.</summary>
    public bool KeyWouldBeRefused => Blocks().Any(b => b.Any(kv => LettersOnly(kv.Key).StartsWith("apikey", StringComparison.OrdinalIgnoreCase)));

    private static string LettersOnly(string key) => new([.. key.Where(char.IsLetter)]);

    /// <summary>
    /// basalt 0.3.3 stopped starting with the answer room turned flat off; it wants <c>auto</c> or a number.
    /// Older builds took <c>false</c> happily, so this is something to say only once a newer engine answers.
    /// </summary>
    public bool AnswerRoomSwitchedOff
        => Blocks().Any(b => FindKey(b, "answer-room") is { } name
                             && b[name] is JsonValue value
                             && value.GetValueKind() == System.Text.Json.JsonValueKind.False);

    // ---- key lookup (basalt accepts underscores, any case and a leading --) -------------------------------
    public static string Canonical(string key) => key.Trim().TrimStart('-').Replace('_', '-');

    private static bool KeyEq(string a, string b) => string.Equals(Canonical(a), Canonical(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>The property name actually used in the block for a canonical key, or null.</summary>
    public static string? FindKey(JsonObject block, string canonical)
    {
        foreach (var kv in block)
            if (KeyEq(kv.Key, canonical))
                return kv.Key;
        return null;
    }

    public static bool Has(JsonObject block, string canonical) => FindKey(block, canonical) is not null;

    public static string? Text(JsonObject block, string canonical)
    {
        var name = FindKey(block, canonical);
        if (name is null)
            return null;
        var node = block[name];
        if (node is null || (node is JsonValue nullValue && nullValue.GetValueKind() == JsonValueKind.Null))
            return null;
        try
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue(out string? s))
                    return s;
                if (v.TryGetValue(out double d))
                    return d.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
                if (v.TryGetValue(out bool b))
                    return b ? "true" : "false";
                if (v.TryGetValue(out long l))
                    return l.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            return node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        }
        catch
        {
            return node.ToJsonString();
        }
    }

    /// <summary>The list a repeatable key carries; a single string counts as a one-item list.</summary>
    public static List<string> List(JsonObject block, string canonical)
    {
        var name = FindKey(block, canonical);
        var list = new List<string>();
        if (name is null)
            return list;
        switch (block[name])
        {
            case JsonArray arr:
                foreach (var item in arr)
                {
                    var s = item?.ToString().Trim('"');
                    if (!string.IsNullOrWhiteSpace(s))
                        list.Add(s!);
                }
                break;
            case JsonValue v when v.TryGetValue(out string? s):
                list.Add(s);
                break;
            case JsonValue other:
                var text = other.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    list.Add(text);
                break;
        }
        return list;
    }

    public static bool? Switch(JsonObject block, string canonical)
    {
        var name = FindKey(block, canonical);
        if (name is null)
            return null;
        try
        {
            if (block[name] is JsonValue v)
            {
                if (v.TryGetValue(out bool b))
                    return b;
                if (v.TryGetValue(out string? s) && bool.TryParse(s, out var pb))
                    return pb;
            }
        }
        catch
        {
            // not a switch
        }
        return null;
    }

    /// <summary>
    /// A key as a number, in whatever shape the DOM holds it. The file's numbers arrive as JSON, but a number this
    /// app just wrote is a CLR long inside the DOM, and System.Text.Json refuses to see that as a double — so a
    /// port the user changed in the app would read back as the default.
    /// </summary>
    public static double? Number(JsonObject block, string canonical)
    {
        var name = FindKey(block, canonical);
        if (name is null)
            return null;
        var node = block[name];
        if (node is null)
            return null;
        try
        {
            if (node is JsonValue v)
            {
                if (v.TryGetValue(out double d))
                    return d;
                if (v.TryGetValue(out long l))
                    return l;
                if (v.TryGetValue(out bool b))
                    return b ? 1 : 0;
            }
            var raw = node is JsonValue text ? text.ToString() : node.ToJsonString();
            return double.TryParse(raw.Trim('"'), System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var pd)
                ? pd
                : null;
        }
        catch
        {
            return null;                                    // an object or a list where a number was expected
        }
    }

    public static int? Int(JsonObject block, string canonical) => Number(block, canonical) is double n and >= int.MinValue and <= int.MaxValue
        ? (int)n
        : null;

    // ---- writes -----------------------------------------------------------------------------------------
    /// <summary>Removes the key when <paramref name="value"/> is null, so it falls back to basalt's default.</summary>
    public void Set(JsonObject block, string canonical, JsonNode? value)
    {
        var existing = FindKey(block, canonical);
        if (existing is not null && existing != canonical)
            block.Remove(existing);                                   // normalise the spelling to the documented one

        if (value is null)
        {
            if (existing is not null)
                block.Remove(canonical);
        }
        else
        {
            block[canonical] = value;
        }
        Dirty = true;
    }

    public void SetText(JsonObject block, string canonical, string? text)
        => Set(block, canonical, string.IsNullOrWhiteSpace(text) ? null : JsonValue.Create(text.Trim()));

    public void SetSwitch(JsonObject block, string canonical, bool? on)
    {
        if (on is null)
        {
            Set(block, canonical, null);
            return;
        }
        // A false switch is the same as omitting it: basalt takes flags as words, never as "--flag false".
        Set(block, canonical, on == true ? JsonValue.Create(true) : null);
    }

    public void SetNumber(JsonObject block, string canonical, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Set(block, canonical, null);
            return;
        }
        var trimmed = text.Trim();
        if (long.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l))
            Set(block, canonical, JsonValue.Create(l));
        else if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
            Set(block, canonical, JsonValue.Create(d));
        else
            Set(block, canonical, JsonValue.Create(trimmed));        // a model name or an "auto": keep the words
    }

    public void SetList(JsonObject block, string canonical, IEnumerable<string> items)
    {
        var filtered = items.Select(i => i.Trim()).Where(i => i.Length > 0).ToList();
        if (filtered.Count == 0)
        {
            Set(block, canonical, null);
            return;
        }
        var arr = new JsonArray();
        foreach (var item in filtered)
            arr.Add(JsonValue.Create(item));
        Set(block, canonical, arr);
    }

    /// <summary>A catalog entry by the spelling the file happens to use, or null when basalt has no such setting.</summary>
    public static SettingDef? Definition(SettingsCatalog catalog, string key)
        => catalog.Find(key) ?? catalog.Find(Canonical(key)) ?? catalog.All.FirstOrDefault(d => KeyEq(d.Key, key));

    /// <summary>Keys in a block that the catalog does not know, so the editor can show them rather than drop them.</summary>
    public static List<string> UnknownKeys(JsonObject block, SettingsCatalog catalog)
        => block.Where(kv => Definition(catalog, kv.Key) is null).Select(kv => kv.Key).ToList();

    /// <summary>
    /// One block against the catalog: a name basalt has never heard of, or a real setting in a block that does not take
    /// it. Both make basalt refuse the start, in words worth showing before the user presses Start.
    /// </summary>
    private static void CheckBlock(List<Issue> issues, JsonObject block, string blockName, SettingsCatalog catalog, string? where = null)
    {
        where ??= blockName;
        foreach (var pair in block)
        {
            if (KeyEq(pair.Key, "engine-bin"))
            {
                issues.Add(new Issue(true, where, "engine-bin is basalt serve's own word: it cannot come from the file"));
                continue;
            }

            var def = Definition(catalog, pair.Key);
            if (def is null)
            {
                issues.Add(new Issue(true, where, $"“{pair.Key}” is not a setting this app knows: unless the engine itself takes that name, basalt stops the start"));
                continue;
            }
            if (def.IsCommandOnly)
                continue;                                       // api-key: named in its own words by the check below
            if (def.Scope.Contains(blockName))
                continue;

            var wrong = blockName switch
            {
                "server" => "a model setting: put it in the engine block or in a model entry",
                "engine" when def.Scope.Contains("entry") => $"a per-model setting: basalt wants it in the model entry it applies to, not in engine",
                "engine" => "a server option: set it under server",
                _ => "a server option: basalt refuses it in a model entry",
            };
            issues.Add(new Issue(true, where, $"{blockName} {pair.Key} ({def.ScopeText}): {wrong}"));
        }
    }

    // ---- validation -------------------------------------------------------------------------------------
    public sealed record Issue(bool Error, string Where, string Message);

    public List<Issue> Validate(SettingsCatalog catalog)
    {
        var issues = new List<Issue>();
        var dir = string.IsNullOrEmpty(FilePath) ? Environment.CurrentDirectory : (Path.GetDirectoryName(FilePath) ?? ".");

        // basalt's own top-of-file checks, in its order; a missing "version" is fine, it reads that as 2.
        var versionNumber = Number(Root, "version");
        if (Root["version"] is not null
            && (Root["version"]!.GetValueKind() != JsonValueKind.Number || versionNumber is null || versionNumber.Value % 1 != 0))
            issues.Add(new Issue(true, "version", "“version” must be a whole number"));
        else if (Version > SupportedVersion)
            issues.Add(new Issue(true, "version", $"this is a v{Version} config; the basalt installed here reads v{SupportedVersion}"));
        else if (Version is > 0 and < SupportedVersion)
            issues.Add(new Issue(true, "version", $"version {Version} is from an older basalt: run python tools/migrations/config_v1_to_v2.py on this file to get a v{SupportedVersion} one, and point the app at the result"));
        else if (Root["v"] is not null)
            issues.Add(new Issue(true, "v", $"only a v{SupportedVersion} config starts: “v” is a v1 marker, run tools/migrations/config_v1_to_v2.py on this file"));

        foreach (var stray in Root.Where(kv => kv.Key is not ("version" or "server" or "engine" or "models")))
            issues.Add(new Issue(true, stray.Key, $"basalt takes only version, server, engine and models here: “{stray.Key}” stops the start"));

        if (Root["models"] is not JsonArray)
            issues.Add(new Issue(true, "models", "no models block: basalt refuses a file without it"));

        var aliases = new List<string>();
        var index = 0;
        foreach (var entry in ModelEntries())
        {
            index++;
            var modelText = Text(entry, "model");
            var label = Text(entry, "alias") ?? $"model {index}";
            if (string.IsNullOrWhiteSpace(modelText))
                issues.Add(new Issue(true, label, "this model entry has no model file"));
            else if (modelText.EndsWith(".basalt", StringComparison.OrdinalIgnoreCase))
            {
                var full = Path.IsPathRooted(modelText) ? modelText : Path.Combine(dir, modelText);
                if (!File.Exists(full))
                    issues.Add(new Issue(false, label, "the model file does not exist: " + full));
            }

            var alias = Text(entry, "alias");
            if (!string.IsNullOrWhiteSpace(alias))
            {
                if (aliases.Any(a => KeyEq(a, alias)))
                    issues.Add(new Issue(true, label, $"alias “{alias}” is used twice: basalt stops the start"));
                aliases.Add(alias);
            }

            CheckBlock(issues, entry, "entry", catalog, label);
        }

        CheckBlock(issues, Engine, "engine", catalog);
        CheckBlock(issues, Server, "server", catalog);

        if (KeyWouldBeRefused)
            issues.Add(new Issue(true, "server", "api-key cannot be in a config file — set it in the app's launch options"));

        var host = Host;
        if (!ThisPcOnly(host))
            issues.Add(new Issue(false, "server", $"host {host} answers to other machines too: give basalt an api key (the Basalt tab) and add allowed-host entries for the names you use"));

        if (Number(Server, "port") is double port and (< 1 or > 65535))
            issues.Add(new Issue(true, "port", "port is a number between 1 and 65535"));
        else if (FindKey(Server, "port") is not null && Number(Server, "port") is null)
            issues.Add(new Issue(true, "port", $"port has to be a number, not “{Text(Server, "port")}”"));

        var parallel = ModelEntries().Select(e => Int(e, "parallel")).Concat([Int(Engine, "parallel")])
                                     .FirstOrDefault(n => n is not null);
        if (parallel is > 8 or < 1)
            issues.Add(new Issue(true, "parallel", "parallel is 1 to 8"));
        else if (parallel > 1)
        {
            // basalt: "parallel and engine --batch both set: use parallel"
            foreach (var clash in new[] { "batch", "slots" })
                if (FindKey(Engine, clash) is not null || ModelEntries().Any(e => FindKey(e, clash) is not null))
                    issues.Add(new Issue(true, "parallel", $"parallel and {clash} are both set, which stops the start: leave {clash} out and let parallel set it"));
        }

        return issues;
    }

    /// <summary>True only for an address another machine cannot reach; 0.0.0.0, :: and * bind the network.</summary>
    public static bool ThisPcOnly(string host)
    {
        var trimmed = host.Trim();
        return trimmed is "" or "127.0.0.1" or "localhost" or "::1"
               || trimmed.StartsWith("127.", StringComparison.Ordinal);
    }

    // ---- output -----------------------------------------------------------------------------------------
    public string ToJson()
    {
        var text = Root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return text.EndsWith('\n') ? text : text + "\n";
    }

    public void MarkSaved() => Dirty = false;

    /// <summary>For text that was edited outside the DOM helpers but belongs to this file.</summary>
    public void MarkDirty() => Dirty = true;

    /// <summary>
    /// Writes the file, taking a timestamped copy of the previous contents first. Keeps the newest 40 backups.
    /// Returns the backup path, or null when there was nothing to back up.
    /// </summary>
    public string? Save(bool takeBackup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        string? backup = null;

        if (takeBackup && File.Exists(FilePath))
        {
            var previous = File.ReadAllText(FilePath);
            if (previous != ToJson())
            {
                Directory.CreateDirectory(AppPaths.BackupsDir);
                backup = NextBackupPath();
                File.WriteAllText(backup, previous);
                PruneBackups();
            }
        }

        // Write beside the target and swap: half a config file would stop the next start.
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, ToJson(), new UTF8Encoding(false));
        File.Move(temp, FilePath, overwrite: true);
        OriginalText = ToJson();
        Dirty = false;
        AppPaths.Log($"saved config {FilePath}" + (backup is null ? "" : $" (backup {backup})"));
        return backup;
    }

    /// <summary>
    /// Backup names are timestamped to the second, which is the same granularity the restore list shows. Two saves
    /// inside one second — or two configs with the same name in different folders — still get their own file.
    /// </summary>
    private string NextBackupPath()
    {
        var stem = Path.GetFileNameWithoutExtension(FilePath) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(AppPaths.BackupsDir, stem + ".json");
        for (var n = 2; File.Exists(path); n++)
            path = Path.Combine(AppPaths.BackupsDir, $"{stem}-{n}.json");
        return path;
    }

    private static void PruneBackups()
    {
        try
        {
            var old = Directory.EnumerateFiles(AppPaths.BackupsDir, "*.json")
                               .OrderByDescending(f => File.GetLastWriteTimeUtc(f))
                               .Skip(40);
            foreach (var file in old)
                File.Delete(file);
        }
        catch
        {
            // pruning is best-effort
        }
    }
}
