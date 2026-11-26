using System.Text.Json;

namespace Porchlight.Core;

// Only the part of the herdr-app document needed for space order, names and membership.
internal sealed class SpaceLayout
{
    sealed record Definition(string Name, string SavedFolder, string? FirstTerminal);
    // User moves are published as the order of spaces[]; names and IDs do not encode it.
    readonly OrderedDictionary<string, Definition> spaces = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> panes = new(StringComparer.Ordinal);
    public IEnumerable<string> OrderedIds => spaces.Keys;

    public static SpaceLayout Read(JsonElement value)
    {
        var layout = new SpaceLayout();
        foreach (var entry in Items(value, "spaces"))
        {
            if (Text(entry, "id") is not { } id || layout.spaces.ContainsKey(id)) continue;
            string? first = null;
            foreach (var tab in Items(entry, "tabs")) layout.Visit(Property(tab, "root"), id, ref first);
            layout.spaces.Add(id, new(StringValue(entry, "name") ?? "", StringValue(entry, "folder") ?? "", first));
        }
        return layout;
    }

    void Visit(JsonElement node, string spaceId, ref string? first)
    {
        switch (Text(node, "type"))
        {
            case "block":
                var terminal = AddContent(node, spaceId);
                first ??= terminal;
                break;
            case "row": case "column": case "stack":
                foreach (var child in Items(node, "children")) Visit(child, spaceId, ref first);
                break;
        }
        // Floats have membership but are not nodes in HerdR's Node::terminals() walk.
        foreach (var floating in Items(node, "floating")) AddContent(floating, spaceId);
    }

    string? AddContent(JsonElement node, string spaceId)
    {
        var terminal = StringValue(Property(node, "content"), "terminal");
        if (LiveTerminal.IsAddress(terminal)) panes.TryAdd(terminal!, spaceId);
        return terminal;
    }

    // Project names when publishing a catalog, not when reading the layout: a pane's
    // folder can change while the layout document (and the space ID) stays the same.
    public Dictionary<string, TerminalSpace> NameSpaces(Func<string, string?> terminalFolder, string home) =>
        spaces.ToDictionary(kv => kv.Key, kv => {
            var space = kv.Value;
            var name = space.Name.Length != 0 ? space.Name : FolderName(
                (space.FirstTerminal is { } address ? terminalFolder(address) : null) ?? space.SavedFolder, home);
            return new TerminalSpace(kv.Key, name);
        }, StringComparer.Ordinal);

    internal static string FolderName(string path, string home)
    {
        if (path.Length == 0) return "~";
        var trimmed = path.TrimEnd('/', '\\');
        home = home.TrimEnd('/', '\\');
        if (SamePath(trimmed, home)) return "~";
        if (trimmed.Length == 0) return path[..1];
        return trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
    }

    static bool SamePath(string left, string right)
    {
        if (left == right) return true;
        if (!OperatingSystem.IsWindows() || left.Length != right.Length) return false;
        // Rust's eq_ignore_ascii_case folds only A-Z, even within a Unicode path.
        static char Fold(char c) => char.IsAsciiLetterUpper(c) ? (char)(c + 32) : c;
        for (int i = 0; i < left.Length; i++) if (Fold(left[i]) != Fold(right[i])) return false;
        return true;
    }

    public TerminalSpace Resolve(LiveTerminal terminal, IReadOnlyDictionary<string, TerminalSpace> names)
    {
        var address = terminal.Address;
        // A remote document is machine-local. Some gateways also localize terminal fields.
        var id = panes.GetValueOrDefault(address[(address.IndexOf('/') + 1)..]) ?? panes.GetValueOrDefault(address) ?? terminal.Space.Id;
        return id != null && names.TryGetValue(id, out var space) ? space : terminal.Space;
    }

    static JsonElement Property(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var field) ? field : default;
    static IEnumerable<JsonElement> Items(JsonElement obj, string key) =>
        Property(obj, key) is { ValueKind: JsonValueKind.Array } items ? items.EnumerateArray() : [];
    static string? StringValue(JsonElement obj, string key) =>
        Property(obj, key) is { ValueKind: JsonValueKind.String } field ? field.GetString() : null;
    internal static string? Text(JsonElement obj, string key) => StringValue(obj, key) is { } s &&
        !string.IsNullOrWhiteSpace(s) && !s.Any(char.IsControl) ? s : null;
}
