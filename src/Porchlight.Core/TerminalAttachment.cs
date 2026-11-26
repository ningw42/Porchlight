using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Porchlight.Core;

/// One viewer lease. Keeping an old lease (for example, in a late UI callback) never
/// grants access to the session's next terminal, even if the core reuses a channel.
public sealed class TerminalAttachment : IAsyncDisposable
{
    readonly CoreSession session;
    readonly TaskCompletionSource<bool> accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<SessionEnd> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Decoder decoder = Encoding.UTF8.GetDecoder();
    Action<char[]>? consume;

    internal TerminalAttachment(CoreSession session, int requestId, string address, string machineId, Action<char[]> consume)
    {
        this.session = session; RequestId = requestId; Address = address; MachineId = machineId; this.consume = consume;
    }

    // Mutated under the session gate. Only the pipe reader calls Decode; Finish runs
    // on that reader, or after it has been joined on connection loss.
    internal int RequestId { get; }
    internal string Address { get; }
    internal string MachineId { get; }
    internal uint Channel { get; private set; }
    internal bool Detaching { get; set; }
    bool closeIssued;
    internal CoreSession.Outbox Writes { get; } = new();
    bool automaticInput;

    /// True only for a validated attach grant, even if DisposeAsync is already waiting to detach it.
    public Task<bool> Accepted => accepted.Task;
    /// No render callback is running or will run for this lease after Completion.
    public Task<SessionEnd> Completion => ended.Task;

    public void Input(ReadOnlySpan<byte> bytes, bool synchronousReply = false)
    {
        if (!bytes.IsEmpty) session.Send(this, synchronousReply ? Kind.AutomaticInput : Kind.Data, bytes);
    }

    internal static void CheckSize(int rows, int cols)
    {
        if (rows is < 1 or > ushort.MaxValue || cols is < 1 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(rows));
    }

    public void Resize(int rows, int cols)
    {
        CheckSize(rows, cols);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)cols);
        BinaryPrimitives.WriteUInt16LittleEndian(size[2..], (ushort)rows);
        session.Send(this, Kind.Resize, size);
    }

    public void Focus(bool focused) => session.Send(this, Kind.Focus, [focused ? (byte)1 : (byte)0]);
    public ValueTask DisposeAsync() => session.Detach(this);

    internal void Accept(uint channel, bool automaticInput)
    {
        Channel = channel; this.automaticInput = automaticInput;
        accepted.SetResult(true);
    }

    internal Frame TakeWrite()
    {
        var f = Writes.Take();
        if (f.Kind == Kind.Close) closeIssued = true;
        return new(Channel, f.Kind == Kind.AutomaticInput && !automaticInput ? Kind.Data : f.Kind, f.Bytes);
    }

    internal void Decode(byte[] bytes, bool flush)
    {
        var chars = new char[decoder.GetCharCount(bytes, flush)];
        decoder.GetChars(bytes, chars, flush);
        if (chars.Length != 0) consume!(chars);
    }

    internal SessionEnd Closed(byte[] bytes, bool wasDetaching)
    {
        // The reader has sealed Writes under the same gate used by TakeWrite. A
        // merely queued CLOSE (e.g. behind blocked input) cannot have been acknowledged.
        if (bytes.Length == 0) return closeIssued ? new(EndKind.Detached, "Detached.") : throw new InvalidDataException("Unsolicited close.");
        var msg = Wire.Parse(bytes);
        var reason = msg.GetProperty("reason").GetString();
        if (reason is "holder_lost" or "host_shutdown" or "server_stop" or "stopped_by_restart" or "signaled")
            return new(EndKind.Disconnected, "Terminal stream lost; no program exit confirmed.");
        if (reason is not ("exited" or "killed")) throw new InvalidDataException("Invalid close reason.");
        if (!wasDetaching) Decode([], flush: true);
        string code = msg.TryGetProperty("exit_code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var n) ? $" (code {n})" : "";
        return new(EndKind.Exited, "Program " + reason + code + ".");
    }

    internal void Finish(SessionEnd end)
    {
        Writes.Clear(); consume = null;
        accepted.TrySetResult(false);
        ended.SetResult(end);
    }
}
