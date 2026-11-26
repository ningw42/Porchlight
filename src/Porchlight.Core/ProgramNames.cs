using System.Text;
using System.Text.Json;

namespace Porchlight.Core;

internal sealed record ProgramName(string Key, string Name);

// Name and alias facts in ProgramNames.json: herdrdev/herdr-app v0.1.18,
// defaults/programs.json (Apache-2.0). No artwork or launch commands are embedded.
// This lookup is independently implemented against that version's src/programs.rs.
internal sealed class ProgramNames
{
    sealed record Entry(string? Name, string[] Aliases);
    sealed record Table(string[] Shells, string[] Interpreters, Dictionary<string, Entry> Programs);

    readonly HashSet<string> shells, interpreters;
    readonly Dictionary<string, ProgramName> keys = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ProgramName> aliases = new(StringComparer.OrdinalIgnoreCase);
    public ProgramName Shell { get; }

    ProgramNames(Table table, Table? overlay)
    {
        shells = [.. table.Shells];
        interpreters = [.. table.Interpreters];
        if (overlay != null)
        {
            shells.UnionWith(overlay.Shells);
            interpreters.UnionWith(overlay.Interpreters);
            foreach (var (key, entry) in overlay.Programs)
            {
                table.Programs.TryGetValue(key, out var prior);
                table.Programs[key] = new(entry.Name ?? prior?.Name,
                    entry.Aliases.Length > 0 ? entry.Aliases : prior?.Aliases ?? []);
            }
        }
        Shell = new("shell", table.Programs["shell"].Name ?? "shell");
        // Herdr uses the first matching key in lexical order when names collide.
        foreach (var (key, entry) in table.Programs.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var program = new ProgramName(key, entry.Name ?? key);
            keys.TryAdd(key, program);
            foreach (var alias in entry.Aliases) aliases.TryAdd(alias, program);
        }
    }

    public static ProgramNames Load()
    {
        static string? Env(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : null;
        static string Home() => Env(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") ??
            (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } profile ? profile : "/");
        var dir = Env("HERDR_APP_HOME") ?? Path.Combine(Env("XDG_CONFIG_HOME") ?? Path.Combine(Home(), ".config"), "herdr-app");
        return Load(Path.Combine(dir, "programs.json"));
    }

    public static ProgramNames Load(string? configPath)
    {
        using var embedded = typeof(ProgramNames).Assembly.GetManifestResourceStream("Porchlight.Core.ProgramNames.json")
            ?? throw new InvalidOperationException("Built-in program names are missing.");
        var table = Read(embedded);
        Table? overlay = null;
        if (configPath != null)
        {
            try
            {
                using var file = File.OpenRead(configPath);
                overlay = Read(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
                NotSupportedException or JsonException or InvalidOperationException)
            { /* Absent, unreadable or invalid personal table: keep the built-in table intact. */ }
        }
        return new(table, overlay);
    }

    static Table Read(Stream input)
    {
        using var doc = JsonDocument.Parse(input);
        var root = doc.RootElement;
        static string[] Strings(JsonElement obj, string field) => obj.TryGetProperty(field, out var array)
            ? array.EnumerateArray().Select(v => v.GetString() ?? throw new JsonException()).ToArray() : [];
        var table = new Table(Strings(root, "shells"), Strings(root, "interpreters"), new(StringComparer.Ordinal));
        if (root.TryGetProperty("programs", out var programs))
            foreach (var property in programs.EnumerateObject())
            {
                var entry = property.Value;
                // A malformed Herdr entry invalidates the overlay, even if its bad field
                // isn't used for naming. Check those fields without importing their values.
                foreach (var field in entry.EnumerateObject())
                    if (field.Name switch
                    {
                        "icon" or "tile" or "color" => field.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null),
                        "tint" => field.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null),
                        "run" => field.Value.ValueKind != JsonValueKind.Array || field.Value.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String),
                        _ => false
                    }) throw new JsonException();
                table.Programs[property.Name] = new(entry.TryGetProperty("name", out var name) ? name.GetString() : null,
                    Strings(entry, "match"));
            }
        return table;
    }

    ProgramName? Find(string text)
    {
        var key = keys.GetValueOrDefault(text);
        // The generic agent key yields to an alias (notably Cursor's CLI).
        return key == null || key.Key == "agent" ? aliases.GetValueOrDefault(text) ?? key : key;
    }

    public ProgramName Agent(string app) => app.Length == 0 ? Shell : Find(app) ?? new("agent", app);

    public ProgramName? FromArgv(JsonElement record)
    {
        if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("argv", out var array) ||
            array.ValueKind != JsonValueKind.Array) return null;
        var argv = array.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToArray();
        return argv.Length == 0 ? null : Resolve(argv);
    }

    static string Basename(string word)
    {
        var name = word[(word.LastIndexOfAny(['/', '\\']) + 1)..].TrimStart('-');
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    static string ScriptName(string word)
    {
        var name = Basename(word);
        int dot = name.LastIndexOf('.');
        return dot >= 0 && name[dot..] is ".js" or ".mjs" or ".cjs" or ".ts" or ".py" or ".rb" ? name[..dot] : name;
    }

    ProgramName Resolve(ReadOnlySpan<string> argv)
    {
        while (!argv.IsEmpty)
        {
            string first = Basename(argv[0]);
            // Shell and interpreter membership, unlike program/alias lookup, is case-sensitive.
            if (shells.Contains(first))
            {
                if (argv.Length > 4 && argv[1].StartsWith('-') && argv[1].EndsWith('c') &&
                    (argv[2].Contains("\"$@\"", StringComparison.Ordinal) ||
                     argv[2] is "command $argv[2..]; exec $argv[1] -i" or "command $argv[2..]; exec $argv[1] -li"))
                {
                    argv = argv[4..];
                    continue;
                }
                if (argv.Length == 4 && argv[1] == "-NoExit" && argv[2] == "-Command" && QuotedWords(argv[3]) is { } words)
                {
                    argv = words;
                    continue;
                }
            }
            if (interpreters.Contains(first))
                foreach (var arg in argv[1..])
                    if (!arg.StartsWith('-') && Find(ScriptName(arg)) is { } match && !interpreters.Contains(match.Key))
                        return match;
            return Find(first) ?? new(shells.Contains(first) ? "shell" : "program", first);
        }
        return Shell;
    }

    // Decode only Herdr's & 'word' 'word' convention, not PowerShell syntax or expansions.
    static string[]? QuotedWords(string line)
    {
        if (!line.StartsWith("& ", StringComparison.Ordinal)) return null;
        static bool Quote(char ch) => ch is '\'' or '\u2018' or '\u2019' or '\u201a' or '\u201b';
        List<string> words = [];
        for (int i = 2; i < line.Length;)
        {
            if (line[i] == ' ') { i++; continue; }
            if (!Quote(line[i++])) return null;
            var word = new StringBuilder();
            while (i < line.Length)
            {
                char ch = line[i++];
                if (Quote(ch))
                {
                    if (i == line.Length || !Quote(line[i])) break;
                    ch = line[i++];
                }
                word.Append(ch);
            }
            words.Add(word.ToString());
        }
        return [.. words];
    }
}
