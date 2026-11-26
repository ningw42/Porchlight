using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

[TestClass]
public class AttachmentTests
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    static void LateCallbacks(TerminalAttachment old)
    {
        old.Input("old key"u8); old.Input("old reply"u8, synchronousReply: true);
        old.Resize(10, 19); old.Focus(false);
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task DetachOrExitThenAttachAnotherTerminalOnTheSamePipe(bool exit)
    {
        using var p = new Peer(); await using var s = await p.Connect();
        var aText = new StringBuilder(); var bText = new StringBuilder();
        var a = s.Attach(Peer.Address, 24, 80, chars => aText.Append(chars), s.Identity.Id);
        int aId = (await p.Request("terminal.attach")).Id;
        await p.Attached(aId, auto: !exit); Assert.IsTrue(await a.Accepted.WaitAsync(Limit));
        a.Input("a"u8, synchronousReply: true);
        CollectionAssert.AreEqual("a"u8.ToArray(), await p.Expect(4, exit ? (byte)0 : (byte)6));
        await p.Write(4, 0, [(byte)'A', 0xe2]); await p.Credit(4, 2); // unfinished UTF-8 scalar belongs to A

        byte[] created = "{\"method\":\"event\",\"params\":{\"seq\":81,\"type\":\"created\",\"terminal\":\"t8\",\"data\":{\"address\":\"t8\",\"state\":\"live\"}}}\n"u8.ToArray();
        await p.Write(9, 0, created); await p.Credit(9, created.Length);
        if (exit)
        {
            await p.Write(4, 2, "{\"reason\":\"exited\",\"exit_code\":0}"u8.ToArray());
            Assert.AreEqual(EndKind.Exited, (await a.Completion.WaitAsync(Limit)).Kind);
        }
        else await p.Detach(a, 4);
        Assert.IsFalse(s.Completion.IsCompleted);
        string frozen = aText.ToString(); StringAssert.StartsWith(frozen, "A");

        var b = s.Attach("t8", 32, 100, chars => bText.Append(chars), s.Identity.Id);
        var (bId, args) = await p.Request("terminal.attach");
        Assert.AreNotEqual(aId, bId); Assert.AreEqual("t8", args.GetProperty("terminal").GetString());
        LateCallbacks(a); b.Input("b"u8, synchronousReply: true); // queue only B; use B's negotiated gate
        uint bChannel = exit ? 7u : 4u; // a confirmed close permits reusing the numeric channel
        await p.Attached(bId, bChannel, "t8", auto: exit); Assert.IsTrue(await b.Accepted.WaitAsync(Limit));
        CollectionAssert.AreEqual("b"u8.ToArray(), await p.Expect(bChannel, exit ? (byte)6 : (byte)0));
        await a.DisposeAsync(); LateCallbacks(a); // old viewer finally shuts down while B is active
        await p.Write(bChannel, 0, "B"u8.ToArray()); await p.Credit(bChannel, 1);
        Assert.AreEqual("B", bText.ToString()); Assert.AreEqual(frozen, aText.ToString());
        await p.Detach(b, bChannel); await p.Fence();
    }

    [TestMethod]
    public async Task DisposedBeforeReplySendsOnlyCloseAndHoldsSlotUntilAck()
    {
        using var p = new Peer(); await using var s = await p.Connect();
        var a = s.Attach(Peer.Address, 24, 80, _ => {}, s.Identity.Id);
        int id = (await p.Request("terminal.attach")).Id;
        LateCallbacks(a); var closing = a.DisposeAsync(); LateCallbacks(a);
        void NoOverlap() => Assert.ThrowsExactly<CoreException>(() => s.Attach(Peer.Address, 24, 80, _ => {}, s.Identity.Id));
        NoOverlap(); await p.Fence(); // before the reply the client doesn't even know the channel
        await p.Attached(id); Assert.IsTrue(await a.Accepted.WaitAsync(Limit));
        Assert.IsEmpty(await p.Expect(4, 2)); Assert.IsFalse(a.Completion.IsCompleted);
        NoOverlap(); LateCallbacks(a); await p.Write(4, 2, []);
        await closing.AsTask().WaitAsync(Limit); Assert.AreEqual(EndKind.Detached, (await a.Completion).Kind);
        var b = await p.Attach(s, _ => {}); await p.Detach(b, 4);
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task OrdinaryRefusalDiscardsOnlyThatLeasesInput(bool disposed)
    {
        using var p = new Peer(); await using var s = await p.Connect();
        var a = s.Attach(Peer.Address, 24, 80, _ => {}, s.Identity.Id);
        int id = (await p.Request("terminal.attach")).Id; LateCallbacks(a);
        var close = disposed ? a.DisposeAsync().AsTask() : Task.CompletedTask;
        await p.Refuse(id); Assert.IsFalse(await a.Accepted.WaitAsync(Limit));
        Assert.AreNotEqual(EndKind.Exited, (await a.Completion.WaitAsync(Limit)).Kind);
        await close.WaitAsync(Limit); await p.Fence();

        var b = s.Attach(Peer.Address, 24, 80, _ => {}, s.Identity.Id);
        int next = (await p.Request("terminal.attach")).Id; Assert.AreNotEqual(id, next);
        LateCallbacks(a); b.Focus(true); await p.Attached(next, 7);
        Assert.IsTrue(await b.Accepted.WaitAsync(Limit));
        CollectionAssert.AreEqual(new byte[]{1}, await p.Expect(7, 4));
        await p.Detach(b, 7);
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task MissingReplyOrDetachAckRetiresUncertainPipe(bool accepted)
    {
        using var p = new Peer(); await using var s = await p.Connect();
        var lease = s.Attach(Peer.Address, 24, 80, _ => {}, s.Identity.Id);
        int id = (await p.Request("terminal.attach")).Id;
        if (accepted) { await p.Attached(id); Assert.IsTrue(await lease.Accepted.WaitAsync(Limit)); }
        var closing = lease.DisposeAsync();
        if (accepted) Assert.IsEmpty(await p.Expect(4, 2));
        // The peer never answers. Without a trusted close boundary, do not reuse the pipe.
        await closing.AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(accepted, await lease.Accepted);
        Assert.AreNotEqual(EndKind.Exited, (await lease.Completion).Kind);
        Assert.AreNotEqual(EndKind.Exited, (await s.Completion.WaitAsync(Limit)).Kind);
        Assert.ThrowsExactly<CoreException>(() => s.Attach(Peer.Address, 24, 80, _ => {}, s.Identity.Id));
    }

    [TestMethod]
    public async Task ClosingDuringRenderWaitsForCallbackAndDropsItsLateReplyAndCredit()
    {
        using var p = new Peer(); await using var s = await p.Connect();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        TerminalAttachment? a = null;
        bool returned = false;
        a = await p.Attach(s, _ => {
            entered.SetResult(); if (!release.Wait(Limit)) throw new TimeoutException();
            LateCallbacks(a!); returned = true;
        });
        await p.Write(4, 0, "paint"u8.ToArray()); await entered.Task.WaitAsync(Limit);
        var closing = a.DisposeAsync();
        try
        {
            Assert.IsEmpty(await p.Expect(4, 2)); await p.Write(4, 2, []);
            Assert.IsFalse(a.Completion.IsCompleted);
            Assert.ThrowsExactly<CoreException>(() => s.Attach(Peer.Address, 24, 80, _ => {}, s.Identity.Id));
        }
        finally { release.Set(); }
        await closing.AsTask().WaitAsync(Limit);
        Assert.AreEqual(EndKind.Detached, (await a.Completion).Kind); Assert.IsTrue(returned);
        await p.Fence(); // no render reply, input, or credit on the closed channel
    }

    [TestMethod]
    public async Task BackedUpInputNeverCrossesTheNextAttachRequest()
    {
        using var p = new Peer(); await using var s = await p.Connect();
        var a = await p.Attach(s, _ => {});
        byte[] bulk = new byte[1 << 20], old = "queued for A"u8.ToArray();
        a.Input(bulk); // Stop reading mid-frame while the client has a backlog for A.
        Assert.AreEqual((4u, (byte)0, bulk.Length), await p.ReadHeader());
        for (int i = 0; i < 16; i++) a.Input(old);
        await p.Write(4, 2, "{\"reason\":\"exited\"}"u8.ToArray());
        async Task<TerminalAttachment> NextViewer()
        {
            Assert.AreEqual(EndKind.Exited, (await a.Completion.WaitAsync(Limit)).Kind);
            return s.Attach(Peer.Address, 24, 80, _ => {}, s.Identity.Id);
        }
        var next = NextViewer(); await p.ReadPayload(bulk.Length);
        int id;
        for (int count = 0;; count++)
        {
            var frame = await p.ReadHeader(); var payload = await p.ReadPayload(frame.Length);
            Assert.AreEqual((byte)0, frame.Kind);
            if (frame.Channel == 0)
            {
                using var request = JsonDocument.Parse(payload);
                Assert.AreEqual("terminal.attach", request.RootElement.GetProperty("method").GetString());
                id = request.RootElement.GetProperty("id").GetInt32(); break;
            }
            // Already written frames can still be in transit, but only before the new attach.
            Assert.IsTrue(count < 16); Assert.AreEqual(4u, frame.Channel); CollectionAssert.AreEqual(old, payload);
        }
        var b = await next; await p.Attached(id, 4); Assert.IsTrue(await b.Accepted.WaitAsync(Limit));
        b.Input("B"u8); CollectionAssert.AreEqual("B"u8.ToArray(), await p.Expect(4, 0));
        await p.Detach(b, 4);
    }

    [TestMethod]
    public async Task EmptyCloseCannotAcknowledgeACloseStillQueuedBehindInput()
    {
        using var p = new Peer(); await using var s = await p.Connect();
        var a = await p.Attach(s, _ => {});
        byte[] bulk = new byte[1 << 20]; a.Input(bulk);
        Assert.AreEqual((4u, (byte)0, bulk.Length), await p.ReadHeader()); // leave the writer blocked in A's payload
        var closing = a.DisposeAsync();
        await p.Write(4, 2, []); // the peer has not received a CLOSE and has no process exit reason
        await closing.AsTask().WaitAsync(Limit);
        Assert.AreEqual(EndKind.Disconnected, (await a.Completion).Kind);
        Assert.AreEqual(EndKind.Disconnected, (await s.Completion.WaitAsync(Limit)).Kind);
    }

    [TestMethod]
    public async Task DataOnRetiredChannelFailsClosedWithoutDeliveringToNewViewer()
    {
        using var p = new Peer(); await using var s = await p.Connect();
        var a = await p.Attach(s, _ => {}); await p.Detach(a, 4);
        var rendered = new StringBuilder();
        var b = s.Attach(Peer.Address, 24, 80, chars => rendered.Append(chars), s.Identity.Id);
        await p.Attached((await p.Request("terminal.attach")).Id, 7); Assert.IsTrue(await b.Accepted.WaitAsync(Limit));
        await p.Write(4, 0, "stale frame"u8.ToArray());
        Assert.AreEqual(EndKind.Disconnected, (await b.Completion.WaitAsync(Limit)).Kind);
        Assert.AreEqual(EndKind.Disconnected, (await s.Completion.WaitAsync(Limit)).Kind);
        Assert.AreEqual("", rendered.ToString()); Assert.AreEqual(EndKind.Detached, (await a.Completion).Kind);
    }
}
