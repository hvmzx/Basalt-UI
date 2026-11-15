using System.Text.Json;

namespace BasaltUI.Core;

/// <summary>
/// Narrow readers over the JSON basalt returns. Every field is optional by design: the client must keep
/// working across basalt versions, so a missing or oddly-typed value falls back instead of throwing.
/// Each reader comes in two shapes, one for a JsonElement and one for the JsonElement? a lookup returns, so a
/// chain like root.Get("hardware").Get("gpus").Arr() reads the way the payload does.
/// </summary>
internal static class JsonEx
{
    public static JsonElement Root(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static JsonElement? Get(this JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v
            : null;

    public static JsonElement? Get(this JsonElement? el, string name) => el is { } e ? e.Get(name) : null;

    public static JsonElement? Path(this JsonElement el, params string[] names)
    {
        var cur = el;
        foreach (var n in names)
        {
            if (cur.Get(n) is not { } next)
                return null;
            cur = next;
        }
        return cur;
    }

    public static JsonElement? Path(this JsonElement? el, params string[] names)
        => el is { } e ? e.Path(names) : null;

    public static double D(this JsonElement? el, double fallback = 0)
        => el is { } e && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : fallback;

    public static double D(this JsonElement el, double fallback = 0) => D((JsonElement?)el, fallback);

    public static double? DN(this JsonElement? el)
        => el is { } e && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) ? v : null;

    public static double? DN(this JsonElement el) => DN((JsonElement?)el);

    public static long L(this JsonElement? el, long fallback = 0)
        => el is { } e && e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v)
            ? v
            : (long)D(el, fallback);

    public static long L(this JsonElement el, long fallback = 0) => L((JsonElement?)el, fallback);

    public static int I(this JsonElement? el, int fallback = 0) => (int)L(el, fallback);

    public static int I(this JsonElement el, int fallback = 0) => (int)L(el, fallback);

    public static bool B(this JsonElement? el, bool fallback = false)
        => el is { } e && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : fallback;

    public static bool B(this JsonElement el, bool fallback = false) => B((JsonElement?)el, fallback);

    public static string S(this JsonElement? el, string fallback = "")
        => el is { } e && e.ValueKind == JsonValueKind.String ? e.GetString() ?? fallback : fallback;

    public static string S(this JsonElement el, string fallback = "") => S((JsonElement?)el, fallback);

    /// <summary>A string that is really optional: null when the field is absent or JSON null.</summary>
    public static string? SR(this JsonElement? el)
        => el is { } e && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    public static string? SR(this JsonElement el) => SR((JsonElement?)el);

    public static JsonElement[] Arr(this JsonElement? el)
        => el is { } e && e.ValueKind == JsonValueKind.Array ? e.EnumerateArray().ToArray() : [];

    public static JsonElement[] Arr(this JsonElement el) => Arr((JsonElement?)el);

    /// <summary>[0, 1, 2, …] as a double list; null entries become gaps (double.NaN), which charts skip.</summary>
    public static double[] Numbers(this JsonElement? el)
        => Arr(el).Select(e => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.NaN).ToArray();

    public static double[] Numbers(this JsonElement el) => Numbers((JsonElement?)el);

    /// <summary>Repeats the last value across gaps: the server's own history has nulls where nothing decoded.</summary>
    public static double[] Filled(this JsonElement? el)
    {
        var raw = Numbers(el);
        var last = 0.0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (double.IsNaN(raw[i]))
                raw[i] = i > 0 ? last : 0.0;
            else
                last = raw[i];
        }
        return raw;
    }

    public static double[] Filled(this JsonElement el) => Filled((JsonElement?)el);

    /// <summary>The entries of an object as name/value pairs, empty when the field is missing or not an object.</summary>
    public static (string Name, JsonElement Value)[] Props(this JsonElement? el)
    {
        if (el is not { } e || e.ValueKind != JsonValueKind.Object)
            return [];
        var list = new List<(string, JsonElement)>();
        foreach (var p in e.EnumerateObject())
            list.Add((p.Name, p.Value));
        return list.ToArray();
    }

    public static (string Name, JsonElement Value)[] Props(this JsonElement el) => Props((JsonElement?)el);
}
