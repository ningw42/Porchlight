using System.Globalization;
using System.Text.Json;

namespace Porchlight.Core.Configuration;

public readonly record struct Rgb(byte R, byte G, byte B);

/// A named palette, independent of font and window chrome. ANSI slots are indexed by SGR,
/// not the order in which a settings editor happened to serialize the scheme's properties.
public sealed record TerminalScheme(string Name, Rgb Foreground, Rgb Background, Rgb Cursor,
    Rgb Selection, IReadOnlyList<Rgb> Ansi)
{
    static readonly string[] Slots = ["black", "red", "green", "yellow", "blue", "purple", "cyan", "white",
        "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightPurple", "brightCyan", "brightWhite"];

    public static TerminalScheme Read(Stream input, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(input, new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (var scheme in doc.RootElement.GetProperty("schemes").EnumerateArray())
            {
                if (scheme.GetProperty("name").GetString() != name) continue;
                Rgb Color(string key)
                {
                    if (!scheme.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.String ||
                        v.GetString() is not { Length: 7 } hex || hex[0] != '#' ||
                        !uint.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
                        throw new FormatException($"Scheme '{name}' needs a #RRGGBB value for '{key}'.");
                    return new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
                }
                return new(name, Color("foreground"), Color("background"), Color("cursorColor"),
                    Color("selectionBackground"), Array.AsReadOnly(Slots.Select(Color).ToArray()));
            }
            throw new FormatException($"No scheme named '{name}' in this settings file's schemes array.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new FormatException("Cannot read the named schemes from this Windows Terminal settings.json."); }
    }
}
