namespace Porchlight.Core;

// Owned by one viewer window. Keep IDs, not tab indexes, captions, or live attachments.
public sealed class SpaceTabMemory
{
    readonly Dictionary<(string CoreId, string? SpaceId), string> lastTab = new();

    public void Remember(HerdMachine machine, TerminalSpace space, string address) =>
        lastTab[(machine.Id, space.Id)] = address[(address.IndexOf('/') + 1)..];

    public string? Enter(HerdMachine machine, TerminalSpace space, IReadOnlyList<LiveTerminal> liveTabs)
    {
        var key = (machine.Id, space.Id);
        if (liveTabs.Count == 0) { lastTab.Remove(key); return null; }
        if (lastTab.TryGetValue(key, out var id))
        {
            // A remote alias may have changed; resolve the ID on its owning core's current route.
            var address = machine.IsLocal ? id : $"{machine.Name}/{id}";
            if (liveTabs.Any(t => t.Address == address)) return address;
        }
        var first = liveTabs[0].Address;
        Remember(machine, space, first);
        return first;
    }
}
