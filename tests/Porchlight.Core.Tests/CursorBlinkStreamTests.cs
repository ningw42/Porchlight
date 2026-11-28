using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

[TestClass]
public class CursorBlinkStreamTests
{
    const string On = "\x1b[?12h", Off = "\x1b[?12l";
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    static async Task<(CursorBlinkStream stream, List<string> calls)> StartRecording()
    {
        var calls = new List<string>();
        var stream = new CursorBlinkStream(c => calls.Add(new(c)), null);
        await stream.StartAsync();
        return (stream, calls);
    }

    [TestMethod]
    public async Task InitialModeIsOptionalAndPreStartRequestsCoalesce()
    {
        var calls = new List<string>();
        var untouched = new CursorBlinkStream(c => calls.Add(new(c)), null);
        await untouched.StartAsync();
        untouched.Render("prompt".ToCharArray());
        await untouched.StartAsync();
        CollectionAssert.AreEqual(new[] { "prompt" }, calls);

        calls.Clear();
        var pending = new CursorBlinkStream(c => calls.Add(new(c)), true);
        await pending.SetCursorBlinkAsync(false);
        Assert.IsEmpty(calls);
        await pending.StartAsync(); await pending.StartAsync();
        CollectionAssert.AreEqual(new[] { Off }, calls);

        calls.Clear();
        var closed = new CursorBlinkStream(c => calls.Add(new(c)), true);
        await closed.CloseAsync(); await closed.StartAsync();
        await closed.SetCursorBlinkAsync(false); closed.Render("too late".ToCharArray());
        Assert.IsEmpty(calls);
    }

    [TestMethod]
    public async Task IdleEditNeedsNoMoreDataAndDoesNotReassertOverApplications()
    {
        var calls = new List<char[]>();
        var stream = new CursorBlinkStream(calls.Add, false);
        await stream.StartAsync();
        var prompt = "$ \x1b[?25l\x1b[1 q".ToCharArray();
        stream.Render(prompt);
        await stream.SetCursorBlinkAsync(true);
        var app = (Off + "\x1b[2 q\x1b[?25h\x1b[?12$p").ToCharArray();
        stream.Render(app);
        CollectionAssert.AreEqual(new[] { Off, new string(prompt), On, new string(app) }, calls.Select(c => new string(c)).ToArray());
        Assert.AreSame(prompt, calls[1]); Assert.AreSame(app, calls[3]);
    }

    [TestMethod]
    public async Task SplitProgramBlinkOnWinsOverAnOlderDeferredOff()
    {
        var (stream, calls) = await StartRecording();
        stream.Render("prompt".ToCharArray());
        await stream.SetCursorBlinkAsync(false);
        stream.Render("\x1b[?1".ToCharArray());
        const string suffixAndQuery = "2h\x1b[?12$p";
        stream.Render(suffixAndQuery.ToCharArray());
        CollectionAssert.AreEqual(new[] { "prompt", "\x1b[?1", suffixAndQuery }, calls);
    }

    [TestMethod]
    [DataRow("\x1b[?25;0012;1h")]
    [DataRow("\x1b[?1;12l")]
    [DataRow("\x1b[ q")]
    [DataRow("\x1b[3;99 q")]
    [DataRow("\x1b[6 q")]
    [DataRow("\u001bc")] // RIS
    public async Task EarlierEditCanPrecedeACompleteProgramCursorCommand(string command)
    {
        var (stream, calls) = await StartRecording();
        stream.Render("prompt".ToCharArray());
        await stream.SetCursorBlinkAsync(false);
        stream.Render((command + "\x1b[?12$p").ToCharArray());
        CollectionAssert.AreEqual(new[] { "prompt", Off + command + "\x1b[?12$p" }, calls);
    }

    [TestMethod]
    [DataRow("", "\x1b[?25;0012;1h")]
    [DataRow("", "\x1b[?1;12l")]
    [DataRow("", "\x1b[5 q")]
    [DataRow("", "\u001bc")]
    [DataRow("\x1b 7", "\u009b?12h")]
    public async Task ProgramCursorDispatchSupersedesEditsInsideItsSequenceButNotAfterIt(string setup, string command)
    {
        for (int split = 1; split < command.Length; split++)
        {
            var (stream, calls) = await StartRecording();
            string prefix = setup + "X" + command[..split], suffix = command[split..] + "\x1b[?12$pY";
            stream.Render(prefix.ToCharArray());
            await stream.SetCursorBlinkAsync(true); await stream.SetCursorBlinkAsync(false);
            stream.Render(suffix.ToCharArray());
            // A new request after the dispatch still takes effect, but cannot steal Y from REP.
            await stream.SetCursorBlinkAsync(true);
            stream.Render("\x1b[3b".ToCharArray());
            CollectionAssert.AreEqual(new[] { prefix, suffix, "\x1b[3b" + On }, calls, $"split={split}");
        }
    }

