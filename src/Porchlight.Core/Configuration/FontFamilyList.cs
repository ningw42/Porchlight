using System.Text;

namespace Porchlight.Core.Configuration;

/// Windows Terminal's FontFace grammar (til::iterate_font_families), for picking and previewing
/// individual families. Imported strings still go to the terminal verbatim.
public static class FontFamilyList
{
    public static IEnumerable<string> Parse(string face)
    {
        var name = new StringBuilder();
        char quote = '\0';
        bool escaped = false, space = false;
        foreach (var ch in face)
        {
            if (!escaped)
            {
                if (ch == '\\') { escaped = true; continue; }
                if ((ch is '\'' or '"') && (quote == '\0' || quote == ch))
                {
                    quote = quote == ch ? '\0' : ch;
                    continue;
                }
                if (quote == '\0' && ch == ' ') { space = name.Length != 0; continue; }
                if (quote == '\0' && ch == ',')
                {
                    if (name.Length != 0) { yield return name.ToString(); name.Clear(); }
                    space = false;
                    continue;
                }
            }
            if (space) { name.Append(' '); space = false; }
            name.Append(ch);
            escaped = false;
        }
        if (quote == '\0' && name.Length != 0) yield return name.ToString();
    }

    public static string Format(IEnumerable<string> names) => string.Join(", ", names.Select(name =>
        name.IndexOfAny([',', '"', '\'', '\\']) >= 0 || name != name.Trim(' ') || name.Contains("  ", StringComparison.Ordinal)
            ? "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : name));
}
