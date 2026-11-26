using System.Globalization;
using System.Text;

namespace Porchlight.Core;

// Herdr's default tabs mod, applied to one terminal rather than to the lead pane of a split.
internal static class TabTitles
{
    public static string Format(string address, string? stored, ProgramName program, bool agent, string? candidate)
    {
        if (agent && (stored == null || stored == program.Name) && candidate != null && candidate != address &&
            candidate != program.Name && candidate.ToLowerInvariant() != program.Key) return candidate;
        return stored ?? program.Name;
    }

    // Keep only a possible display title (not status.title, screen contents or a spinning frame).
    public static string? Candidate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Any(char.IsControl)) return null;
        string shown = WithoutSpinner(raw);
        var path = SkipDecoration(shown, path: true);
        if (path is ['~' or '/', ..] || path is [var drive, ':', '\\', ..] && char.IsAsciiLetter(drive)) return null;
        var title = SkipDecoration(shown, path: false);
        if (title.StartsWith("[ ! ] ") || title.StartsWith("[ . ] ")) title = title[6..];
        title = title.Trim();
        return title.IsEmpty || title.Contains('@') ? null : title.ToString();
    }

    static ReadOnlySpan<char> SkipDecoration(string s, bool path)
    {
        int offset = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            if (Rune.IsLetter(rune) || Rune.IsNumber(rune) || (path ? rune.Value is '~' or '/' :
                Rune.GetUnicodeCategory(rune) is UnicodeCategory.OpenPunctuation or UnicodeCategory.InitialQuotePunctuation)) break;
            offset += rune.Utf16SequenceLength;
        }
        return s.AsSpan(offset);
    }

    static string WithoutSpinner(string title)
    {
        static bool Braille(char c) => c is >= '\u2800' and <= '\u28ff';
        static bool Frame(char c) => Braille(c) || c is '·' or '✢' or '✳' or '✶' or '✻' or '✽' or >= '◐' and <= '◓';
        int i = 0; bool spinner = false;
        while (i < title.Length && (Frame(title[i]) || char.IsWhiteSpace(title[i])))
        {
            spinner |= Frame(title[i]);
            i++;
        }
        if (spinner && i < title.Length && char.IsWhiteSpace(title[i - 1])) return title[i..];
        // Pi also puts a braille spinner *after* its short prefix: “π ⠹ task” → “π task”.
        int space = title.IndexOf(' ');
        if (space is <= 0 or > 6) return title;
        var prefix = title[..space];
        if (prefix.EnumerateRunes().Count() > 3 || prefix.All(Braille)) return title;
        i = space + 1;
        while (i < title.Length && Braille(title[i])) i++;
        if (i == space + 1 || i < title.Length && title[i] != ' ') return title;
        var rest = title.AsSpan(i).TrimStart();
        return rest.IsEmpty ? prefix : $"{prefix} {rest}";
    }
}