    [TestMethod]
    public async Task CursorCommandInterruptingAnOscCannotAcceptAnEditBeforeItsEscape()
    {
        var (stream, calls) = await StartRecording();
        const string prefix = "X\x1b]0;title";
        stream.Render(prefix.ToCharArray()); await stream.SetCursorBlinkAsync(false);
        stream.Render(On.ToCharArray());
        CollectionAssert.AreEqual(new[] { prefix, On }, calls);
    }

    [TestMethod]
    [DataRow("\x1b]0;[?12h \u009b2 q", "\a")]
    [DataRow("\x1b[?25;120", "h")]
    [DataRow("\x1b[12", "h")]
    [DataRow("\x1b[?12$", "p")]
    [DataRow("\x1b[7 ", "q")]
    [DataRow("\x1b[?1 ", "q")]
    [DataRow("\x1b[1  ", "q")]
    public async Task CursorLookalikesDoNotCancelTheRequest(string prefix, string final)
    {
        var (stream, calls) = await StartRecording();
        stream.Render(prefix.ToCharArray()); await stream.SetCursorBlinkAsync(false);
        stream.Render(final.ToCharArray());
        CollectionAssert.AreEqual(new[] { prefix, final + Off }, calls);
    }

    [TestMethod, DataRow(31), DataRow(32)]
    public async Task OnlyPrivateModesWithinTheNativeParameterLimitCount(int precedingModes)
    {
        var (stream, calls) = await StartRecording();
        var prefix = "\x1b[?" + new string(';', precedingModes) + "12";
        stream.Render(prefix.ToCharArray()); await stream.SetCursorBlinkAsync(false);
        stream.Render(['h']);
        CollectionAssert.AreEqual(new[] { prefix, precedingModes == 31 ? "h" : "h" + Off }, calls);
    }

    [TestMethod]
    [DataRow("\x1b[4;1H")] // the three-frame split-CUP reproduction
    [DataRow("\x1b[?1;2:3 $p")]
    [DataRow("\x1b[1\x7f;\a2m")]
    [DataRow("\x1b(0")]
    [DataRow("\x1b#\u007f8")]
    [DataRow("\x1b]8;id=keep;https://host/path\x1b\\")]
    [DataRow("\x1b]8;;\x1b\\")]
    [DataRow("\x1b]0;title\a")]
    [DataRow("\x1bP1;0qABC\aDEF\x1b\\")]
    [DataRow("\x1bXpayload\a\x1b\\")]
    [DataRow("\x1b^payload\a\x1b\\")]
    [DataRow("\x1b_payload\a\x1b\\")]
    [DataRow("\x1b]0;title\x1b[4;1H")] // ESC aborts the OSC; it also starts the next CSI
    [DataRow("\x1bPignored\x1b]8;;url\a")]
    [DataRow("\x1b]0;title\u009cstill OSC\a")] // Windows ignores C1 by default, even ST
    [DataRow("\x1b]0;title\x18")]
    [DataRow("\x1bP1;2\x1a")]
    [DataRow("\x1b^private\x18")]
    [DataRow("\x1b[4;\x1a")]
    public async Task DeferredEditNeverConsumesAnyPartOfTheRemoteSequence(string sequence)
    {
        // Request a change after EVERY incomplete prefix; newer edits replace it.
        for (int split = 1; split < sequence.Length; split++)
        {
            var (stream, calls) = await StartRecording();
            string prefix = sequence[..split];
            stream.Render(prefix.ToCharArray());
            await stream.SetCursorBlinkAsync(true); await stream.SetCursorBlinkAsync(false);
            CollectionAssert.AreEqual(new[] { prefix }, calls, $"split={split}");
            // Frames don't need to end in ground; a safe cut inside one suffices.
            string next = On + "text\x1b[";
            stream.Render((sequence[split..] + next).ToCharArray());
            CollectionAssert.AreEqual(new[] { prefix, sequence[split..] + Off + next }, calls, $"split={split}");
        }
    }

