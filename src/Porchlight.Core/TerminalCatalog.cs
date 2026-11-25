using System.Text.Json;

namespace Porchlight.Core;

public sealed record LiveTerminal(string Address, string? Name)
{
    public string Label => string.IsNullOrEmpty(Name) ? Address : $"{Address} · {Name}";
    // The gateway mirrors tN (local) and alias/tN (directly configured machine).
    public string? MachineAlias => Address.IndexOf('/') is >= 0 and var slash ? Address[..slash] : null;
    public override string ToString() => Label;
    internal static bool IsAddress(string? address)
    {
        if (IsLocal(address)) return true;
        int slash = address?.IndexOf('/') ?? -1;
        return slash > 0 && MachineDirectory.ValidAlias(address![..slash]) && IsLocal(address[(slash + 1)..]);
    }
    internal static bool IsLocal(string? id) => id is { Length: > 1 } && id[0] == 't' && id.AsSpan(1).IndexOfAnyExceptInRange('0','9') < 0;
}

// Single reader. Expose only the fields the picker needs, never screen/title/argv/status text.
internal sealed class TerminalCatalog
{
    readonly Dictionary<string, LiveTerminal> live = new(StringComparer.Ordinal);
    readonly MemoryStream partial = new();
    ulong seq;
    public MachineDirectory Directory(IReadOnlyList<HerdMachine> machines)
    {
        var online = machines.Where(m => !m.IsLocal && m.Available).Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var visible = live.Values.Where(t => t.MachineAlias == null || online.Contains(t.MachineAlias))
            .OrderBy(t => t.Address, StringComparer.Ordinal).ToArray();
        return new(machines, Array.AsReadOnly(visible));
    }

    public void Snapshot(JsonElement p)
    {
        seq = p.GetProperty("seq").GetUInt64();
        live.Clear();
        foreach (var t in p.GetProperty("terminals").EnumerateArray()) Update(t);
    }

    bool Update(JsonElement t)
    {
        var id = t.GetProperty("address").GetString();
        if (!LiveTerminal.IsAddress(id)) return false;
        if (t.GetProperty("state").GetString() != "live" || (t.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String))
            return live.Remove(id!);
        var item = new LiveTerminal(id!, t.TryGetProperty("name", out var n) ? n.GetString() : null);
        if (live.GetValueOrDefault(id!) == item) return false;
        live[id!] = item;
        return true;
    }

    public bool Consume(ReadOnlySpan<byte> bytes)
    {
        bool changed = false;
        while (!bytes.IsEmpty)
        {
            int newline = bytes.IndexOf((byte)'\n');
            var segment = newline < 0 ? bytes : bytes[..newline];
            if (partial.Length + segment.Length > Wire.MaxFrame) throw new InvalidDataException("Oversized event line.");
            partial.Write(segment);
            if (newline < 0) break;
            changed |= Apply(Wire.Parse(partial.GetBuffer().AsMemory(0, (int)partial.Length)));
            partial.SetLength(0);
            bytes = bytes[(newline + 1)..];
        }
        return changed;
    }

    bool Apply(JsonElement msg)
    {
        var p = msg.GetProperty("params");
        switch (msg.GetProperty("method").GetString())
        {
            case "missed":
                if (p.GetProperty("seq").GetUInt64() < seq) return false;
                Snapshot(p); return true;
            case "event":
                ulong next = p.GetProperty("seq").GetUInt64();
                if (next <= seq) return false;
                seq = next;
                if (!p.TryGetProperty("terminal", out var address) || !LiveTerminal.IsAddress(address.GetString())) return false;
                return p.GetProperty("type").GetString() switch {
                    "created" or "restarted" when p.GetProperty("data").GetProperty("address").GetString() == address.GetString() => Update(p.GetProperty("data")),
                    "removed" or "finished" or "lost" => live.Remove(address.GetString()!),
                    _ => false
                };
            default: return false; // heartbeat or additive protocol extension
        }
    }
}
