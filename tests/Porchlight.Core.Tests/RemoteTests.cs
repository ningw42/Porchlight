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
    public async Task MachineListIncludesEmptyAndOffline_ButOnlyAvailableDirectTerminalsAreSelectable()
    {
        using var p = new Peer(); await using var s = await Open(p);
        Assert.AreEqual(4, s.Directory.Machines.Count); Assert.IsTrue(s.Directory.Machines[0].IsLocal);
        Assert.IsFalse(s.Directory.Machines.Single(m => m.Name == "away").Available);
        CollectionAssert.AreEquivalent(new[]{Local,Remote}, s.Terminals.Select(t => t.Address).ToArray());
        foreach (var invalid in new[]{"away/t1","ghost/t1","studio/nested/t4","studio/t99"})
            Assert.ThrowsExactly<CoreException>(() => s.AttachAsync(invalid,24,80,_=>{}).GetAwaiter().GetResult());
        Assert.ThrowsExactly<CoreException>(() => s.AttachAsync(Remote,24,80,_=>{},"wrong-core").GetAwaiter().GetResult());
    }

    [TestMethod, DataRow(Remote,true), DataRow(Local,false), DataRow("another/t74",false)]
    public async Task RelayedAttachPinsCoreIdAndRequiresFullQualifiedReply(string replyAddress, bool valid)
    {
        using var p = new Peer(); await using var s = await Open(p);
        // Opening the machine dropdown can race a click on Attach. Replies may be interleaved.
        s.RefreshMachines(); s.RefreshMachines();
        var attached = s.AttachAsync(Remote,26,89,_=>{},Id);
        s.Input([65]); await p.Call("machine.list");
        var q = await p.Call("terminal.attach"); Assert.AreEqual(Id + "/" + Local, q.GetProperty("terminal").GetString());
        await Machines(p, Machine());
        await p.Reply(3,new {channel=4,terminal=Peer.Target(replyAddress),automatic_input=false});
        Assert.AreEqual(valid,await attached);
        if (!valid) { Assert.AreEqual(EndKind.Disconnected,(await s.Completion).Kind); return; }
        CollectionAssert.AreEqual(new byte[]{65},await p.Expect(4,0));
        s.Input([27,91,99], synchronousReply:true); // older remote path: must still send DATA
        CollectionAssert.AreEqual(new byte[]{27,91,99},await p.Expect(4,0));
        await p.Write(4,0,"remote paint"u8.ToArray()); await p.Credit(4,12);
        var close = s.DisposeAsync(); await p.Expect(4,2); await p.Write(4,2,[]); await close;
        Assert.AreEqual(EndKind.Detached,(await s.Completion).Kind);
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
        await Refresh("unreachable"); Assert.AreEqual(Local,s.Terminals.Single().Address);
        await Refresh("connected"); Assert.AreEqual(2,s.Terminals.Count);
        var evt = JsonSerializer.SerializeToUtf8Bytes(new { method="event", @params=new {seq=81, type="removed", terminal=Remote} });
        await p.Write(9,0,[..evt,10]); await p.Credit(9,evt.Length+1);
        Assert.AreEqual(Local,s.Terminals.Single().Address);
    }
}
