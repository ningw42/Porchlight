using Microsoft.Terminal.TerminalConnection;
using Microsoft.UI.Dispatching;
using Windows.Foundation;
using Windows.Foundation.Collections;

namespace Porchlight.App;

// A resize can originate inside the terminal's layout/resize lock. Repaint on the
// UI queue after the grid has settled, never re-enter the renderer from Resize.
internal sealed partial class PreviewConnection(DispatcherQueue dispatcher, Action<PreviewConnection> fit) : ITerminalConnection
{
    // Give the end cursor a blank cell even when the last wide glyph fills the right margin.
    const string Sample = "C:\\work> \x1b[33mgit\x1b[m diff\r\n└─ != => ffi 0123456789 日本語 ";
    int started, pending;
    public Guid SessionId { get; } = Guid.NewGuid();
    public ConnectionState State => ConnectionState.Connected; // No transport state transitions.
    public event TypedEventHandler<ITerminalConnection, object> StateChanged { add { } remove { } }
    public event TerminalOutputHandler? TerminalOutput;

    public void Start() { Volatile.Write(ref started, 1); Refresh(); }
    public void Resize(uint rows, uint columns) => Refresh();
    internal void Refresh()
    {
        if (Volatile.Read(ref started) == 0 || Interlocked.Exchange(ref pending, 1) != 0) return;
        dispatcher.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref pending, 0);
            if (Volatile.Read(ref started) == 0) return;
            // Clear the previous viewport AND history; a narrower reflow may have scrolled the prompt away.
            TerminalOutput?.Invoke(("\x1b[H\x1b[2J\x1b[3J\x1b[H" + Sample).ToCharArray());
            fit(this);
        });
    }

    public void Initialize(ValueSet settings) { }
    public void WriteInput(char[] data) { }
    public void Close() => Volatile.Write(ref started, 0);
}
