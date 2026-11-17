using System.Windows.Media;

namespace BasaltUI.Core;

/// <summary>
/// Who a model comes from, by its own name: the first well-known name inside it wins, and the name is the
/// evidence it points at. The badge the rows wear is a monogram tile in the family's colour, not a copy of
/// anyone's logo — the tile's tooltip says which name matched, so a false match can be argued with.
/// </summary>
public static class ModelIdentity
{
    public sealed class Identity
    {
        public required string Vendor { get; init; }
        public required string Monogram { get; init; }
        public required Color Tint { get; init; }
        public required string Matched { get; init; }

        /// <summary>What a tooltip tells the user: which family, and which word in the name said so.</summary>
        public string Tip => $"{Vendor} — matched “{Matched}”";
    }

    private static readonly (Func<string, string, string?> Rule, string Vendor, string Mono, Color Tint)[] Known =
    [
        // The order is the precedence: an early name wins an argument, so the general families come first.
        (NameIn("qwen"), "Qwen", "QW", FromRgb(96, 140, 255)),
        (NameIn("deepseek"), "DeepSeek", "DS", FromRgb(70, 99, 235)),
        (NameIn("llama"), "Llama", "LL", FromRgb(0, 168, 232)),
        ((n, _) => n.Contains("mistral") || n.Contains("mixtral") || n.Contains("pixtral") ? "mixtral" : null,
            "Mistral", "MI", FromRgb(240, 96, 44)),
        (NameIn("gemma"), "Gemma", "GE", FromRgb(52, 168, 83)),
        ((n, o) => o.Contains("01-ai") ? "01-ai" : n.StartsWith("yi-", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("yi0") || n.Contains("-yi-") ? "yi" : null, "Yi", "YL", FromRgb(150, 96, 220)),
        (NameIn("granite"), "Granite", "GR", FromRgb(31, 122, 224)),
        (NameIn("nemotron"), "Nemotron", "NE", FromRgb(118, 185, 0)),
        (NameIn("jamba"), "Jamba", "JA", FromRgb(91, 124, 250)),
        (NameIn("olmo"), "OLMo", "OL", FromRgb(199, 141, 81)),
        (NameIn("glm"), "GLM", "GL", FromRgb(41, 178, 127)),
        ((n, _) => n.StartsWith("phi", StringComparison.OrdinalIgnoreCase) ? "phi" : null, "Phi", "PH", FromRgb(122, 94, 255)),
        ((n, _) => n.Contains("gpt-oss") || n.Contains("openai") ? "openai" : null, "OpenAI", "OA", FromRgb(150, 156, 166)),
        ((n, _) => n.StartsWith("smol", StringComparison.OrdinalIgnoreCase) ? "smol" : null, "SmolLM", "SM", FromRgb(235, 160, 60)),
    ];

    private static Func<string, string, string?> NameIn(string word)
        => (n, _) => n.Contains(word, StringComparison.OrdinalIgnoreCase) ? word : null;

    /// <param name="name">The model's name, as the server or the file has it.</param>
    /// <param name="owner">The repo owner, when one is known: some families only announce themselves there.</param>
    public static Identity? For(string name, string owner = "")
    {
        var n = (name ?? "").ToLowerInvariant();
        var o = (owner ?? "").ToLowerInvariant();
        foreach (var (rule, vendor, mono, tint) in Known)
        {
            var matched = rule(n, o);
            if (matched is null)
                continue;
            return new Identity
            {
                Vendor = vendor,
                Monogram = mono,
                Tint = tint,
                Matched = o.Length > 0 && o.Contains(matched!, StringComparison.OrdinalIgnoreCase)
                    ? "the repo"
                    : matched!,
            };
        }
        return null;
    }

    private static Color FromRgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}