    [TestMethod]
    [DataRow("\x1b 7", "\u0090$qm\u009c")]
    [DataRow("\x1b%@", "\u009d8;;url\u009c")]
    [DataRow("\x1b 7", "\u009b4;1H")]
    [DataRow("\x1b 7", "\u0098sos\u009c")]
    [DataRow("\x1b 7", "\u009eprivate\u009c")]
    [DataRow("\x1b 7", "\u009fapplication\u009c")]
    [DataRow("\x1b 7\x1b%G", "\x1b]8;;\u009cignored\a")]
    [DataRow("\x1b%@\x1b" + "c", "\x1b]8;;\u009cignored\a")]
    [DataRow("\x1b G", "\x1b]8;;\u009cignored\a")] // S8C1T changes replies, not the output parser
    public async Task C1IntroducersAndTerminatorsObeyTheControlsReceiveMode(string setup, string sequence)
    {
        var (stream, calls) = await StartRecording();
        var prefix = setup + sequence[..^1];
        stream.Render(prefix.ToCharArray()); await stream.SetCursorBlinkAsync(false);
        CollectionAssert.AreEqual(new[] { prefix }, calls);
        stream.Render((sequence[^1] + "text").ToCharArray());
        CollectionAssert.AreEqual(new[] { prefix, sequence[^1] + Off + "text" }, calls);
    }

    [TestMethod, DataRow("\x1bO"), DataRow("\x1bN"), DataRow("\x1b 7\u008f")]
    public async Task SingleShiftNeedsTheNextGraphicButNotTheNextControl(string shift)
    {
        var (stream, calls) = await StartRecording();
        stream.Render(shift.ToCharArray()); await stream.SetCursorBlinkAsync(false);
        CollectionAssert.AreEqual(new[] { shift, Off }, calls);
    }

    [TestMethod]
    public async Task SingleShiftOnOutputDoesNotSwallowTheFollowingOsc()
    {
        var (stream, calls) = await StartRecording();
        const string prefix = "\x1bO\x1b]8;;somewhere";
        stream.Render(prefix.ToCharArray()); await stream.SetCursorBlinkAsync(false);
        stream.Render("\x1b\\X".ToCharArray());
        CollectionAssert.AreEqual(new[] { prefix, "\x1b\\" + Off + "X" }, calls);
    }

    [TestMethod]
    [DataRow("\x1b[?25;0002;1l", "\x1bY <")] // VT52 direct cursor address
    [DataRow("\x1b[4*z", "macro could end in an unknown parser mode")]
    public async Task UnknownOrVt52ModeCannotTurnLocalCsiIntoPrintedText(string change, string extra)
    {
        var (stream, calls) = await StartRecording();
        stream.Render(change.ToCharArray()); await stream.SetCursorBlinkAsync(false);
        stream.Render(extra.ToCharArray()); stream.Render(['\x1b']);
        CollectionAssert.AreEqual(new[] { change, extra, "\x1b" }, calls);
        stream.Render("<prompt".ToCharArray());
        Assert.AreEqual("<" + Off + "prompt", calls[^1]);
    }

