using System.Text;
using Microsoft.Terminal.TerminalConnection;
using Porchlight.Core;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace Porchlight.App;

// Native lifecycle: TermControl supplies its first grid and subscribes TerminalOutput
// before Start. There is no ConPTY here. A closed view never owns the program.
internal sealed partial class PaneConnection(CoreSession session, string machineId, string address, Action<bool> ready) : ITerminalConnection
{
    [ThreadStatic] static PaneConnection? dispatching;
    readonly object gate = new();
    int rows = 30, cols = 80;
    public Guid SessionId { get; } = Guid.NewGuid();
    public ConnectionState State { get; private set; } = ConnectionState.NotConnected;
    public event TypedEventHandler<ITerminalConnection, object>? StateChanged;
    public event TerminalOutputHandler? TerminalOutput;
    public void Initialize(ValueSet settings) { }

    public async void Start()
    {
        try
        {
            Task<bool> reply;
            lock (gate)
            {
                if (State != ConnectionState.NotConnected) return;
                State = ConnectionState.Connecting;
                reply = session.AttachAsync(address, rows, cols, Render, machineId);
            }
            if (!await reply) return;
            lock (gate)
            {
                if (State == ConnectionState.Closed) return;
                State = ConnectionState.Connected;
            }
            StateChanged?.Invoke(this, null!);
            ready(true);
        }
        catch { ready(false); await session.DisposeAsync(); }
    }

    void Render(char[] chars)
    {
        // Pin the scope to THIS instance and stack (does not flow to tasks/UI keys).
        // A deferred reply has no origin tag and cannot be distinguished from typing.
        var previous = dispatching; dispatching = this;
        try { (TerminalOutput ?? throw new InvalidOperationException("Renderer unavailable."))(chars); }
        finally { dispatching = previous; }
    }
    public void WriteInput(char[] data) => session.Input(Encoding.UTF8.GetBytes(data), ReferenceEquals(dispatching, this));
    public void Resize(uint r, uint c)
    {
        if (r is 0 or > ushort.MaxValue || c is 0 or > ushort.MaxValue) return;
        lock (gate) { rows = (int)r; cols = (int)c; session.Resize(rows, cols); }
    }
    public void Close()
    {
        lock (gate) { if (State == ConnectionState.Closed) return; State = ConnectionState.Closed; }
        _ = session.DisposeAsync();
        StateChanged?.Invoke(this, null!);
    }
}
