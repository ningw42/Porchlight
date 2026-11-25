using System.Text.Json;

namespace Porchlight.Core;

// Keep just picker metadata. SSH targets, via commands, error detail and login data
// belong to the gateway; Porchlight never dials a machine itself.
public sealed record HerdMachine(string Id, string Name, string State, bool IsLocal)
{
    public bool Available => State == "connected";
    public string Label => IsLocal ? $"{Name} (local)" : $"{Name} · {State.Replace('_', ' ')}";
    public override string ToString() => Label;
}

public sealed record MachineDirectory(IReadOnlyList<HerdMachine> Machines, IReadOnlyList<LiveTerminal> Terminals)
{
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
