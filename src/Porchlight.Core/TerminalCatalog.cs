using System.Text.Json;

namespace Porchlight.Core;

public sealed record LiveTerminal(string Address, string? Name)
{
    public TerminalSpace Space { get; init; } = TerminalSpace.Unassigned;
    public string Title { get; init; } = Name ?? Address;
    // The compact header is just Title. Details/automation also disambiguate identical captions.
    public string Label => Name == null || Name == Title ? $"{Address} · {Title}" : $"{Address} · {Name} · {Title}";
    // The gateway mirrors tN (local) and alias/tN (directly configured machine).
    public string? MachineAlias => Address.IndexOf('/') is >= 0 and var slash ? Address[..slash] : null;
    public override string ToString() => Label;
    internal static bool IsAddress(string? address)
    {
        if (IsLocal(address)) return true;
        int slash = address?.IndexOf('/') ?? -1;
        return slash > 0 && MachineDirectory.ValidAlias(address![..slash]) && IsLocal(address[(slash + 1)..]);
    }
    // HerdR Core's id_shaped: t1..t9, then t10..t1z, etc. No vowels or lowercase L.
    const string IdAlphabet = "0123456789bcdfghjkmnpqrstvwxyz";
    internal static bool IsLocal(string? id) => id is { Length: >= 2 } && id[0] == 't' && id[1] is >= '1' and <= '9' &&
        id.AsSpan(2).IndexOfAnyExcept(IdAlphabet) < 0;
}

// Single reader. Derive names at ingestion; never retain command lines or agent reports.
internal sealed class TerminalCatalog(string? viewerHome = null, ProgramNames? programNames = null)
{
    readonly string home = viewerHome ?? ViewerHome();
    readonly ProgramNames programs = programNames ?? ProgramNames.Load();
    static string ViewerHome()
    {
        var userHome = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME");
        if (!string.IsNullOrEmpty(userHome)) return userHome;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length != 0 ? profile : "/";
    }

    sealed record Entry(LiveTerminal Terminal, string? LaunchFolder, string? ForegroundFolder,
        ProgramName Launch, ProgramName? Foreground, ProgramName? Agent, string? TitleCandidate)
    {
        public string? Folder => ForegroundFolder ?? LaunchFolder;
        public LiveTerminal Describe(TerminalSpace space, string? stored) => Terminal with {
            Space = space, Title = TabTitles.Format(Terminal.Address, stored, Agent ?? Foreground ?? Launch, Agent != null, TitleCandidate)
        };
    }
    readonly Dictionary<string, Entry> live = new(StringComparer.Ordinal);
    readonly MemoryStream partial = new();
    ulong seq;
    public event Action<string?>? DocumentChanged;
    public event Action? DocumentsMissed;

    public MachineDirectory Directory(IReadOnlyList<HerdMachine> machines, IReadOnlyDictionary<string, SpaceLayout>? layouts = null,
        Func<HerdMachine, string, string?>? checkoutTop = null)
    {
        var visible = new List<LiveTerminal>();
        var layoutSpaces = new Dictionary<string, TerminalSpace[]>();
        var byMachine = live.Values.ToLookup(e => e.Terminal.MachineAlias, StringComparer.Ordinal);
        foreach (var machine in machines.Where(m => m.Available))
        {
            var terminals = byMachine[machine.IsLocal ? null : machine.Name];
            if (layouts?.GetValueOrDefault(machine.Id) is not { } layout)
            {
                visible.AddRange(terminals.Select(e => e.Describe(e.Terminal.Space, e.Terminal.Name)));
                continue;
            }
            var names = layout.NameSpaces(address => {
                var cwd = PaneFolder(machine, address);
                return cwd == null || machine.IsLocal && cwd == home ? cwd : checkoutTop?.Invoke(machine, cwd) ?? cwd;
            }, home);
            layoutSpaces[machine.Id] = layout.OrderedIds.Select(id => names[id]).ToArray();
            foreach (var entry in terminals)
            {
                var (space, stored) = layout.Locate(entry.Terminal, names);
                visible.Add(entry.Describe(space, stored));
            }
        }
        visible.Sort((a, b) => string.CompareOrdinal(a.Address, b.Address));
        return new(machines, visible.AsReadOnly()) { LayoutSpaces = layoutSpaces, ExpectsLayouts = layouts != null };
    }

