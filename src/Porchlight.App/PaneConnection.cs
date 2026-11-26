using System.Text;
using Microsoft.Terminal.TerminalConnection;
using Porchlight.Core;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace Porchlight.App;

// One native control owns one attachment, never the gateway or the program.
internal sealed partial class PaneConnection(CoreSession session, string machineId, string address) : ITerminalConnection
{
    [ThreadStatic] static PaneConnection? dispatching;
    readonly object gate = new();
    readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<SessionEnd> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TerminalAttachment? attachment;
    bool releasing;
    int rows = 30, cols = 80;
    internal Task<bool> Ready => ready.Task;
    internal Task<SessionEnd> Completion => ended.Task;
    public Guid SessionId { get; } = Guid.NewGuid();
    public ConnectionState State { get; private set; } = ConnectionState.NotConnected;
    public event TypedEventHandler<ITerminalConnection, object>? StateChanged;
    public event TerminalOutputHandler? TerminalOutput;
    public void Initialize(ValueSet settings) { }

    public async void Start()
    {
        TerminalAttachment? stream = null;
        try
        {
            lock (gate)
            {
                if (releasing || State != ConnectionState.NotConnected) return;
                State = ConnectionState.Connecting;
                attachment = stream = session.Attach(address, rows, cols, Render, machineId);
            }
            bool ok = await stream.Accepted;
            lock (gate)
            {
                ok &= !releasing;
                if (ok) State = ConnectionState.Connected;
            }
            if (ok) StateChanged?.Invoke(this, null!);
            ready.TrySetResult(ok);
            Finish(await stream.Completion);
        }
        catch (Exception ex)
        {
            if (stream != null) await stream.DisposeAsync();
            Finish(new(EndKind.Disconnected, ex is CoreException ? ex.Message : "Could not open the terminal viewer."));
        }
    }

    void Render(char[] chars)
    {
        lock (gate) if (releasing) return;
        var previous = dispatching; dispatching = this;
        try { (TerminalOutput ?? throw new InvalidOperationException("Renderer unavailable."))(chars); }
        finally { dispatching = previous; }
    }
    public void WriteInput(char[] data)
    {
        lock (gate) if (!releasing) attachment?.Input(Encoding.UTF8.GetBytes(data), ReferenceEquals(dispatching, this));
    }
    public void Resize(uint r, uint c)
    {
        if (r is 0 or > ushort.MaxValue || c is 0 or > ushort.MaxValue) return;
        lock (gate) { rows = (int)r; cols = (int)c; if (!releasing) attachment?.Resize(rows, cols); }
    }
    internal void Focus(bool focused) { lock (gate) if (!releasing) attachment?.Focus(focused); }

    internal Task CloseAsync()
    {
        TerminalAttachment? stream;
        lock (gate)
        {
            if (releasing) return ended.Task;
            releasing = true; stream = attachment;
        }
        if (stream == null) Finish(new(EndKind.Detached, "Viewer closed."));
        else _ = stream.DisposeAsync(); // Start observes Completion after the reader releases Render.
        return ended.Task;
    }
    public void Close() => _ = CloseAsync();
    void Finish(SessionEnd result)
    {
        lock (gate)
        {
            if (State == ConnectionState.Closed) return;
            releasing = true; State = ConnectionState.Closed;
        }
        ready.TrySetResult(false);
        try { StateChanged?.Invoke(this, null!); }
        finally { ended.TrySetResult(result); }
    }
}
