using System.Text.Json;

namespace Porchlight.Core;

// Keep just picker metadata. SSH targets, via commands, error detail and login data
// belong to the gateway; Porchlight never dials a machine itself.
public sealed record HerdMachine(string Id, string Name, string State, bool IsLocal)
{
    public bool Available => State == "connected";
    public string DisplayName => IsLocal ? "Local" : Name;
    public override string ToString() => DisplayName;
}

public sealed record TerminalSpace(string? Id, string DisplayName)
{
    public static TerminalSpace Unassigned { get; } = new(null, "Unassigned");
    internal static TerminalSpace FromLabel(string? id) => id == null ? Unassigned : new(id, id);
    public override string ToString() => DisplayName;
}

public sealed class MachineDirectory(IReadOnlyList<HerdMachine> machines, IReadOnlyList<LiveTerminal> terminals)
{
    public IReadOnlyList<HerdMachine> Machines { get; } = machines;
    public IReadOnlyList<LiveTerminal> Terminals { get; } = terminals;
    internal IReadOnlyDictionary<string, TerminalSpace[]> LayoutSpaces { get; init; } = new Dictionary<string, TerminalSpace[]>();
    readonly ILookup<string?, LiveTerminal> byMachine = terminals.ToLookup(t => t.MachineAlias, StringComparer.Ordinal);
    IEnumerable<LiveTerminal> On(HerdMachine machine) => machine.Available ? byMachine[machine.IsLocal ? null : machine.Name] : [];

    public IReadOnlyList<TerminalSpace> SpacesFor(HerdMachine machine)
    {
        if (!machine.Available) return [];
        var saved = LayoutSpaces.GetValueOrDefault(machine.Id) ?? [];
        var ids = saved.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var unsaved = On(machine).Select(t => t.Space).Where(s => ids.Add(s.Id))
            .OrderBy(s => s.Id == null).ThenBy(s => s.Id, StringComparer.Ordinal);
        return [.. saved, .. unsaved];
    }
    public IReadOnlyList<LiveTerminal> TerminalsIn(HerdMachine machine, TerminalSpace space) =>
        On(machine).Where(t => t.Space.Id == space.Id).ToArray();

    internal static IReadOnlyList<HerdMachine> ReadMachines(CoreIdentity gateway, JsonElement? listing)
    {
        var machines = new List<HerdMachine> { new(gateway.Id, gateway.Name, "connected", true) };
        if (listing is { } result)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal) { gateway.Id };
            var names = new HashSet<string>(StringComparer.Ordinal) { gateway.Name };
            foreach (var entry in result.GetProperty("machines").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString();
                var name = entry.GetProperty("name").GetString();
                if (string.IsNullOrEmpty(id) || id.Contains('/') || id.Any(char.IsControl) ||
                    !ValidAlias(name) || !ids.Add(id) || !names.Add(name!)) throw new InvalidDataException("Invalid machine directory.");
                var state = entry.GetProperty("state").GetString();
                if (state is not ("connected" or "connecting" or "needs_login" or "needs_update" or "unreachable")) state = "unavailable";
                machines.Add(new(id, name!, state, false));
            }
        }
        return Array.AsReadOnly(machines.OrderByDescending(m => m.IsLocal).ThenBy(m => m.Name, StringComparer.Ordinal).ToArray());
    }

    internal static bool ValidAlias(string? s) => s is { Length: > 0 and <= 32 } && char.IsAsciiLetterOrDigit(s[0]) &&
        s.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '-' or '_') && !LiveTerminal.IsLocal(s);
}
