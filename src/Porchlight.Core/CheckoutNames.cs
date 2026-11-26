using System.Text;
using System.Text.Json;

namespace Porchlight.Core;

// Reader-owned, session-local cache. Only first panes of unnamed spaces participate.
// Keys include the stable machine ID; the gateway runs this one Git operation as argv,
// never a shell line, in its normal working directory (not inside the checkout).
internal sealed class CheckoutNames
{
    sealed record Location(string Machine, bool Local, string Folder);
    sealed class Probe { public string? Top; public bool NeedsRead = true; public long Checked; }
    readonly Dictionary<Location, Probe> cache = [];
    readonly Dictionary<int, Probe> inFlight = [];
    readonly HashSet<Location> visible = [];

    public string? Lookup(HerdMachine machine, string folder)
    {
        if (!CanProbe(folder)) return null;
        var key = new Location(machine.Id, machine.IsLocal, folder);
        visible.Add(key);
        if (!cache.TryGetValue(key, out var probe)) cache.Add(key, probe = new());
        // Recheck known checkouts on later catalog activity, e.g. after a worktree move.
        if (probe.Top != null && Environment.TickCount64 - probe.Checked >= 60_000) probe.NeedsRead = true;
        return probe.Top;
    }

    static bool CanProbe(string folder)
    {
        if (folder.Length is 0 or > 32767 || folder.Any(char.IsControl)) return false;
        // An observed cwd is data, not an instruction to contact a third host or open a device.
        static bool DrivePath(ReadOnlySpan<char> path) => path.Length >= 3 && char.IsAsciiLetter(path[0]) &&
            path[1] == ':' && path[2] is '\\' or '/';
        if (folder.StartsWith(@"\\?\", StringComparison.Ordinal)) return DrivePath(folder.AsSpan(4));
        return DrivePath(folder) || folder.StartsWith('/') && (folder.Length == 1 || folder[1] is not ('/' or '\\'));
    }

    public void Refresh() { foreach (var probe in cache.Values) probe.NeedsRead = true; }

    public void FinishProjection(Func<int> nextId, Action<Frame> send)
    {
        foreach (var key in cache.Keys.Where(k => !visible.Contains(k)).ToArray()) cache.Remove(key);
        visible.Clear();
        foreach (var (key, probe) in cache)
        {
            if (inFlight.Count >= 4) break;
            if (!probe.NeedsRead || inFlight.ContainsValue(probe)) continue;
            probe.NeedsRead = false;
            probe.Checked = Environment.TickCount64;
            int id = nextId();
            inFlight.Add(id, probe);
            string[] argv = ["git", "-C", key.Folder, "rev-parse", "--show-toplevel", "--abbrev-ref", "HEAD"];
            object args = key.Local ? new { argv, timeout = 30 } : new { argv, timeout = 30, on = key.Machine };
            send(new(0, Kind.Data, Wire.Request(id, "machine.exec", args)));
        }
    }

    public bool Receive(int id, JsonElement reply)
    {
        if (!inFlight.Remove(id, out var probe)) return false;
        // If the pane moved (even away and back), this old Probe is no longer in cache.
        probe.Checked = Environment.TickCount64;
        probe.Top = !reply.TryGetProperty("error", out _) && reply.TryGetProperty("result", out var result) ? ReadTop(result) : null;
        return true;
    }

    static string? ReadTop(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("code", out var code) ||
            code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out var exit) || exit != 0 ||
            Wire.Flag(result, "timed_out") || Wire.Flag(result, "truncated") ||
            !result.TryGetProperty("stdout", out var text) || text.ValueKind != JsonValueKind.String ||
            text.GetString() is not { Length: > 0 and <= 262144 } encoded) return null;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { return null; }
        // Match the official two-line success condition, including linked worktrees and detached HEAD.
        using var lines = new StringReader(Encoding.UTF8.GetString(bytes));
        var top = lines.ReadLine();
        return top is { Length: > 0 } && !top.Any(char.IsControl) && lines.ReadLine() != null && lines.ReadLine() == null ? top : null;
    }
}
