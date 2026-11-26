using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

[TestClass]
public class RemoteTests
{
    const string Alias = "studio", Id = "core-studio", Local = Peer.Address, Remote = Alias + "/" + Local;
    static object Machine(string name = Alias, string id = Id, string state = "connected") => new { id, name, state };
    static Task Machines(Peer p, params object[] machines) => p.Reply(4, new { machines });

    static async Task<CoreSession> Open(Peer p)
    {
        var open = CoreSession.ConnectAsync(p.Endpoint);
        await p.Hello(machines: true);
        await p.Call("machine.list"); await Machines(p, Machine(), Machine("empty", "other"), Machine("away", "absent", "needs_login"));
        await p.Call("events.subscribe");
        await p.Reply(2, new { channel = 9, seq = 80, terminals = new[] { Peer.Target(Local), Peer.Target(Remote),
            Peer.Target("away/t1"), Peer.Target("ghost/t1"), Peer.Target("studio/nested/t4") } });
        return await open;
    }

    [TestMethod]
    public void OnlyHerdRIdShapesAreReservedAsTerminalAddresses()
    {
        foreach (var id in new[] { "t1", "t19", "t1b", "t1z", "t65jk" })
        {
            Assert.IsTrue(LiveTerminal.IsAddress(id), id);
            Assert.IsTrue(LiveTerminal.IsAddress("studio/" + id), id);
            Assert.IsFalse(MachineDirectory.ValidAlias(id), id);
        }
        foreach (var alias in new[] { "t0", "tmp", "t1l", "t1a", "t3po" })
        {
            Assert.IsFalse(LiveTerminal.IsAddress(alias), alias);
            Assert.IsTrue(MachineDirectory.ValidAlias(alias), alias);
        }
        Assert.IsFalse(LiveTerminal.IsAddress("t1Z"));
    }

    [TestMethod]
    public async Task MachineListIncludesEmptyAndOffline_ButOnlyAvailableDirectTerminalsAreSelectable()
    {
        using var p = new Peer(); await using var s = await Open(p);
        Assert.AreEqual(4, s.Directory.Machines.Count); Assert.IsTrue(s.Directory.Machines[0].IsLocal);
        Assert.IsFalse(s.Directory.Machines.Single(m => m.Name == "away").Available);
        CollectionAssert.AreEquivalent(new[]{Local,Remote}, s.Terminals.Select(t => t.Address).ToArray());
        foreach (var invalid in new[]{"away/t1","ghost/t1","studio/nested/t4","studio/t99"})
            Assert.ThrowsExactly<CoreException>(() => s.Attach(invalid,24,80,_=>{},Id));
        Assert.ThrowsExactly<CoreException>(() => s.Attach(Remote,24,80,_=>{},"wrong-core"));
    }

    [TestMethod, DataRow(Remote,Id,true), DataRow(Local,Id,false), DataRow("another/t74",Id,false), DataRow(Remote,"replacement",false)]
    public async Task RelayedAttachPinsCoreIdAndRequiresFullQualifiedReply(string replyAddress, string refreshedId, bool valid)
    {
        using var p = new Peer(); await using var s = await Open(p);
        // Refreshing the machine directory can race a click on Attach. Replies may be interleaved.
        s.RefreshMachines(); s.RefreshMachines();
        var lease = s.Attach(Remote,26,89,_=>{},Id);
        lease.Input([65]); await p.Call("machine.list");
        var (id,q) = await p.Request("terminal.attach"); Assert.AreEqual(Id + "/" + Local, q.GetProperty("terminal").GetString());
        await Machines(p, Machine(id:refreshedId)); // even a matching alias can now refer to a different machine
        await p.Attached(id,address:replyAddress,auto:false);
        Assert.AreEqual(valid,await lease.Accepted.WaitAsync(TimeSpan.FromSeconds(4)));
        if (!valid) { Assert.AreEqual(EndKind.Disconnected,(await s.Completion.WaitAsync(TimeSpan.FromSeconds(4))).Kind); await p.ExpectEnd(); return; }
        CollectionAssert.AreEqual(new byte[]{65},await p.Expect(4,0));
        lease.Input([27,91,99], synchronousReply:true); // older remote path: must still send DATA
        CollectionAssert.AreEqual(new byte[]{27,91,99},await p.Expect(4,0));
        await p.Write(4,0,"remote paint"u8.ToArray()); await p.Credit(4,12);
        await p.Detach(lease,4); await p.Fence();
    }

    [TestMethod]
    public async Task OfflineRefreshAndRemoteEventsDoNotRetargetTheLocalTwin()
    {
        using var p = new Peer(); await using var s = await Open(p);
        async Task Refresh(string state)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Notify() => changed.TrySetResult();
            s.CatalogChanged += Notify;
            try {
                s.RefreshMachines(); await p.Call("machine.list"); await Machines(p, Machine(state:state));
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(4));
            } finally { s.CatalogChanged -= Notify; }
        }
        // A local viewer does not freeze the remote picker or its machine.list subscription.
        var local = await p.Attach(s, _ => {});
        await Refresh("unreachable"); Assert.AreEqual(Local,s.Terminals.Single().Address);
        await Refresh("connected"); Assert.AreEqual(2,s.Terminals.Count);
        var evt = JsonSerializer.SerializeToUtf8Bytes(new { method="event", @params=new {seq=81, type="removed", terminal=Remote} });
        await p.Write(9,0,[..evt,10]); await p.Credit(9,evt.Length+1);
        Assert.AreEqual(Local,s.Terminals.Single().Address);
        local.Focus(true); CollectionAssert.AreEqual(new byte[]{1},await p.Expect(4,4));
        await p.Detach(local,4);
    }
}
