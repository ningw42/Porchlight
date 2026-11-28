namespace Porchlight.Core;

/// Adds one-shot local DEC mode 12 requests to a single Windows Terminal output stream.
/// Start after the renderer is installed, before giving Render to the attachment reader.
/// A plain-text prompt may defer a request: the program can still repeat its last graphic (REP).
/// An explicit program blink mode or cursor style takes precedence at dispatch.
public sealed class CursorBlinkStream(Action<char[]> render, bool? initialCursorBlink)
{
    readonly Boundary boundary = new();
    // Only the reader and the idle worker take the writer. A UI/native callback can
    // request a change or begin close without synchronously waiting on native output.
    readonly SemaphoreSlim writer = new(1, 1);
    readonly object requests = new();
    bool started, closed;
    bool? pending = initialCursorBlink;
    Task? queuedIdle;

    public Task StartAsync()
    {
        lock (requests)
        {
            if (closed) return Task.CompletedTask;
            started = true;
            return ScheduleIdle();
        }
    }

    // Completion means the idle attempt finished; a partial VT or repeatable graphic may defer delivery.
    public Task SetCursorBlinkAsync(bool enabled)
    {
        lock (requests)
        {
            if (closed) return Task.CompletedTask;
            pending = enabled;
            return ScheduleIdle();
        }
    }

    Task ScheduleIdle() // under requests; at most one waiting worker, no polling
    {
        if (!started || !pending.HasValue) return Task.CompletedTask;
        return queuedIdle ??= Task.Run(async () =>
        {
            await writer.WaitAsync().ConfigureAwait(false);
            try
            {
                bool? mode;
                lock (requests)
                {
                    queuedIdle = null;
                    mode = boundary.Safe ? TakePending() : null;
                }
                if (mode.HasValue) render(Mode(mode.Value).ToCharArray());
            }
            finally { writer.Release(); }
        });
    }

    bool? TakePending() { var value = pending; pending = null; return value; }
    public static string Mode(bool blink) => blink ? "\x1b[?12h" : "\x1b[?12l";

    public void Render(char[] chars)
    {
        writer.Wait(); // the pipe reader's callback, and its render credit, stay synchronous
        try
        {
            int cut = boundary.Advance(chars);
            bool? mode;
            lock (requests)
            {
                if (closed) return;
                mode = started && cut != Boundary.Deferred ? TakePending() : null;
            }
            if (cut >= 0 && mode.HasValue)
            {
                // One native write per remote callback, including when a safe boundary
                // occurs *inside* the buffer. Do not separate or reschedule its replies.
                var insert = Mode(mode.Value);
                var combined = new char[chars.Length + insert.Length];
                chars.AsSpan(0, cut).CopyTo(combined);
                insert.CopyTo(combined.AsSpan(cut));
                chars.AsSpan(cut).CopyTo(combined.AsSpan(cut + insert.Length));
                chars = combined;
            }
            render(chars);
        }
        finally { writer.Release(); }
    }

    public async Task CloseAsync()
    {
        lock (requests) { closed = true; pending = null; }
        // A callback already committed to render may still be using the control.
        await writer.WaitAsync().ConfigureAwait(false);
        writer.Release();
    }

    // Boundary recognizer, not a VT emulator. Mirrors the output (not keyboard)
    // parser in winui3-terminal 7a83c0f, src/terminal/parser/stateMachine.cpp.
    // Track framing, REP safety, and explicit program cursor choices, not screen state.
    sealed class Boundary
    {
        enum State { Ground, Escape, Csi, Osc, String, Opaque }
        State state;
        char intermediate; // sole ESC intermediate, or '\0' / multiple
        bool? acceptC1 = false;
        readonly CsiAccumulator csi = new();
        bool opaqueEscape, repeatable;
        public const int Deferred = -1;
        const int Superseded = -2;

        public bool Safe => state == State.Ground && !repeatable;
        static bool Graphic(char c) => c >= ' ' && c is not (>= '\x7f' and <= '\u009f');

        public int Advance(ReadOnlySpan<char> chars)
        {
            int first = Safe ? 0 : Deferred;
            int commandStart = Superseded; // can't insert before a partial command from an earlier frame
            for (int i = 0; i < chars.Length; ++i)
            {
                char c = chars[i];
                // A CSI clears Windows Terminal's REP cache. Immediately before a
                // graphic is safe too: that character replaces the cache before
                // the application can read it. Never cut inside a surrogate pair.
                if (state == State.Ground && Graphic(c))
                {
                    if (first == Deferred && !char.IsLowSurrogate(c)) first = i;
                    repeatable = true;
                }
                else
                {
                    if (c == '\x1b' || (acceptC1 == true && c == '\u009b'))
                        commandStart = state == State.Ground ? i : Superseded;
                    // A complete explicit cursor setter dispatches (and clears REP)
                    // without using the graphic. Insert before it if its introducer
                    // is still ours to write; otherwise retire the older request.
                    if (Step(c) && first == Deferred) first = commandStart;
                }
                if (first == Deferred && Safe) first = i + 1;
            }
            return first;
        }

