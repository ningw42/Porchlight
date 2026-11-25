using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

[TestClass]
public class SessionTests
{
    [TestMethod, DataRow(true,true), DataRow(true,false), DataRow(false,true), DataRow(false,false)]
    public async Task OrderedInputResizeAndCreditsAfterRender(bool hello, bool attach)
    {
        using var p = new Peer(); await using var s = await p.Connect(hello);
        Assert.AreEqual(Peer.Address,s.Terminals.Single().Address);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var rendered = new StringBuilder();
        var ack = s.AttachAsync(Peer.Address, 27, 92, chars => {
            rendered.Append(chars); entered.Set(); if (!release.Wait(5000)) throw new TimeoutException();
        });
        var q = await p.Call("terminal.attach");
        Assert.AreEqual(Peer.Address,q.GetProperty("terminal").GetString()); Assert.AreEqual(92,q.GetProperty("cols").GetInt32());
        Assert.AreEqual(27,q.GetProperty("rows").GetInt32()); Assert.AreEqual(2000,q.GetProperty("history").GetInt32());
        Assert.IsFalse(q.GetProperty("observe").GetBoolean());
        byte[] input = [27, 91, 99]; s.Input(input); input[0] = 0;
        s.Resize(0x103,0x201); await p.Attached(attach); Assert.IsTrue(await ack);
        CollectionAssert.AreEqual(new byte[]{27,91,99}, await p.Expect(4,0));
        CollectionAssert.AreEqual(new byte[]{1,2,3,1},await p.Expect(4,3));
        await p.Write(4,0,[0xe2]); await p.Credit(4,1); // decoder accepted a partial scalar
        await p.Write(4,0,[0x82,0xac]); Assert.IsTrue(entered.Wait(4000));
        try {
            s.Input([27,91,99],synchronousReply:true); s.Focus(false); // fence: no credit yet
            CollectionAssert.AreEqual(new byte[]{27,91,99},await p.Expect(4,hello && attach ? (byte)6 : (byte)0));
            CollectionAssert.AreEqual(new byte[]{0},await p.Expect(4,4));
        } finally { release.Set(); }
        await p.Credit(4,2); Assert.AreEqual("€",rendered.ToString());
        s.Input([1]); var dispose = s.DisposeAsync().AsTask(); s.Input([2]);
        CollectionAssert.AreEqual(new byte[]{1},await p.Expect(4,0)); Assert.IsEmpty(await p.Expect(4,2));
        await p.Write(4,2,[]); await dispose; Assert.AreEqual(new SessionEnd(EndKind.Detached,"Detached."),await s.Completion);
    }

    [TestMethod]
    public async Task SnapshotRace_SplitEventsAndMissedReplaceOnlyLocalLiveList()
    {
        using var p = new Peer(); var connecting = CoreSession.ConnectAsync(p.Endpoint);
        await p.Hello(); await p.Call("events.subscribe");
        var stale = Line("event",new {seq=80, type="removed", terminal=Peer.Address});
        await p.Write(9,0,stale[..8]); await p.Subscribe(); await p.Credit(9,8);
        await using var s = await connecting;
        Assert.ThrowsExactly<CoreException>(() => s.AttachAsync("other/t10",20,80,_=>{}).GetAwaiter().GetResult());
        var created = Line("event",new {seq=81,type="created",terminal="t8",data=Peer.Target("t8")});
        byte[] rest = [..stale[8..],..created]; await p.Write(9,0,rest); await p.Credit(9,rest.Length);
        Assert.AreEqual(2,s.Terminals.Count);
        var finished = Line("event",new {seq=82,type="finished",terminal="t8"}); await p.Write(9,0,finished); await p.Credit(9,finished.Length);
        Assert.AreEqual(Peer.Address,s.Terminals.Single().Address);
        var missed = Line("missed",new {seq=99,terminals=new[]{Peer.Target("t11")} }); await p.Write(9,0,missed); await p.Credit(9,missed.Length);
        Assert.AreEqual("t11",s.Terminals.Single().Address);
        var older = Line("missed",new {seq=80,terminals=new[]{Peer.Target()} }); await p.Write(9,0,older); await p.Credit(9,older.Length);
        Assert.AreEqual("t11",s.Terminals.Single().Address);
    }
    static byte[] Line(string method,object args) => [..JsonSerializer.SerializeToUtf8Bytes(new {jsonrpc="2.0",method,@params=args}),10];

