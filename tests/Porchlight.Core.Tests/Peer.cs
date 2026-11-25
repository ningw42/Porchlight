using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

// Independent wire oracle: a single private random pipe, no CLI, no core, no child processes.
sealed class Peer : IDisposable
{
    internal const string Address = "t74";
    internal static object Target(string id = Address) => new { address = id, state = "live", name = "fixture" };
    readonly NamedPipeServerStream pipe;
    readonly CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
    internal CoreEndpoint Endpoint { get; }
    internal Peer()
    {
        var name = "porchlight-test-" + Guid.NewGuid().ToString("N");
        Endpoint = CoreEndpoint.Select(CoreEndpoint.Prefix + name);
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
    internal async Task Hello(bool auto = true, int generation = 1, bool machines = false)
    {
        await pipe.WaitForConnectionAsync(deadline.Token);
        using var line = new MemoryStream(); byte[] one = [0];
        do { await pipe.ReadExactlyAsync(one, deadline.Token); if (one[0] != 10) line.WriteByte(one[0]); } while (one[0] != 10);
        var msg = JsonDocument.Parse(line.ToArray()).RootElement;
        Assert.AreEqual("hello", msg.GetProperty("method").GetString()); Assert.IsTrue(msg.GetProperty("params").GetProperty("framed").GetBoolean());
        await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc="2.0", id=1, result=new { core=new { id="peer", name="test" },
            version="test", generation, framed=true, methods=machines ? new[]{"events.subscribe","terminal.attach","machine.list"} : ["events.subscribe","terminal.attach"], features=auto ? new[]{"automatic_input"} : [] } }), deadline.Token);
        await pipe.WriteAsync("\n"u8.ToArray(), deadline.Token);
    }
    internal async Task<byte[]> Expect(uint ch, byte kind)
    {
        byte[] h = new byte[9]; await pipe.ReadExactlyAsync(h, deadline.Token);
        Assert.AreEqual(ch, BinaryPrimitives.ReadUInt32LittleEndian(h)); Assert.AreEqual(kind,h[4]);
        var n = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(5)); Assert.IsTrue(n <= 16 << 20);
        byte[] b = new byte[n]; await pipe.ReadExactlyAsync(b, deadline.Token); return b;
    }
    internal async Task<JsonElement> Call(string method)
    {
        var value = JsonDocument.Parse(await Expect(0,0)).RootElement;
        Assert.AreEqual(method,value.GetProperty("method").GetString()); return value.GetProperty("params");
    }
    internal async Task Write(uint ch, byte kind, byte[] b, int? declaredLength = null)
    {
        byte[] h = new byte[9]; BinaryPrimitives.WriteUInt32LittleEndian(h,ch); h[4]=kind;
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(5),declaredLength ?? b.Length);
        await pipe.WriteAsync(h.AsMemory(0,3), deadline.Token); await pipe.WriteAsync(h.AsMemory(3), deadline.Token);
        await pipe.WriteAsync(b, deadline.Token);
    }
    internal Task Reply(int id, object result) => Write(0,0,JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc="2.0",id,result }));
    internal Task Subscribe() => Reply(2,new {channel=9, seq=80, terminals=new[]{Target(),Target("other/t10")} });
    internal Task Attached(bool auto=true) => Reply(3,new { channel=4, terminal=Target(), automatic_input=auto });
    internal async Task Credit(uint ch,int len) => Assert.AreEqual(len,BinaryPrimitives.ReadInt32LittleEndian(await Expect(ch,1)));
    internal async Task<CoreSession> Connect(bool auto=true)
    {
        var task = CoreSession.ConnectAsync(Endpoint); await Hello(auto);
        await Call("events.subscribe"); await Subscribe(); return await task;
    }
    internal async Task Attach(CoreSession s, Action<char[]> consume)
    {
        var ack = s.AttachAsync(Address,24,80,consume); await Call("terminal.attach"); await Attached(); Assert.IsTrue(await ack);
    }
    public void Dispose() { pipe.Dispose(); deadline.Dispose(); }
}