        // True only when a program command actually dispatches an explicit blink choice.
        bool Step(char c)
        {
            if (state == State.Opaque)
            {
                // CSI would print literal text in VT52. A native DEC macro can
                // also run arbitrary VT we never see. Only a literal ESC < proves
                // we're back in ANSI ground from either mode or a partial string.
                if (opaqueEscape && c == '<') { state = State.Ground; acceptC1 = null; repeatable = true; }
                opaqueEscape = c == '\x1b';
                return false;
            }
            if (c is '\x18' or '\x1a') { state = State.Ground; repeatable = false; return false; } // CAN, SUB execute anywhere
            if (c is >= '\u0080' and <= '\u009f')
            {
                // Disabled C1 is ignored even inside a partial OSC (U+009C is
                // NOT its ST). When enabled, Windows reprocesses it as ESC + final.
                if (acceptC1 == null) { state = State.Opaque; return false; }
                if (acceptC1 == false) return false;
                BeginEscape();
                c = (char)(c - 0x40);
            }
            if (c == '\x1b') { BeginEscape(); return false; }
            if (state == State.Osc && c == '\a') state = State.Ground;
            if (c < ' ' || c == '\x7f')
            {
                // Ground executes C0/DEL; BEL above dispatched OSC. Controls in
                // other states may be ignored, so don't assume they clear REP.
                if (state == State.Ground) repeatable = false;
                return false;
            }

            switch (state)
            {
                case State.Escape:
                    if (c is >= ' ' and <= '/') { intermediate = intermediate == '\0' ? c : '\uffff'; break; }
                    state = State.Ground;
                    if (c > '~') { state = State.Opaque; break; } // non-ASCII native dispatch ID
                    if (intermediate == '\0')
                    {
                        // ESC O is a single shift on output, not the input parser's
                        // SS3 parameter state. A CSI contains no printable character.
                        state = c switch { '[' => State.Csi, ']' => State.Osc,
                            'P' or 'X' or '^' or '_' => State.String, _ => State.Ground };
                        if (c == 'c') // RIS also restores blinking on the active page.
                        {
                            acceptC1 = false;
                            repeatable = false;
                            return true;
                        }
                        if (state == State.Csi) csi.Start();
                    }
                    else if ((intermediate, c) is (' ', '7') or ('%', '@')) acceptC1 = true;
                    else if (intermediate == '%' && c == 'G') acceptC1 = false;
                    if (state == State.Ground) repeatable = false; // ESC dispatch (including ST)
                    break;
                case State.Csi:
                    if (c >= '@')
                    {
                        state = c > '~' || csi.EntersOpaque(c) ? State.Opaque : State.Ground;
                        if (state == State.Ground && csi.PlainDispatch) repeatable = false;
                        return csi.SetsBlink(c);
                    }
                    csi.Collect(c);
                    break;
                // OSC and DCS/SOS/PM/APC payloads never finish on printable data.
                // ESC starts the ST (or interrupts and starts another sequence).
            }
            return false;
        }

        void BeginEscape() { state = State.Escape; intermediate = '\0'; }
    }

    sealed class CsiAccumulator
    {
        enum Syntax { Start, Params, Intermediates, Uncertain }
        Syntax syntax;
        char modifier; // sole private prefix or intermediate; multiple never match our commands
        readonly int[] parameters = new int[32]; // native MAX_PARAMETER_COUNT; overflow parameters are ignored
        int index;

        public void Start() { syntax = Syntax.Start; modifier = '\0'; index = 0; parameters[0] = 0; }

        public void Collect(char c)
        {
            if (syntax == Syntax.Uncertain) return;
            if (c is >= ' ' and <= '/' || (syntax == Syntax.Start && c is >= '<' and <= '?'))
            {
                modifier = modifier == '\0' ? c : '\uffff';
                syntax = c <= '/' ? Syntax.Intermediates : Syntax.Params;
            }
            else if (syntax != Syntax.Intermediates && (char.IsAsciiDigit(c) || c == ';'))
            {
                syntax = Syntax.Params;
                if (index == parameters.Length) return;
                if (c != ';') parameters[index] = Math.Min(13, parameters[index] * 10 + c - '0'); // only 0..12 matter
                else if (++index < parameters.Length) parameters[index] = 0;
            }
            else syntax = Syntax.Uncertain; // subparameters may be rejected; bad ordering may be ignored
        }

        public bool PlainDispatch => syntax != Syntax.Uncertain;
        bool HasMode(int mode) => parameters.AsSpan(0, Math.Min(index + 1, parameters.Length)).Contains(mode);
        public bool EntersOpaque(char final) => PlainDispatch && (modifier, final) switch {
            ('?', 'l') => HasMode(2), // DECRST 2: VT52
            ('*', 'z') => true, // DECINVM: arbitrary macro
            _ => false
        };
        public bool SetsBlink(char final) => PlainDispatch && (modifier, final) switch {
            ('?', 'h' or 'l') => HasMode(12),
            (' ', 'q') => parameters[0] <= 6, // DECSCUSR uses the first parameter; omitted = user default
            _ => false
        };
    }
}
