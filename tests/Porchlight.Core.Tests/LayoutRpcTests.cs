using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

[TestClass]
public class LayoutRpcTests
{
    static object Layout(string name) => new { value = new { spaces = new[] { new { id = "sp1", name,
        tabs = new[] { new { root = new { type = "block", content = new { terminal = Peer.Address } } } } } } } };

    static async Task<int> ReadDocument(Peer p, string? on = null)
    {
        using var doc = JsonDocument.Parse(await p.Expect(0, 0));
        var call = doc.RootElement;
        Assert.AreEqual("doc.get", call.GetProperty("method").GetString());
        var args = call.GetProperty("params");
        Assert.AreEqual("herdr-app", args.GetProperty("name").GetString());
        if (on == null) Assert.IsFalse(args.TryGetProperty("on", out _));
        else Assert.AreEqual(on, args.GetProperty("on").GetString());
        int id = call.GetProperty("id").GetInt32(); Assert.IsTrue(id >= 5); return id;
    }

    [TestMethod]
    public async Task RemoteAndLocalTwinReceiveOnlyTheirOwnLayouts()
    {
        using var p = new Peer(); var open = CoreSession.ConnectAsync(p.Endpoint);
        await p.Hello(machines: true, docs: true); await p.Call("machine.list");
        await p.Reply(4, new { machines = new[] { new { id = "core-studio", name = "studio", state = "connected" },
            new { id = "offline", name = "away", state = "unreachable" } } });
        await p.Call("events.subscribe");
        await p.Reply(2, new { channel = 9, seq = 80, terminals = new[] { Peer.Target(), Peer.Target("studio/t74") } });
        await using var s = await open.WaitAsync(TimeSpan.FromSeconds(4));
        int local = await ReadDocument(p), remote = await ReadDocument(p, "core-studio");
        Assert.AreNotEqual(local, remote);
        string Space(string address) => s.Terminals.Single(t => t.Address == address).Space.DisplayName;
        await p.Reply(remote, Layout("Studio")); await p.Fence();
        Assert.AreEqual("Studio", Space("studio/t74")); Assert.AreEqual("Unassigned", Space("t74"));
        await p.Reply(local, Layout("Work")); await p.Fence();
        Assert.AreEqual("Studio", Space("studio/t74")); Assert.AreEqual("Work", Space("t74"));

        byte[] change = [..JsonSerializer.SerializeToUtf8Bytes(new { method = "event", @params = new {
            seq = 81, type = "doc", machine = "studio", data = new { name = "herdr-app" } } }), 10];
        await p.Write(9, 0, change);
        int refresh = await ReadDocument(p, "core-studio"); await p.Credit(9, change.Length);
        await p.Reply(refresh, Layout("Updated")); await p.Fence();
        Assert.AreEqual("Updated", Space("studio/t74")); Assert.AreEqual("Work", Space("t74"));
    }

    [TestMethod, DataRow("original"), DataRow("replacement")]
    public async Task RetiredReplyCannotOverwriteReconnectedOrReassignedMachine(string currentId)
    {
        using var p = new Peer(); var open = CoreSession.ConnectAsync(p.Endpoint);
        Task Listing(string id, string state) => p.Reply(4, new { machines = new[] { new { id, name = "studio", state } } });
        await p.Hello(machines: true, docs: true); await p.Call("machine.list"); await Listing("original", "connected");
        await p.Call("events.subscribe");
        await p.Reply(2, new { channel = 9, seq = 80, terminals = new[] { Peer.Target("studio/t74") } });
        await using var s = await open.WaitAsync(TimeSpan.FromSeconds(4));
        await ReadDocument(p); // Leave the local request pending throughout both refreshes.
        int retired = await ReadDocument(p, "original");
        s.RefreshMachines(); await p.Call("machine.list"); await Listing("original", "unreachable"); await p.Fence();
        Assert.IsEmpty(s.Terminals);
        s.RefreshMachines(); await p.Call("machine.list"); await Listing(currentId, "connected");
        int current = await ReadDocument(p, currentId); Assert.AreNotEqual(retired, current);
        await p.Reply(current, Layout("Current")); await p.Reply(retired, Layout("Stale")); await p.Fence();
        Assert.AreEqual("Current", s.Terminals.Single().Space.DisplayName);
    }

    [TestMethod]
    public async Task DirtyReadRetriesOnceAfterOptionalErrorWhileAttachIsPending()
    {
        using var p = new Peer(); var open = CoreSession.ConnectAsync(p.Endpoint);
        await p.Hello(docs: true); await p.Call("events.subscribe"); await p.Subscribe();
        await using var s = await open.WaitAsync(TimeSpan.FromSeconds(4));
        int first = await ReadDocument(p);
        foreach (int seq in new[]{81,82})
        {
            byte[] missed = [..JsonSerializer.SerializeToUtf8Bytes(new { method = "missed", @params = new { seq, terminals = new[] { Peer.Target() } } }), 10];
            await p.Write(9, 0, missed); await p.Credit(9, missed.Length); // no duplicate request before reply
        }
        var lease = s.Attach(Peer.Address, 24, 80, _ => {}, s.Identity.Id);
        var (attachId, _) = await p.Request("terminal.attach"); Assert.AreNotEqual(first, attachId);
        await p.Write(0, 0, JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id = first, error = new { code = -32601, message = "unsupported" } }));
        int retry = await ReadDocument(p); Assert.AreNotEqual(first, retry); Assert.AreNotEqual(attachId, retry);
        await p.Attached(attachId); Assert.IsTrue(await lease.Accepted.WaitAsync(TimeSpan.FromSeconds(4)));
        await p.Reply(retry, Layout("Work")); await p.Fence();
        Assert.AreEqual("Work", s.Terminals.Single().Space.DisplayName);
        await p.Detach(lease,4); await p.Fence();
    }
}
