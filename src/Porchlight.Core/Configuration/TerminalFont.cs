using System.Text.Json;

namespace Porchlight.Core.Configuration;

/// Data passed to the renderer. The face string is Windows Terminal's ordered list syntax;
/// in particular, do not split, unquote or re-sort it.
public sealed record TerminalFont
{
    public string Face { get; init; } = "Cascadia Code, KaiTi, SimHei, Segoe UI Emoji, Symbols Nerd Font Mono";
    public float Size { get; init; } = 14;
    public ushort Weight { get; init; } = 400;
    public string CellHeight { get; init; } = "";
    public string CellWidth { get; init; } = "";
    public bool BuiltinGlyphs { get; init; } = true;
    public bool ColorGlyphs { get; init; } = true;
    public Dictionary<string, float> Features { get; init; } = new() { ["ss01"] = 0, ["calt"] = 1 };
    public Dictionary<string, float> Axes { get; init; } = [];

    public static TerminalFont ReadWindowsTerminalDefaults(Stream input)
    {
        try
        {
            using var doc = JsonDocument.Parse(input, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var font = doc.RootElement.GetProperty("profiles").GetProperty("defaults").GetProperty("font");
            var size = font.TryGetProperty("size", out var s) ? s.GetSingle() : 12f;
            if (!float.IsFinite(size) || size <= 0) throw new FormatException("Invalid Windows Terminal font.size.");
            return new() {
                Face = font.TryGetProperty("face", out var face) ? face.GetString() ?? throw new FormatException("Invalid font.face.") : "Cascadia Mono",
                Size = size, Weight = font.TryGetProperty("weight", out var weight) ? WeightValue(weight) : (ushort)400,
                CellHeight = Dimension("cellHeight"), CellWidth = Dimension("cellWidth"),
                BuiltinGlyphs = !font.TryGetProperty("builtinGlyphs", out var b) || b.GetBoolean(),
                ColorGlyphs = !font.TryGetProperty("colorGlyphs", out var c) || c.GetBoolean(),
                Features = Tags("features"), Axes = Tags("axes")
            };

            string Dimension(string key) => !font.TryGetProperty(key, out var value) ? "" : value.ValueKind switch {
                JsonValueKind.String => value.GetString()!, JsonValueKind.Number => value.GetRawText(),
                _ => throw new FormatException($"Invalid Windows Terminal font.{key}.")
            };
            Dictionary<string,float> Tags(string key)
            {
                var tags = new Dictionary<string,float>(StringComparer.Ordinal);
                if (font.TryGetProperty(key, out var map)) foreach (var entry in map.EnumerateObject())
                {
                    if (entry.Name.Length != 4 || entry.Name.Any(ch => ch is < ' ' or > '~') ||
                        !entry.Value.TryGetSingle(out float value) || !float.IsFinite(value))
                        throw new FormatException($"Windows Terminal font.{key} needs four-character tags and numeric values.");
                    tags[entry.Name] = value;
                }
                return tags;
            }
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or OverflowException)
        { throw new FormatException("Cannot read profiles.defaults.font from this Windows Terminal settings.json."); }
    }

    static ushort WeightValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number && value.TryGetUInt16(out var n) && n is >= 1 and <= 1000) return n;
        return (value.ValueKind == JsonValueKind.String ? value.GetString() : null) switch {
            "thin" => 100, "extra-light" => 200, "light" => 300, "semi-light" => 350, "normal" => 400,
            "medium" => 500, "semi-bold" => 600, "bold" => 700, "extra-bold" => 800, "black" => 900,
            "extra-black" => 950, _ => throw new FormatException("Invalid Windows Terminal font.weight.")
        };
    }
}
