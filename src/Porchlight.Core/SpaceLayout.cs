using System.Text.Json;

namespace Porchlight.Core;

// Only the part of the herdr-app document needed for names, space order and membership.
internal sealed class SpaceLayout
{
    sealed record Definition(string Name, string SavedFolder, string? FirstTerminal);
    // User moves are published as the order of spaces[]; names and IDs do not encode it.
    readonly OrderedDictionary<string, Definition> spaces = new(StringComparer.Ordinal);
    sealed record Placement(string SpaceId, string? TabName);
    readonly Dictionary<string, Placement> panes = new(StringComparer.Ordinal);
    public IEnumerable<string> OrderedIds => spaces.Keys;

    public static SpaceLayout Read(JsonElement value)
    {
        var layout = new SpaceLayout();
        foreach (var entry in Items(value, "spaces"))
        {
            if (Text(entry, "id") is not { } id || layout.spaces.ContainsKey(id)) continue;
            string? first = null;
            foreach (var tab in Items(entry, "tabs"))
                layout.Visit(Property(tab, "root"), new Placement(id, Text(tab, "name")), ref first);
            layout.spaces.Add(id, new(StringValue(entry, "name") ?? "", StringValue(entry, "folder") ?? "", first));
        }
        return layout;
    }

    void Visit(JsonElement node, Placement placement, ref string? first)
    {
        switch (Text(node, "type"))
        {
            case "block":
                var terminal = AddContent(node, placement);
                first ??= terminal;
                break;
            case "row": case "column": case "stack":
                foreach (var child in Items(node, "children")) Visit(child, placement, ref first);
                break;
        }
        // Floats have membership but are not nodes in HerdR's Node::terminals() walk.
        foreach (var floating in Items(node, "floating")) AddContent(floating, placement);
    }

    string? AddContent(JsonElement node, Placement placement)
    {
        var terminal = StringValue(Property(node, "content"), "terminal");
        if (LiveTerminal.IsAddress(terminal)) panes.TryAdd(terminal!, placement);
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

    public (TerminalSpace Space, string? StoredName) Locate(LiveTerminal terminal, IReadOnlyDictionary<string, TerminalSpace> names)
    {
        var address = terminal.Address;
        // A remote document is machine-local. Some gateways also localize terminal fields.
        var pane = panes.GetValueOrDefault(address[(address.IndexOf('/') + 1)..]) ?? panes.GetValueOrDefault(address);
        var id = pane?.SpaceId ?? terminal.Space.Id;
        var space = id != null && names.TryGetValue(id, out var saved) ? saved : terminal.Space;
        // A placed but unnamed tab follows its program, even if hc run supplied a launch name.
        return (space, pane == null ? terminal.Name : pane.TabName);
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
