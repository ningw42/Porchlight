namespace Porchlight.Core.Configuration;

/// The optional OpenType mechanisms covered by the coding-ligature switch. calt is
/// included because coding fonts use contextual alternates for multi-cell operators.
public static class CodingLigatures
{
    public static IReadOnlyList<string> DefaultOnTags { get; } = Array.AsReadOnly<string>(["calt", "liga", "clig"]);
    public static IReadOnlyList<string> Tags { get; } = Array.AsReadOnly<string>([.. DefaultOnTags, "dlig", "hlig"]);

    // Required ligatures belong to script shaping, not either user-facing selector.
    public static bool IsExcludedFromPicker(string tag) => tag == "rlig" || Tags.Contains(tag);

    // Missing common ligature tags are default-on in the terminal; discretionary and
    // historical ligatures are opt-in. A mixed launch map is left intact until edited.
    public static bool IsEnabled(IReadOnlyDictionary<string, float> values) => Tags.Any(tag =>
        values.TryGetValue(tag, out var value) ? value != 0 : DefaultOnTags.Contains(tag));
}
