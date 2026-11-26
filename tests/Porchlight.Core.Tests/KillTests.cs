using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

[TestClass]
public class KillTests
{
    const string Local = Peer.Address, Remote = "studio/" + Local, RemoteId = "core-studio";
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);

    static async Task ListMachines(Peer p, string id = RemoteId, string state = "connected") =>
        await p.Reply((await p.Request("machine.list")).Id, new { machines = new[] { new { name = "studio", id, state } } });

    static async Task<CoreSession> Open(Peer p, bool docs = false)
    {
        var connecting = CoreSession.ConnectAsync(p.Endpoint);
        await p.Hello(machines: true, kill: true, docs: docs); await ListMachines(p);
        await p.Reply((await p.Request("events.subscribe")).Id, new { channel = 9, seq = 80,
            terminals = new[] { Peer.Target(Local), Peer.Target(Remote) } });
        return await connecting.WaitAsync(Limit);
    }

    static async Task<int> ExpectKill(Peer p, string route)
    {
        var (id, args) = await p.Request("terminal.kill");
        Assert.AreEqual("terminal", args.EnumerateObject().Single().Name);
        Assert.AreEqual(route, args.GetProperty("terminal").GetString());
        return id;
    }

    static async Task Finished(Peer p, int seq, string terminal)
    {
        byte[] line = [.. JsonSerializer.SerializeToUtf8Bytes(new { method = "event", @params = new { seq, type = "finished", terminal } }), 10];
        await p.Write(9, 0, line); await p.Credit(9, line.Length);
    }

    [TestMethod]
    public async Task TwinsUseDistinctRoutesAndOnlyEventsRemoveThem()
    {
        using var p = new Peer(); await using var s = await Open(p);
        Assert.IsTrue(s.SupportsKill);
        var viewer = await p.Attach(s, _ => { });
        var remote = s.KillTerminalAsync(Remote, RemoteId);
        int remoteId = await ExpectKill(p, RemoteId + "/" + Local);
        await p.Reply(remoteId, new { address = Remote, state = "finished", reason = "killed" }); await remote.WaitAsync(Limit);
        CollectionAssert.AreEquivalent(new[] { Local, Remote }, s.Terminals.Select(t => t.Address).ToArray());
        await Finished(p, 81, Remote);
        Assert.AreEqual(Local, s.Terminals.Single().Address);

        var local = s.KillTerminalAsync(Local, s.Identity.Id);
        int localId = await ExpectKill(p, Local); Assert.AreNotEqual(remoteId, localId);
        await Finished(p, 82, Local); Assert.IsEmpty(s.Terminals);
        Assert.IsFalse(local.IsCompleted); // A catalog event isn't an RPC reply.
        await p.Reply(localId, new { address = Local, state = "finished", reason = "killed" }); await local.WaitAsync(Limit);
        Assert.IsFalse(viewer.Completion.IsCompleted); // Nor is the RPC ack a stream CLOSE.
        await p.Write(4, 2, "{\"reason\":\"killed\"}"u8.ToArray());
        Assert.AreEqual(EndKind.Exited, (await viewer.Completion.WaitAsync(Limit)).Kind);
        await p.Fence(); // No implicit detach, removal, or other follow-up request.
    }

    [TestMethod]
    public async Task MissingCapabilitySendsNothing()
    {
        using var p = new Peer(); await using var s = await p.Connect();
        Assert.IsFalse(s.SupportsKill);
        await Assert.ThrowsExactlyAsync<CoreException>(() => s.KillTerminalAsync(Local, s.Identity.Id).WaitAsync(Limit));
        await p.Fence();
    }

    [TestMethod]
    public async Task OfflineOrReassignedAliasDoesNotFallBackToTheLocalTwin()
    {
        using var p = new Peer(); await using var s = await Open(p);
        async Task Reject(string address, string expectedId) =>
            await Assert.ThrowsExactlyAsync<CoreException>(() => s.KillTerminalAsync(address, expectedId).WaitAsync(Limit));

        await Reject(Local, RemoteId); await Reject(Remote, s.Identity.Id); await Reject("studio/t99", RemoteId);
        s.RefreshMachines(); await ListMachines(p, state: "unreachable"); await p.Fence();
        await Reject(Remote, RemoteId);
        s.RefreshMachines(); await ListMachines(p, id: "replacement"); await p.Fence();
        Assert.IsTrue(s.Terminals.Any(t => t.Address == Remote)); // Same displayed address, different machine.
        await Reject(Remote, RemoteId); await p.Fence();
    }

    [TestMethod]
    public async Task RefusalCompletesOnlyTheKillWhileOtherRpcsInterleave()
    {
        using var p = new Peer(); await using var s = await Open(p, docs: true);
        int doc1 = (await p.Request("doc.get")).Id, doc2 = (await p.Request("doc.get")).Id;
        var close = s.KillTerminalAsync(Remote, RemoteId);
        int killId = await ExpectKill(p, RemoteId + "/" + Local);
        var viewer = s.Attach(Local, 24, 80, _ => { }, s.Identity.Id);
        int attachId = (await p.Request("terminal.attach")).Id;
        Assert.AreEqual(4, new[] { doc1, doc2, killId, attachId }.Distinct().Count());
        await p.Attached(attachId); await p.Refuse(doc2); await p.Reply(doc1, new { }); await p.Fence();
        Assert.IsTrue(await viewer.Accepted.WaitAsync(Limit)); Assert.IsFalse(close.IsCompleted);

        await p.Refuse(killId);
        var error = await Assert.ThrowsExactlyAsync<CoreException>(() => close.WaitAsync(Limit));
        StringAssert.Contains(error.Message, "refused terminal.kill");
        await p.Fence(); Assert.AreEqual(2, s.Terminals.Count);
        viewer.Focus(true); CollectionAssert.AreEqual(new byte[] { 1 }, await p.Expect(4, 4));
        var next = s.KillTerminalAsync(Local, s.Identity.Id);
        int nextId = await ExpectKill(p, Local); Assert.AreNotEqual(killId, nextId);
        await p.Reply(nextId, new { }); await next.WaitAsync(Limit);
        await p.Detach(viewer, 4);
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task WrongIdOrMalformedReplyCannotAcknowledgeTheKill(bool wrongId)
    {
        using var p = new Peer(); await using var s = await Open(p);
        var close = s.KillTerminalAsync(Local, s.Identity.Id); int id = await ExpectKill(p, Local);
        if (wrongId) await p.Reply(id + 2, new { }); // No such action is in flight.
        else await p.Write(0, 0, JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id,
            result = new { }, error = new { code = -32000, message = "ambiguous" } }));
        await Assert.ThrowsExactlyAsync<CoreException>(() => close.WaitAsync(Limit));
        Assert.AreEqual(EndKind.Disconnected, (await s.Completion.WaitAsync(Limit)).Kind);
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task PendingKillFailsOnLossOrSessionClose(bool closeSession)
    {
        using var p = new Peer(); await using var s = await Open(p);
        var close = s.KillTerminalAsync(Local, s.Identity.Id); await ExpectKill(p, Local);
        if (closeSession) await s.DisposeAsync().AsTask().WaitAsync(Limit); else p.Dispose();
        await Assert.ThrowsExactlyAsync<CoreException>(() => close.WaitAsync(Limit));
        Assert.AreEqual(closeSession ? EndKind.Detached : EndKind.Disconnected, (await s.Completion.WaitAsync(Limit)).Kind);
        await Assert.ThrowsExactlyAsync<CoreException>(() => s.KillTerminalAsync(Local, s.Identity.Id).WaitAsync(Limit));
    }

    [TestMethod]
    public async Task MissingAckTimesOutWithoutRetryAndLateReplyReleasesTheSingleSlot()
    {
        using var p = new Peer(timeoutSeconds: 25); await using var s = await Open(p);
        var close = s.KillTerminalAsync(Remote, RemoteId); int id = await ExpectKill(p, RemoteId + "/" + Local);
        async Task NoOverlap() =>
            await Assert.ThrowsExactlyAsync<CoreException>(() => s.KillTerminalAsync(Local, s.Identity.Id).WaitAsync(Limit));
        await NoOverlap(); await p.Fence();
        var error = await Assert.ThrowsExactlyAsync<CoreException>(() => close.WaitAsync(TimeSpan.FromSeconds(16)));
        StringAssert.Contains(error.Message, "did not confirm");
        await NoOverlap(); await p.Fence();
        await p.Reply(id, new { }); await p.Fence(); Assert.AreEqual(2, s.Terminals.Count);
        var next = s.KillTerminalAsync(Local, s.Identity.Id); int nextId = await ExpectKill(p, Local);
        Assert.AreNotEqual(id, nextId); await p.Reply(nextId, new { }); await next.WaitAsync(Limit);
    }
}