    [TestMethod, DataRow("exit",EndKind.Exited), DataRow("lost",EndKind.Disconnected), DataRow("eof",EndKind.Disconnected),
     DataRow("empty",EndKind.Disconnected), DataRow("oversize",EndKind.Disconnected)]
    public async Task OnlyExplicitProcessCloseMeansExit(string mode, EndKind expected)
    {
        using var p = new Peer(); await using var s = await p.Connect(); await p.Attach(s,_=>{});
        switch(mode) {
            case "exit": await p.Write(4,2,"{\"reason\":\"exited\",\"exit_code\":7}"u8.ToArray()); break;
            case "lost": await p.Write(4,2,"{\"reason\":\"holder_lost\"}"u8.ToArray()); break;
            case "empty": await p.Write(4,2,[]); break;
            case "oversize": await p.Write(4,0,[],(16<<20)+1); break;
            case "eof": p.Dispose(); break;
        }
        var result = await s.Completion.WaitAsync(TimeSpan.FromSeconds(4)); Assert.AreEqual(expected,result.Kind);
        if (expected==EndKind.Exited) StringAssert.Contains(result.Message,"7");
    }

    [TestMethod]
    public async Task ClosingDuringAttachDoesNotWaitForRpcOrReportExit()
    {
        using var p = new Peer(); await using var s = await p.Connect();
        var attach = s.AttachAsync(Peer.Address,24,80,_=>{}); await p.Call("terminal.attach");
        await s.DisposeAsync(); Assert.IsFalse(await attach); Assert.AreEqual(EndKind.Detached,(await s.Completion).Kind);
    }
    [TestMethod]
    public async Task MissingDetachAckIsBoundedAndNotProgramExit()
    {
        using var p = new Peer(); await using var s = await p.Connect(); await p.Attach(s,_=>{});
        var close = s.DisposeAsync(); await p.Expect(4,2); await close.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(EndKind.Detached,(await s.Completion).Kind);
    }
    [TestMethod]
    public async Task WrongTerminalReplyCannotEnableWrites()
    {
        using var p = new Peer(); await using var s = await p.Connect(); var ack = s.AttachAsync(Peer.Address,24,80,_=>{});
        s.Input([65]); await p.Call("terminal.attach"); await p.Reply(3,new {channel=4,terminal=Peer.Target("t99")});
        Assert.IsFalse(await ack); Assert.AreEqual(EndKind.Disconnected,(await s.Completion).Kind);
    }
    [TestMethod]
    public async Task AbsentExplicitPipeFailsWithoutFallingBack()
    {
        var missing = CoreEndpoint.Select(CoreEndpoint.Prefix + "porchlight-absent-" + Guid.NewGuid().ToString("N"));
        var failure = await Assert.ThrowsExactlyAsync<CoreException>(() => CoreSession.ConnectAsync(missing));
        StringAssert.Contains(failure.Message,"selected pipe");
    }
    [TestMethod]
    public async Task SlowHelloHonorsWindowCancellation()
    {
        using var p = new Peer(); using var cancel = new CancellationTokenSource();
        var connecting = CoreSession.ConnectAsync(p.Endpoint,cancel.Token);
        await p.Hello(); await p.Call("events.subscribe"); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => connecting.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [TestMethod]
    public async Task RejectUnnegotiatedGeneration()
    {
        using var p = new Peer(); var connecting = CoreSession.ConnectAsync(p.Endpoint); await p.Hello(generation:2);
        var ex = await Assert.ThrowsExactlyAsync<CoreException>(() => connecting); StringAssert.Contains(ex.Message,"generation 1");
    }
}
