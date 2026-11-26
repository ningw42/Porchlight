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
        // Let a complete CLOSE ack reach the pipe while the client reader is blocked in
        // render. A zero output quota can make the fake peer wait for that very reader.
        pipe = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            inBufferSize: 4096, outBufferSize: 4096);
    }
    internal async Task Hello(bool auto = true, int generation = 1, bool machines = false, bool docs = false, bool exec = false)
    {
        await pipe.WaitForConnectionAsync(deadline.Token);
        using var line = new MemoryStream(); byte[] one = [0];
        do { await pipe.ReadExactlyAsync(one, deadline.Token); if (one[0] != 10) line.WriteByte(one[0]); } while (one[0] != 10);
        var msg = JsonDocument.Parse(line.ToArray()).RootElement;
        Assert.AreEqual("hello", msg.GetProperty("method").GetString()); Assert.IsTrue(msg.GetProperty("params").GetProperty("framed").GetBoolean());
        var methods = new List<string> { "events.subscribe", "terminal.attach" };
        if (machines) methods.Add("machine.list");
        if (docs) methods.Add("doc.get");
        if (exec) methods.Add("machine.exec");
        await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc="2.0", id=1, result=new { core=new { id="peer", name="test" },
            version="test", generation, framed=true, methods, features=auto ? new[]{"automatic_input"} : [] } }), deadline.Token);
        await pipe.WriteAsync("\n"u8.ToArray(), deadline.Token);
    }
    internal async Task<(uint Channel, byte Kind, int Length)> ReadHeader()
    {
        byte[] h = new byte[9]; await pipe.ReadExactlyAsync(h, deadline.Token);
        uint n = BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(5)); Assert.IsTrue(n <= 16 << 20);
        return (BinaryPrimitives.ReadUInt32LittleEndian(h), h[4], (int)n);
    }
    internal async Task<byte[]> ReadPayload(int length)
    {
        byte[] b = new byte[length]; await pipe.ReadExactlyAsync(b, deadline.Token); return b;
    }
    internal async Task<byte[]> Expect(uint ch, byte kind)
    {
        var frame = await ReadHeader(); Assert.AreEqual(ch,frame.Channel); Assert.AreEqual(kind,frame.Kind);
        return await ReadPayload(frame.Length);
    }
    internal async Task ExpectEnd()
    {
        try { Assert.AreEqual(0,await pipe.ReadAsync(new byte[1],deadline.Token)); }
        catch (IOException) { } // A broken pipe also signals the client closed its end.
    }
    internal async Task<(int Id, JsonElement Args)> Request(string method)
    {
        using var doc = JsonDocument.Parse(await Expect(0,0));
        var value = doc.RootElement;
        Assert.AreEqual(method,value.GetProperty("method").GetString());
        return (value.GetProperty("id").GetInt32(), value.GetProperty("params").Clone());
    }
    internal async Task<JsonElement> Call(string method) => (await Request(method)).Args;
    internal async Task Write(uint ch, byte kind, byte[] b, int? declaredLength = null)
    {
        byte[] h = new byte[9]; BinaryPrimitives.WriteUInt32LittleEndian(h,ch); h[4]=kind;
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(5),declaredLength ?? b.Length);
        await pipe.WriteAsync(h.AsMemory(0,3), deadline.Token); await pipe.WriteAsync(h.AsMemory(3), deadline.Token);
        await pipe.WriteAsync(b, deadline.Token);
    }
    internal Task Reply(int id, object result) => Write(0,0,JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc="2.0",id,result }));
    internal Task Subscribe() => Reply(2,new {channel=9, seq=80, terminals=new[]{Target(),Target("other/t10")} });
    internal Task Attached(int id, uint channel=4, string address=Address, bool auto=true) =>
        Reply(id,new { channel, terminal=Target(address), automatic_input=auto });
    internal Task Refuse(int id) => Write(0,0,JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc="2.0",id,
        error=new { code=-32000,message="Target no longer available" } }));
    internal async Task Credit(uint ch,int len) => Assert.AreEqual(len,BinaryPrimitives.ReadInt32LittleEndian(await Expect(ch,1)));
    internal async Task<CoreSession> Connect(bool auto=true)
    {
        var task = CoreSession.ConnectAsync(Endpoint); await Hello(auto);
        await Call("events.subscribe"); await Subscribe(); return await task;
    }
    internal async Task<TerminalAttachment> Attach(CoreSession s, Action<char[]> consume)
    {
        var lease = s.Attach(Address,24,80,consume,s.Identity.Id);
        await Attached((await Request("terminal.attach")).Id);
        Assert.IsTrue(await lease.Accepted.WaitAsync(deadline.Token)); return lease;
    }
    internal async Task Detach(TerminalAttachment lease, uint channel)
    {
        var close = lease.DisposeAsync(); Assert.IsEmpty(await Expect(channel,2));
        await Write(channel,2,[]); await close.AsTask().WaitAsync(deadline.Token);
        Assert.AreEqual(EndKind.Detached,(await lease.Completion).Kind);
    }
    // The credit fences all preceding inbound frames. Any stray outbound frame fails Expect.
    internal async Task Fence()
    {
        byte[] heartbeat = "{\"method\":\"heartbeat\",\"params\":{}}\n"u8.ToArray();
        await Write(9,0,heartbeat); await Credit(9,heartbeat.Length);
    }
    public void Dispose() { pipe.Dispose(); deadline.Dispose(); }
}