    string? PaneFolder(HerdMachine machine, string address)
    {
        // Join a document only to its owning machine. Some gateways have already qualified the address.
        if (LiveTerminal.IsLocal(address)) address = machine.IsLocal ? address : $"{machine.Name}/{address}";
        else if (machine.IsLocal || !address.StartsWith(machine.Name + "/", StringComparison.Ordinal)) return null;
        return live.GetValueOrDefault(address)?.Folder;
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
        var item = new LiveTerminal(id!, SpaceLayout.Text(t, "name")) {
            Space = t.TryGetProperty("labels", out var labels) ? LabelSpace(labels) : TerminalSpace.Unassigned
        };
        t.TryGetProperty("foreground", out var fg);
        var entry = new Entry(item, Folder(t), Folder(fg), programs.FromArgv(t) ?? programs.Shell, programs.FromArgv(fg),
            Agent(t), TabTitles.Candidate(SpaceLayout.Text(t, "title")));
        if (live.GetValueOrDefault(id!) == entry) return false;
        live[id!] = entry;
        return true;
    }

    static string? Folder(JsonElement record) => record.ValueKind == JsonValueKind.Object &&
        record.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String ? cwd.GetString() : null;

    ProgramName? Agent(JsonElement data) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty("status", out var status) &&
        SpaceLayout.Text(status, "app") is { } app ? programs.Agent(app) : null;

    bool Observe(string type, string address, JsonElement data)
    {
        if (!live.TryGetValue(address, out var prior)) return false;
        var updated = type switch {
            "foreground" => prior with { ForegroundFolder = Folder(data), Foreground = programs.FromArgv(data) },
            "status" => prior with { Agent = Agent(data) },
            "title" => prior with { TitleCandidate = TabTitles.Candidate(SpaceLayout.Text(data, "title")) },
            _ => prior
        };
        if (updated == prior) return false;
        live[address] = updated;
        // Keep hidden observations for later (e.g. the agent clears its status); don't redraw for them.
        return updated.Folder != prior.Folder || updated.Agent != prior.Agent ||
            (updated.Agent == null ? updated.Foreground != prior.Foreground : updated.TitleCandidate != prior.TitleCandidate);
    }

    static TerminalSpace LabelSpace(JsonElement labels) => TerminalSpace.FromLabel(SpaceLayout.Text(labels, "herdr.space"));

    bool Relabel(string address, JsonElement data)
    {
        if (!live.TryGetValue(address, out var prior) || !data.TryGetProperty("labels", out var labels) ||
            labels.ValueKind != JsonValueKind.Object) return false;
        var space = LabelSpace(labels); // The event replaces the entire label map.
        if (prior.Terminal.Space == space) return false;
        live[address] = prior with { Terminal = prior.Terminal with { Space = space } };
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
                Snapshot(p); DocumentsMissed?.Invoke(); return true;
            case "event":
                ulong next = p.GetProperty("seq").GetUInt64();
                if (next <= seq) return false;
                seq = next;
                var type = p.GetProperty("type").GetString();
                if (type == "doc")
                {
                    if (SpaceLayout.Text(p.GetProperty("data"), "name") == "herdr-app")
                    {
                        if (!p.TryGetProperty("machine", out var origin) || origin.ValueKind == JsonValueKind.Null) DocumentChanged?.Invoke(null);
                        else if (origin.ValueKind == JsonValueKind.String && MachineDirectory.ValidAlias(origin.GetString())) DocumentChanged?.Invoke(origin.GetString());
                    }
                    return false;
                }
                if (!p.TryGetProperty("terminal", out var address) || !LiveTerminal.IsAddress(address.GetString())) return false;
                return type switch {
                    "labels" => Relabel(address.GetString()!, p.GetProperty("data")),
                    "foreground" or "status" or "title" => Observe(type, address.GetString()!, p.GetProperty("data")),
                    "created" or "restarted" when p.GetProperty("data").GetProperty("address").GetString() == address.GetString() => Update(p.GetProperty("data")),
                    "removed" or "finished" or "lost" => live.Remove(address.GetString()!),
                    _ => false
                };
            default: return false; // heartbeat or additive protocol extension
        }
    }
}