    [TestMethod, DataRow(true), DataRow(false)]
    public async Task ActiveCloseRevokesQueuedEditsAndAsynchronouslyJoinsNativeOutput(bool local)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var calls = new List<string>();
        var stream = new CursorBlinkStream(c => {
            calls.Add(new(c)); entered.SetResult();
            if (!release.Wait(Limit)) throw new TimeoutException();
        }, local ? true : null);
        var start = stream.StartAsync();
        Task painting = local ? start : Task.Run(() => stream.Render("prompt".ToCharArray()));
        await entered.Task.WaitAsync(Limit);
        Task update = stream.SetCursorBlinkAsync(false);
        Task remote = Task.Run(() => stream.Render("late".ToCharArray()));
        Task close = stream.CloseAsync();
        try { Assert.IsFalse(close.IsCompleted); await stream.SetCursorBlinkAsync(true).WaitAsync(Limit); }
        finally { release.Set(); }
        await Task.WhenAll(close, remote, painting, update).WaitAsync(Limit);
        CollectionAssert.AreEqual(new[] { local ? On : "prompt" }, calls);
    }

    [TestMethod]
    public async Task EditFromWithinNativeRenderDoesNotReenterItOrWaitForMoreTraffic()
    {
        var calls = new List<string>();
        CursorBlinkStream stream = null!;
        Task edit = Task.CompletedTask;
        stream = new(c => {
            calls.Add(new(c));
            if (calls.Count == 1)
            {
                _ = stream.SetCursorBlinkAsync(false);
                edit = stream.SetCursorBlinkAsync(true);
                Assert.IsFalse(edit.IsCompleted);
                CollectionAssert.AreEqual(new[] { "prompt\r" }, calls);
            }
        }, null);
        await stream.StartAsync();
        await Task.Run(() => stream.Render("prompt\r".ToCharArray())).WaitAsync(Limit);
        await edit.WaitAsync(Limit);
        CollectionAssert.AreEqual(new[] { "prompt\r", On }, calls);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("\u009c")] // ignored C1 does not dispatch on a fresh output parser
    [DataRow("\x1b[?12:3h")] // rejected DECSET and DECSCUSR do not choose blink or clear REP
    [DataRow("\x1b[1:2 q")]
    [DataRow("\x1b[?12?h")]
    [DataRow("\x1b[3:4b")] // unsupported subparameters: no dispatch epilogue
    [DataRow("\x1b[1??b")] // ignored malformed CSI does not dispatch at all
    [DataRow("\x1b[ $2m")]
    public async Task PlainPromptCanStillBeRepeatedAfterAToggle(string ignored)
    {
        var (stream, calls) = await StartRecording();
        stream.Render("test:Y".ToCharArray()); await stream.SetCursorBlinkAsync(true);
        stream.Render((ignored + "\x1b[").ToCharArray()); await stream.SetCursorBlinkAsync(false);
        // Y is still repeatable: in particular, never inject on the idle prompt,
        // before the split REP, or after a malformed sequence that preserves Y.
        stream.Render("3b".ToCharArray());
        CollectionAssert.AreEqual(new[] { "test:Y", ignored + "\x1b[", "3b" + Off }, calls);
    }

    [TestMethod, DataRow("prompt ", "x"), DataRow("prompt ", "\ud83c\udf31"), DataRow("\ud83c", "\udf31x")]
    public async Task NextGraphicSupersedesTheOldOneWithoutSplittingASurrogatePair(string first, string more)
    {
        var (stream, calls) = await StartRecording();
        stream.Render(first.ToCharArray());
        await stream.SetCursorBlinkAsync(false);
        stream.Render((more + "\x1b[3b").ToCharArray());
        string next = char.IsLowSurrogate(more[0]) ? more[..1] + Off + more[1..] : Off + more;
        CollectionAssert.AreEqual(new[] { first, next + "\x1b[3b" }, calls);
    }

    [TestMethod]
    [DataRow("\r")]
    [DataRow("\x18")]
    [DataRow("\x1b[3J")]
    [DataRow("\x1b]8;;url\a")]
    [DataRow("\x1bPqdata\x1b\\")]
    [DataRow("\x1b(0")]
    public async Task KnownDispatchReleasesIdleEditAfterTheAction(string action)
    {
        var (stream, calls) = await StartRecording();
        stream.Render("X".ToCharArray()); await stream.SetCursorBlinkAsync(false);
        stream.Render(action.ToCharArray());
        CollectionAssert.AreEqual(new[] { "X", action + Off }, calls);
    }

    [TestMethod]
    public async Task SplitQueryRepliesRemainOneSynchronousAutomaticFrameBeforeCredit()
    {
        using var peer = new Peer(); await using var core = await peer.Connect();
        var calls = new List<string>(); TerminalAttachment? lease = null;
        var stream = new CursorBlinkStream(c => {
            string text = new(c); calls.Add(text);
            if (text.EndsWith('n')) lease!.Input("\x1b[3;4R\x1b[3;4R"u8, synchronousReply: true);
        }, null);
        await stream.StartAsync(); lease = await peer.Attach(core, stream.Render);
        await peer.Write(4, 0, "\x1b[6"u8.ToArray()); await peer.Credit(4, 3);
        await stream.SetCursorBlinkAsync(false);
        byte[] suffix = "n\x1b[6n"u8.ToArray(); await peer.Write(4, 0, suffix);
        CollectionAssert.AreEqual("\x1b[3;4R\x1b[3;4R"u8.ToArray(), await peer.Expect(4, 6));
        await peer.Credit(4, suffix.Length); await peer.Fence();
        CollectionAssert.AreEqual(new[] { "\x1b[6", "n" + Off + "\x1b[6n" }, calls);
        await stream.CloseAsync(); await peer.Detach(lease, 4);
    }
}
