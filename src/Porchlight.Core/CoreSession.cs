using System.Text.Json;
using System.Threading.Channels;

namespace Porchlight.Core;

public sealed record CoreIdentity(string Name, string Id, string Version);
public enum EndKind { Detached, Exited, Disconnected }
public sealed record SessionEnd(EndKind Kind, string Message);

/// One existing pipe and catalog, with at most one viewer lease at a time.
/// Connection loss, unlike the end of a terminal stream, requires a new session.
public sealed class CoreSession : IAsyncDisposable
{
    readonly Stream pipe, reader;
    readonly CancellationTokenSource stop = new();
    readonly TerminalCatalog catalog = new();
    readonly object gate = new();
    // A single writer merges connection and lease frames in enqueue order. The signal
    // carries no data: retiring a lease discards its entire backlog, including credits.
    readonly Channel<bool> writable = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true });
    readonly Outbox control = new();
    long writeOrder;
    readonly TaskCompletionSource<SessionEnd> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TerminalAttachment? active;
    Timer? deadline;
    uint events;
    string? fault;
    bool helloAuto, closing, finished;
    const int MachineListId = 4;
    IReadOnlyList<HerdMachine> machines = [];
    MachineDirectory directory = new([], []);
    bool refreshingMachines, supportsDocuments, supportsCheckout;
    // Odd IDs are unique user actions (attach or kill), allocated under gate.
    // Even IDs >= 6 remain reader-owned metadata requests.
    int nextActionId = 3, nextMetadataId = 6;
    int NextActionId() { int id = nextActionId; nextActionId = checked(id + 2); return id; }
    (int Id, TaskCompletionSource<bool> Ack)? pendingKill;
    readonly CheckoutNames checkoutNames = new();
    int NextMetadataId() { int id = nextMetadataId; nextMetadataId = checked(id + 2); return id; }
    readonly Dictionary<string, SpaceLayout> layouts = new();
    readonly Dictionary<int, HerdMachine> documentRequests = new();
    readonly Dictionary<string, int> pendingDocuments = new();
    readonly HashSet<string> dirtyDocuments = new();
    public bool SupportsMachineList { get; private set; }
    public bool SupportsKill { get; private set; }
    public MachineDirectory Directory => Volatile.Read(ref directory);
    public IReadOnlyList<LiveTerminal> Terminals => Directory.Terminals;
    public CoreIdentity Identity { get; private set; } = null!;
    public Task<SessionEnd> Completion => ended.Task;
    public event Action? CatalogChanged; // on the reader; marshal to UI, then read Terminals

    CoreSession(Stream pipe) { this.pipe = pipe; reader = new BufferedStream(pipe, 16384); }

    public static async Task<CoreSession> ConnectAsync(CoreEndpoint endpoint, CancellationToken token = default)
    {
        using var setup = CancellationTokenSource.CreateLinkedTokenSource(token);
        setup.CancelAfter(TimeSpan.FromSeconds(10));
        CoreSession? session = null;
        try
        {
            session = new(await endpoint.OpenAsync(setup.Token).ConfigureAwait(false));
            await session.Negotiate(setup.Token).ConfigureAwait(false);
            _ = Task.Run(session.Run);
            return session;
        }
        catch (Exception e)
        {
            if (session != null) { session.DisposePipe(); session.stop.Dispose(); }
            token.ThrowIfCancellationRequested();
            throw e switch {
                CoreException safe => safe,
                TimeoutException or OperationCanceledException => new CoreException("Core did not answer. Check that HerdR is running at the selected pipe, then reconnect.") { IsConnectionTimeout = true },
                UnauthorizedAccessException => new CoreException("Access denied. Run as the core's desktop user and verify its pipe."),
                _ => new CoreException("Cannot negotiate with this pipe. Verify the endpoint and that the running core supports generation 1 framed events and attach.")
            };
        }
    }

    async Task Negotiate(CancellationToken ct)
    {
        await pipe.WriteAsync(Wire.Request(1, "hello", new { generation = 1, framed = true }), ct).ConfigureAwait(false);
        await pipe.WriteAsync("\n"u8.ToArray(), ct).ConfigureAwait(false);
        var h = Wire.Result(await Wire.LineAsync(reader, ct).ConfigureAwait(false), 1, "hello");
        if (h.GetProperty("generation").GetInt32() != 1 || !Wire.Flag(h,"framed") ||
            !Wire.Contains(h,"methods","events.subscribe") || !Wire.Contains(h,"methods","terminal.attach"))
            throw new CoreException("Incompatible core. Select a running generation 1 core with framed events.subscribe and terminal.attach.");
        var core = h.GetProperty("core");
        if (core.GetProperty("id").GetString() is not { Length: > 0 } id) throw new InvalidDataException();
        Identity = new(core.GetProperty("name").GetString() ?? "unnamed", id, h.GetProperty("version").GetString() ?? "unknown");
        helloAuto = Wire.Contains(h,"features","automatic_input");
        SupportsMachineList = Wire.Contains(h, "methods", "machine.list");
        SupportsKill = Wire.Contains(h, "methods", "terminal.kill");
        supportsDocuments = Wire.Contains(h, "methods", "doc.get");
        supportsCheckout = supportsDocuments && Wire.Contains(h, "methods", "machine.exec");
        JsonElement? listing = null;
        if (SupportsMachineList)
        {
            // Before subscribing, so this reply cannot race any stream traffic.
            await Wire.WriteAsync(pipe, MachineListRequest(), ct).ConfigureAwait(false);
            var reply = await Wire.ReadAsync(reader, ct).ConfigureAwait(false);
            if (reply.Channel != 0 || reply.Kind != Kind.Data) throw new InvalidDataException();
            listing = Wire.Result(reply.Bytes, MachineListId, "machine.list");
        }
        machines = MachineDirectory.ReadMachines(Identity, listing);
        await Wire.WriteAsync(pipe, new(0, Kind.Data, Wire.Request(2, "events.subscribe", new { })), ct).ConfigureAwait(false);
        // Event stream (unlike attach) can beat the RPC result. Buffer at most its initial credit.
        var early = new List<Frame>(); int bytes = 0;
        Frame f;
        while ((f = await Wire.ReadAsync(reader, ct).ConfigureAwait(false)).Channel != 0)
        {
            if (f.Kind != Kind.Data || (bytes += f.Bytes.Length) > Wire.Window || early.Count == 256) throw new InvalidDataException();
            early.Add(f);
        }
        if (f.Kind != Kind.Data) throw new InvalidDataException();
        var sub = Wire.Result(f.Bytes, 2, "events.subscribe");
        events = Wire.Channel(sub); catalog.Snapshot(sub);
        foreach (var e in early)
        {
            if (e.Channel != events) throw new InvalidDataException();
            catalog.Consume(e.Bytes);
            if (e.Bytes.Length != 0) await Wire.WriteAsync(pipe, Wire.Credit(e), ct).ConfigureAwait(false);
        }
        if (supportsDocuments)
        {
            catalog.DocumentChanged += alias => {
                var machine = machines.FirstOrDefault(m => m.Available && (alias is null ? m.IsLocal : !m.IsLocal && m.Name == alias));
                if (machine != null) RequestDocument(machine);
            };
            catalog.DocumentsMissed += RefreshDocuments;
            RefreshDocuments(); // covers the snapshot and any early events; no reply needed to connect
        }
        PublishCatalog();
    }

    void RefreshDocuments()
    {
        foreach (var machine in machines)
            if (machine.Available) RequestDocument(machine);
    }

    void RequestDocument(HerdMachine machine)
    {
        if (pendingDocuments.ContainsKey(machine.Id)) { dirtyDocuments.Add(machine.Id); return; }
        lock (gate)
        {
            if (closing || finished) return;
            int id = NextMetadataId();
            pendingDocuments.Add(machine.Id, id);
            documentRequests.Add(id, machine);
            object args = machine.IsLocal ? new { name = "herdr-app" } : new { name = "herdr-app", on = machine.Id };
            Enqueue(new(0, Kind.Data, Wire.Request(id, "doc.get", args)));
        }
    }

    void AcceptDocument(int id, JsonElement reply)
    {
        if (!documentRequests.Remove(id, out var machine)) return; // retired route, possibly since reconnected
        pendingDocuments.Remove(machine.Id);
        if (dirtyDocuments.Remove(machine.Id)) RequestDocument(machine);
        if (reply.TryGetProperty("error", out _))
        {
            // Optional RPC, including on a relayed peer. Keep any previous layout. Otherwise
            // release startup to use the label-only catalog when there is no retry in flight.
            if (pendingDocuments.ContainsKey(machine.Id) || !layouts.TryAdd(machine.Id, new SpaceLayout())) return;
        }
        else
        {
            var result = Wire.Result(reply, id, "doc.get");
            layouts[machine.Id] = SpaceLayout.Read(result.ValueKind == JsonValueKind.Object && result.TryGetProperty("value", out var value) ? value : default);
        }
        PublishCatalog();
    }

    static Frame MachineListRequest() => new(0, Kind.Data, Wire.Request(MachineListId, "machine.list", new { }));

    public void RefreshMachines()
    {
        lock (gate)
        {
            if (!SupportsMachineList || refreshingMachines || closing || finished || stop.IsCancellationRequested) return;
            refreshingMachines = true;
            Enqueue(MachineListRequest());
        }
    }

    void PublishCatalog()
    {
        Volatile.Write(ref directory, catalog.Directory(machines, supportsDocuments ? layouts : null, supportsCheckout ? checkoutNames.Lookup : null));
        if (supportsCheckout)
            lock (gate) if (!closing && !finished) checkoutNames.FinishProjection(NextMetadataId, Enqueue);
        CatalogChanged?.Invoke();
    }

    void CheckConnected()
    {
        if (closing || finished || stop.IsCancellationRequested) throw new CoreException("Connection closed. Reconnect and select again.");
    }

    string TerminalRoute(string address, string expectedMachineId)
    {
        var current = Directory;
        var target = current.Terminals.FirstOrDefault(t => t.Address == address)
            ?? throw new CoreException("That program or machine is no longer available. Select a live terminal.");
        var machine = current.Machines.Single(m => target.MachineAlias is null ? m.IsLocal : !m.IsLocal && m.Name == target.MachineAlias);
        if (machine.Id != expectedMachineId) throw new CoreException("The selected machine changed. Select a terminal on the intended machine.");
        // Pin the outbound route even if a later refresh reassigns this alias.
        return machine.IsLocal ? address : machine.Id + address[address.IndexOf('/')..];
    }

    /// Requests termination, not removal. Only the event stream changes the catalog.
    public async Task KillTerminalAsync(string address, string expectedMachineId)
    {
        ArgumentNullException.ThrowIfNull(expectedMachineId);
        Task<bool> reply;
        lock (gate)
        {
            CheckConnected();
            if (!SupportsKill) throw new CoreException("This core does not support terminal.kill.");
            string route = TerminalRoute(address, expectedMachineId);
            if (pendingKill != null) throw new CoreException("A terminal close is already awaiting confirmation.");
            int id = NextActionId();
            var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendingKill = (id, ack);
            reply = ack.Task;
            Enqueue(new(0, Kind.Data, Wire.Request(id, "terminal.kill", new { terminal = route })));
        }
        try
        {
            if (!await reply.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false))
                throw new CoreException("Core refused terminal.kill. Check the program's state in HerdR.");
        }
        catch (TimeoutException)
        {
            // Keep the single slot until the late reply or disconnect. The request might
            // still take effect; don't retry it or interrupt an unrelated viewer.
            throw new CoreException("Core did not confirm terminal.kill. Reconnect and check the program's state.");
        }
        catch (OperationCanceledException)
        {
            throw new CoreException("Connection closed before terminal.kill was confirmed. Reconnect and check the program's state.");
        }
    }

    public TerminalAttachment Attach(string address, int rows, int cols, Action<char[]> consume, string expectedMachineId)
    {
        TerminalAttachment.CheckSize(rows, cols);
        ArgumentNullException.ThrowIfNull(consume);
        ArgumentNullException.ThrowIfNull(expectedMachineId);
        lock (gate)
        {
            CheckConnected();
            if (active != null) throw new CoreException("Detach the current viewer before attaching another pane.");
            string route = TerminalRoute(address, expectedMachineId);
            int id = NextActionId();
            active = new(this, id, address, expectedMachineId, consume);
            ArmDeadline(active, detach: false);
            Enqueue(new(0, Kind.Data, Wire.Request(id, "terminal.attach", new { terminal = route, rows, cols, history = 2000, observe = false })));
            return active;
        }
    }

    internal sealed class Outbox
    {
        readonly Queue<(long Order, Frame Frame)> frames = new();
        int bytes;
        internal long NextOrder => frames.TryPeek(out var head) ? head.Order : long.MaxValue;
        internal bool Fits(int size) => frames.Count < 1024 && size <= Wire.MaxFrame - bytes;
        internal void Add(long order, Frame frame) { frames.Enqueue((order, frame)); bytes += frame.Bytes.Length; }
        internal Frame Take() { var frame = frames.Dequeue().Frame; bytes -= frame.Bytes.Length; return frame; }
        internal void Clear() { frames.Clear(); bytes = 0; }
    }

    // All outbox operations run under gate. No control callback waits for pipe I/O.
    // Check lease identity and capacity before copying caller-owned input.
    internal void Send(TerminalAttachment lease, Kind kind, ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            if (active != lease || lease.Detaching || closing || finished || stop.IsCancellationRequested) return;
            if (!lease.Writes.Fits(bytes.Length)) { Overflow(); return; }
            Queue(lease.Writes, new(0, kind, bytes.ToArray()));
        }
    }

    void Enqueue(Frame f) => Queue(control, f);
    void Queue(Outbox outbox, Frame f)
    {
        if (!outbox.Fits(f.Bytes.Length)) { Overflow(); return; }
        outbox.Add(writeOrder++, f);
        writable.Writer.TryWrite(true);
    }
    void Overflow() { fault = "Output to the core backed up; reconnect rather than losing input silently."; stop.Cancel(); }

    async Task WriteLoop(CancellationToken ct)
    {
        await foreach (var _ in writable.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Frame f;
                lock (gate)
                {
                    if (active is { Channel: not 0 } a && a.Writes.NextOrder < control.NextOrder) f = a.TakeWrite();
                    else if (control.NextOrder != long.MaxValue) f = control.Take();
                    else break;
                }
                // One in-flight frame already names its channel; even a subsequent attach
                // request cannot overtake it on this pipe.
                await Wire.WriteAsync(pipe, f, ct).ConfigureAwait(false);
            }
    }

    async Task Run()
    {
        var r = ReadLoop(stop.Token); var w = WriteLoop(stop.Token);
        SessionEnd end;
        try
        {
            if (await Task.WhenAny(r, w).ConfigureAwait(false) == w) await w.ConfigureAwait(false);
            end = await r.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (gate) end = closing && fault == null ? new(EndKind.Detached, "Viewer closed; no program exit confirmed.") :
                new(EndKind.Disconnected, fault ?? (ex is CoreException ? ex.Message : "Connection lost or invalid stream. Check the core and reconnect; no program exit confirmed."));
        }
        lock (gate)
        {
            finished = true;
            pendingKill?.Ack.SetCanceled(); pendingKill = null;
            ClearDeadline(); writable.Writer.TryComplete(); stop.Cancel();
        }
        // Async pipe operations observe stop. Join first; disposing a duplex BufferedStream
        // after closing its underlying PipeStream can throw while forwarding Flush.
        try { await Task.WhenAll(r, w).ConfigureAwait(false); } catch { /* end above is authoritative */ }
        DisposePipe();
        lock (gate)
        {
            control.Clear();
            if (active != null) FinishAttachment(active, end);
            stop.Dispose();
            ended.TrySetResult(end);
        }
    }

    void DisposePipe()
    {
        try { reader.Dispose(); } // BufferedStream owns and closes the pipe (even when Flush fails).
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    async Task<SessionEnd> ReadLoop(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var f = await Wire.ReadAsync(reader, ct).ConfigureAwait(false);
            if (f.Channel == 0 && f.Kind == Kind.Data)
            {
                var reply = Wire.Parse(f.Bytes);
                int id = reply.GetProperty("id").GetInt32();
                switch (id)
                {
                    case >= 3 when (id & 1) != 0: AcceptAction(id, reply); break;
                    case MachineListId: AcceptMachines(reply); break;
                    case >= 6 when id < nextMetadataId:
                        if (checkoutNames.Receive(id, reply)) PublishCatalog();
                        else AcceptDocument(id, reply);
                        break;
                    default: throw new InvalidDataException("Unexpected RPC response.");
                }
                continue;
            }
            if (f.Channel == events && f.Kind == Kind.Data)
            {
                if (catalog.Consume(f.Bytes)) PublishCatalog();
                lock (gate) if (!closing && f.Bytes.Length != 0) Enqueue(Wire.Credit(f));
                continue;
            }

            TerminalAttachment lease;
            bool detaching;
            lock (gate)
            {
                if (active is not { Channel: not 0 } a || f.Channel != a.Channel) throw new InvalidDataException("Unexpected stream channel.");
                lease = a; detaching = a.Detaching;
                if (f.Kind == Kind.Close) { a.Detaching = true; a.Writes.Clear(); }
            }
            if (f.Kind == Kind.Close)
            {
                var end = lease.Closed(f.Bytes, detaching);
                lock (gate)
                {
                    FinishAttachment(lease, end);
                    if (closing) return new(EndKind.Detached, "Detached.");
                }
                continue;
            }
            if (f.Kind != Kind.Data) throw new InvalidDataException();
            if (detaching) continue; // terminal output can cross our CLOSE on the wire
            lease.Decode(f.Bytes, flush: false); // synchronous consumption, never under gate
            lock (gate) if (!lease.Detaching && !finished && f.Bytes.Length != 0) Queue(lease.Writes, Wire.Credit(f));
        }
    }

    void AcceptMachines(JsonElement reply)
    {
        var previous = machines;
        lock (gate)
        {
            if (!refreshingMachines) throw new InvalidDataException();
            machines = MachineDirectory.ReadMachines(Identity, Wire.Result(reply, MachineListId, "machine.list"));
            refreshingMachines = false;
        }
        if (supportsDocuments)
        {
            var connected = machines.Where(m => m.Available).ToHashSet();
            foreach (var old in previous.Where(m => m.Available && !connected.Contains(m)))
            {
                layouts.Remove(old.Id);
                dirtyDocuments.Remove(old.Id);
                if (pendingDocuments.Remove(old.Id, out int id)) documentRequests.Remove(id);
            }
            checkoutNames.Refresh();
            RefreshDocuments();
        }
        PublishCatalog();
    }

    // Null means an ordinary JSON-RPC refusal; malformed responses still fail closed.
    static JsonElement? ActionResult(JsonElement reply)
    {
        if (reply.GetProperty("jsonrpc").GetString() != "2.0") throw new InvalidDataException("Unexpected RPC response.");
        if (!reply.TryGetProperty("error", out var error)) return reply.GetProperty("result");
        if (reply.TryGetProperty("result", out _) || error.ValueKind != JsonValueKind.Object ||
            !error.GetProperty("code").TryGetInt32(out _) || error.GetProperty("message").ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Invalid RPC error.");
        return null;
    }

    void AcceptAction(int id, JsonElement reply)
    {
        lock (gate)
        {
            var response = ActionResult(reply);
            if (pendingKill is { } kill && kill.Id == id)
            {
                pendingKill = null;
                kill.Ack.SetResult(response.HasValue);
                return;
            }
            if (active is not { Channel: 0 } lease || lease.RequestId != id) throw new InvalidDataException("Unexpected action reply.");
            if (closing || stop.IsCancellationRequested) throw new OperationCanceledException();
            if (response is not { } result)
            {
                FinishAttachment(lease, new(EndKind.Detached, "Core refused the attachment. Select a live program."));
                return;
            }
            var ch = Wire.Channel(result);
            string address = lease.Address;
            int slash = address.IndexOf('/');
            if (ch == events || result.GetProperty("terminal").GetProperty("address").GetString() != address ||
                !machines.Any(m => m.Id == lease.MachineId && m.Available && (slash < 0 ? m.IsLocal : !m.IsLocal && m.Name == address[..slash])))
                throw new CoreException("Unexpected attach channel or terminal identity. Reconnect and reselect.");
            lease.Accept(ch, helloAuto && Wire.Flag(result, "automatic_input"));
            if (lease.Detaching) Queue(lease.Writes, new(ch, Kind.Close, [])); // still on its original detach deadline
            else { ClearDeadline(); writable.Writer.TryWrite(true); }
        }
    }

    // Only the reader (or Run after joining it) can release the slot and its renderer.
    void FinishAttachment(TerminalAttachment lease, SessionEnd end)
    {
        ClearDeadline(); active = null; lease.Finish(end);
    }

    void ClearDeadline() { deadline?.Dispose(); deadline = null; }
    void ArmDeadline(TerminalAttachment lease, bool detach)
    {
        ClearDeadline();
        deadline = new Timer(_ => {
            lock (gate)
            {
                // Dispose does not retract a timer callback already waiting for gate.
                if (active != lease || finished || lease.Detaching != detach || !detach && lease.Channel != 0 || stop.IsCancellationRequested) return;
                if (!closing) fault = detach ? "Detach was not confirmed. Reconnect; no program exit confirmed." : "Core did not answer terminal.attach. Reconnect and reselect.";
                stop.Cancel(); // uncertain ownership: never recycle this pipe
            }
        }, null, TimeSpan.FromSeconds(detach ? 2 : 10), Timeout.InfiniteTimeSpan);
    }

    void BeginDetach(TerminalAttachment lease)
    {
        if (lease.Detaching) return;
        lease.Detaching = true;
        ArmDeadline(lease, detach: true);
        if (lease.Channel == 0) lease.Writes.Clear(); // do not send any pre-grant input
        else Queue(lease.Writes, new(lease.Channel, Kind.Close, []));
    }

    internal ValueTask Detach(TerminalAttachment lease)
    {
        lock (gate) if (active == lease && !finished && !stop.IsCancellationRequested) BeginDetach(lease);
        return new(lease.Completion);
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (!closing && !finished)
            {
                closing = true;
                if (active is { Channel: not 0 } lease) BeginDetach(lease);
                else stop.Cancel(); // window shutdown need not wait for an outstanding attach RPC
            }
        }
        return new(Completion);
    }
}
