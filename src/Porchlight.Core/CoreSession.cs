using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Porchlight.Core;

public sealed record CoreIdentity(string Name, string Id, string Version);
public enum EndKind { Detached, Exited, Disconnected }
public sealed record SessionEnd(EndKind Kind, string Message);

/// One existing pipe, one catalog and at most one explicitly requested attach. Reconnect
/// with a new instance: channel numbers, event sequence and UTF-8 state are connection-local.
public sealed class CoreSession : IAsyncDisposable
{
    readonly Stream pipe, reader;
    readonly CancellationTokenSource stop = new();
    readonly TerminalCatalog catalog = new();
    readonly object gate = new();
    readonly Channel<Frame> writes = Channel.CreateBounded<Frame>(new BoundedChannelOptions(1024) { SingleReader = true });
    readonly Queue<Frame> beforeReply = new();
    readonly TaskCompletionSource<SessionEnd> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource<bool>? accepted;
    Action<char[]>? render;
    readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    uint events, attachment;
    string? selected, fault;
    bool helloAuto, sendAuto, closing, finished;
    int queuedBytes;
    const int MachineListId = 4;
    IReadOnlyList<HerdMachine> machines = [];
    MachineDirectory directory = new([], []);
    bool refreshingMachines;
    public bool SupportsMachineList { get; private set; }
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
                TimeoutException or OperationCanceledException => new CoreException("Core did not answer. Check that HerdR is running at the selected pipe, then reconnect."),
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
        PublishCatalog();
    }

    static Frame MachineListRequest() => new(0, Kind.Data, Wire.Request(MachineListId, "machine.list", new { }));

    public void RefreshMachines()
    {
        lock (gate)
        {
            if (!SupportsMachineList || refreshingMachines || accepted != null || closing || finished) return;
            refreshingMachines = true;
            Enqueue(MachineListRequest());
        }
    }

    void PublishCatalog()
    {
        Volatile.Write(ref directory, catalog.Directory(machines));
        CatalogChanged?.Invoke();
    }

    public Task<bool> AttachAsync(string address, int rows, int cols, Action<char[]> consume, string? expectedMachineId = null)
    {
        CheckSize(rows, cols);
        ArgumentNullException.ThrowIfNull(consume);
        lock (gate)
        {
            if (closing || finished || accepted != null) throw new CoreException("Reconnect before attaching another pane.");
            var current = Directory;
            var target = current.Terminals.FirstOrDefault(t => t.Address == address)
                ?? throw new CoreException("That program or machine is no longer available. Refresh and select again.");
            var machine = current.Machines.Single(m => target.MachineAlias is null ? m.IsLocal : !m.IsLocal && m.Name == target.MachineAlias);
            if (expectedMachineId != null && machine.Id != expectedMachineId)
                throw new CoreException("The selected machine changed. Refresh and select again.");
            // Route by permanent core ID, not a potentially reassigned alias. The gateway
            // localizes the attach reply back to alias/tN; require that exact displayed address.
            string route = machine.IsLocal ? address : machine.Id + address[address.IndexOf('/')..];
            selected = address; render = consume;
            accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            stop.CancelAfter(TimeSpan.FromSeconds(10));
            Enqueue(new(0, Kind.Data, Wire.Request(3, "terminal.attach", new { terminal = route, rows, cols, history = 2000, observe = false })));
            return accepted.Task;
        }
    }

    // Call-site origin only. The transport never classifies the bytes. Take ownership of
    // a copy now; none of these synchronous control callbacks wait on pipe I/O.
    public void Input(ReadOnlySpan<byte> bytes, bool synchronousReply = false)
    {
        if (bytes.IsEmpty) return;
        lock (gate)
        {
            if (!CanSend) return;
            if (bytes.Length > Wire.MaxFrame - queuedBytes) { Overflow(); return; }
            SendStream(synchronousReply ? Kind.AutomaticInput : Kind.Data, bytes.ToArray());
        }
    }
    static void CheckSize(int rows, int cols)
    {
        if (rows is < 1 or > ushort.MaxValue || cols is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(rows));
    }
    public void Resize(int rows, int cols)
    {
        CheckSize(rows, cols);
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)cols);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)rows);
        lock (gate) if (CanSend) SendStream(Kind.Resize, bytes);
    }
    public void Focus(bool focused) { lock (gate) if (CanSend) SendStream(Kind.Focus, [focused ? (byte)1 : (byte)0]); }
    bool CanSend => accepted != null && !closing && !finished && !stop.IsCancellationRequested;
    void SendStream(Kind kind, byte[] bytes) // under gate
    {
        if (attachment != 0) Enqueue(new(attachment, WireKind(kind), bytes));
        else if (beforeReply.Count >= 1024 || bytes.Length > Wire.MaxFrame - queuedBytes) Overflow();
        else { beforeReply.Enqueue(new(0, kind, bytes)); queuedBytes += bytes.Length; }
    }
    Kind WireKind(Kind k) => k == Kind.AutomaticInput && !sendAuto ? Kind.Data : k;
    void Enqueue(Frame f) // under gate
    {
        if (f.Bytes.Length > Wire.MaxFrame - queuedBytes || !writes.Writer.TryWrite(f)) Overflow();
        else queuedBytes += f.Bytes.Length;
    }
    void Overflow() { fault = "Output to the core backed up; reconnect rather than losing input silently."; stop.Cancel(); }

    async Task WriteLoop(CancellationToken ct)
    {
        await foreach (var f in writes.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await Wire.WriteAsync(pipe, f, ct).ConfigureAwait(false);
            lock (gate) queuedBytes -= f.Bytes.Length;
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
        lock (gate) { finished = true; writes.Writer.TryComplete(); stop.Cancel(); }
        // Async pipe operations observe stop. Join first; disposing a duplex BufferedStream
        // after closing its underlying PipeStream can throw while forwarding Flush.
        try { await Task.WhenAll(r, w).ConfigureAwait(false); } catch { /* end above is authoritative */ }
        DisposePipe(); stop.Dispose();
        lock (gate) { render = null; beforeReply.Clear(); accepted?.TrySetResult(false); }
        ended.TrySetResult(end);
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
            var f = await Wire.ReadAsync(reader, ct).ConfigureAwait(false);
            if (f.Channel == 0 && f.Kind == Kind.Data)
            {
                var reply = Wire.Parse(f.Bytes);
                if (reply.GetProperty("id").GetInt32() == MachineListId) AcceptMachines(reply);
                else AcceptAttach(reply);
                continue;
            }
            if (f.Channel == events && f.Kind == Kind.Data)
            {
                if (catalog.Consume(f.Bytes)) PublishCatalog();
            }
            else if (attachment != 0 && f.Channel == attachment)
            {
                if (f.Kind == Kind.Close) return Closed(f.Bytes);
                if (f.Kind != Kind.Data) throw new InvalidDataException();
                Decode(f.Bytes, flush: false); // synchronous consumption by renderer, NOT delivery to a UI queue
            }
            else throw new InvalidDataException();
            lock (gate) if (!closing && f.Bytes.Length != 0) Enqueue(Wire.Credit(f));
        }
    }

    void AcceptMachines(JsonElement reply)
    {
        lock (gate)
        {
            if (!refreshingMachines) throw new InvalidDataException();
            machines = MachineDirectory.ReadMachines(Identity, Wire.Result(reply, MachineListId, "machine.list"));
            refreshingMachines = false;
        }
        PublishCatalog();
    }

    void AcceptAttach(JsonElement reply)
    {
        lock (gate)
        {
            if (accepted == null || attachment != 0) throw new InvalidDataException();
            var a = Wire.Result(reply, 3, "terminal.attach");
            var ch = Wire.Channel(a);
            if (ch == events || a.GetProperty("terminal").GetProperty("address").GetString() != selected)
                throw new CoreException("Unexpected attach channel or terminal identity. Reconnect and reselect.");
            if (closing) throw new OperationCanceledException();
            attachment = ch; sendAuto = helloAuto && Wire.Flag(a,"automatic_input");
            stop.CancelAfter(Timeout.Infinite);
            while (beforeReply.TryDequeue(out var pending))
            {
                queuedBytes -= pending.Bytes.Length;
                Enqueue(new(ch, WireKind(pending.Kind), pending.Bytes));
            }
            accepted.TrySetResult(true);
        }
    }

    void Decode(byte[] bytes, bool flush)
    {
        var chars = new char[decoder.GetCharCount(bytes, flush)];
        decoder.GetChars(bytes, chars, flush);
        if (chars.Length > 0) render!(chars);
    }
    SessionEnd Closed(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            lock (gate) if (closing) return new(EndKind.Detached, "Detached.");
            throw new InvalidDataException("Unsolicited close.");
        }
        var msg = Wire.Parse(bytes);
        if (msg.GetProperty("reason").GetString() is not ("exited" or "killed")) throw new InvalidDataException();
        Decode([], flush: true);
        string code = msg.TryGetProperty("exit_code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? $" (code {n})" : "";
        return new(EndKind.Exited, "Program " + msg.GetProperty("reason").GetString() + code + ".");
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (!closing && !finished)
            {
                closing = true;
                if (attachment == 0) stop.Cancel(); // even if attach RPC is in flight, pipe loss releases only the viewer
                else { Enqueue(new(attachment, Kind.Close, [])); stop.CancelAfter(TimeSpan.FromSeconds(2)); }
            }
        }
        return new(Completion);
    }
}
