using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

[TestClass]
public class CheckoutRpcTests
{
    const string Alias = "studio", MachineId = "core-studio";
    const string Local = Peer.Address, Remote = Alias + "/" + Local;

    static object Foreground(string cwd) => new { pid = 123, name = "sh", argv = new[] { "sh" }, cwd };
    static object Terminal(string address, string cwd, string? foreground = null) => new {
        address, state = "live", cwd, foreground = foreground is null ? null : Foreground(foreground) };
    static object Space(string id, string name, params string[] panes) => new { id, name, folder = "/saved/layout-folder",
        tabs = panes.Select(terminal => new { root = new { type = "block", content = new { terminal } } }).ToArray() };
    static Task Layout(Peer p, int id, params object[] spaces) => p.Reply(id, new { value = new { spaces } });
    static Task Checkout(Peer p, int id, string top, string branch = "main") => p.Reply(id, new { code = 0,
        timed_out = false, truncated = false, stdout = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{top}\n{branch}\n")), stderr = "" });
    static string Name(CoreSession s, string address) => s.Terminals.Single(t => t.Address == address).Space.DisplayName;

    static async Task<CoreSession> Open(Peer p, object[] terminals, bool remote = false, bool exec = true)
    {
        var connecting = CoreSession.ConnectAsync(p.Endpoint);
        await p.Hello(machines: remote, docs: true, exec: exec);
        if (remote)
        {
            await p.Call("machine.list");
            await p.Reply(4, new { machines = new[] { new { id = MachineId, name = Alias, state = "connected" } } });
        }
        await p.Call("events.subscribe"); await p.Reply(2, new { channel = 9, seq = 80, terminals });
        return await connecting.WaitAsync(TimeSpan.FromSeconds(4));
    }

    static async Task<(int Id, JsonElement Args)> Request(Peer p, string method)
    {
        using var doc = JsonDocument.Parse(await p.Expect(0, 0));
        var call = doc.RootElement;
        Assert.AreEqual(method, call.GetProperty("method").GetString());
        return (call.GetProperty("id").GetInt32(), call.GetProperty("params").Clone());
    }

    static async Task<int> Probe(Peer p, string cwd, string? on = null)
    {
        var (id, args) = await Request(p, "machine.exec");
        CollectionAssert.AreEquivalent(on is null ? new[] { "argv", "timeout" } : new[] { "argv", "timeout", "on" },
            args.EnumerateObject().Select(field => field.Name).ToArray());
        if (on != null) Assert.AreEqual(on, args.GetProperty("on").GetString());
        Assert.AreEqual(30, args.GetProperty("timeout").GetInt32());
        CollectionAssert.AreEqual(new[] { "git", "-C", cwd, "rev-parse", "--show-toplevel", "--abbrev-ref", "HEAD" },
            args.GetProperty("argv").EnumerateArray().Select(arg => arg.GetString()).ToArray());
        return id;
    }

    // Credit is the reader's acknowledgment: prior replies have been processed, and an unexpected RPC fails the test.
    static async Task Fence(Peer p)
    {
        byte[] heartbeat = "{\"method\":\"heartbeat\",\"params\":{}}\n"u8.ToArray();
        await p.Write(9, 0, heartbeat); await p.Credit(9, heartbeat.Length);
    }

    [TestMethod]
    public async Task LocalAndRemoteTwinsAtTheSameForegroundCwdUseTheirOwnCheckout()
    {
        const string cwd = "/src/shared checkout/worktree/nested folder";
        using var p = new Peer();
        await using var s = await Open(p, [Terminal(Local, "/launch/local", cwd), Terminal(Remote, "/launch/remote", cwd)], remote: true);
        var docs = new[] { await Request(p, "doc.get"), await Request(p, "doc.get") };
        await Fence(p); // A known cwd without a layout is not a reason to probe.
        var space = Space("sp1", "", Local); // Both documents use machine-local pane IDs.
        await Layout(p, docs.Single(d => !d.Args.TryGetProperty("on", out _)).Id, space);
        int local = await Probe(p, cwd);
        await Layout(p, docs.Single(d => d.Args.TryGetProperty("on", out var on) && on.GetString() == MachineId).Id, space);
        int remote = await Probe(p, cwd, MachineId);
        Assert.AreNotEqual(local, remote);

        await Checkout(p, remote, "/src/shared checkout/worktree", "HEAD"); await Fence(p);
        Assert.AreEqual("worktree", Name(s, Remote)); Assert.AreEqual("nested folder", Name(s, Local));
        await Checkout(p, local, "/src/shared checkout", "topic/local"); await Fence(p);
        Assert.AreEqual("shared checkout", Name(s, Local)); Assert.AreEqual("worktree", Name(s, Remote));
    }

    [TestMethod]
    public async Task OnlyTheFirstTerminalOfAnUnnamedSpaceIsProbed()
    {
        using var p = new Peer();
        await using var s = await Open(p, [Terminal("t1", "/chosen/first"), Terminal("t2", "/other/second"),
            Terminal("t3", "/explicit/name"), Peer.Target("t4"), Terminal("t5", "/other/known")]);
        await Layout(p, (await Request(p, "doc.get")).Id,
            Space("work", "", "t1", "t2"), Space("named", "Pinned", "t3"), Space("unknown", "", "t4", "t5"));
        int first = await Probe(p, "/chosen/first"); // No foreground: use the first terminal's launch cwd.
        await Checkout(p, first, "/chosen"); await Fence(p);
        Assert.AreEqual("chosen", Name(s, "t1")); Assert.AreEqual("chosen", Name(s, "t2"));
        Assert.AreEqual("Pinned", Name(s, "t3")); Assert.AreEqual("layout-folder", Name(s, "t5"));
    }

    [TestMethod]
    public async Task LateReplyForOldForegroundCannotRenameTheCurrentCheckout()
    {
        using var p = new Peer();
        await using var s = await Open(p, [Terminal(Local, "/launch", "/old/repo/src")]);
        await Layout(p, (await Request(p, "doc.get")).Id, Space("work", "", Local));
        int stale = await Probe(p, "/old/repo/src");
        byte[] change = [..JsonSerializer.SerializeToUtf8Bytes(new { method = "event", @params = new {
            seq = 81, type = "foreground", terminal = Local, data = Foreground("/work/new checkout/lib") } }), 10];
        await p.Write(9, 0, change);
        int current = await Probe(p, "/work/new checkout/lib"); await p.Credit(9, change.Length);
        Assert.AreNotEqual(stale, current);
        await Checkout(p, current, "/work/new checkout"); await Fence(p);
        Assert.AreEqual("new checkout", Name(s, Local));
        await Checkout(p, stale, "/old/repo"); await Fence(p);
        Assert.AreEqual("new checkout", Name(s, Local));
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task MissingExecOrRpcErrorKeepsTheForegroundFolder(bool advertised)
    {
        using var p = new Peer();
        await using var s = await Open(p, [Terminal(Local, "/launch", "/working/current folder")], exec: advertised);
        await Layout(p, (await Request(p, "doc.get")).Id, Space("work", "", Local));
        if (advertised)
        {
            int id = await Probe(p, "/working/current folder");
            await p.Write(0, 0, JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id,
                error = new { code = -32601, message = "unsupported" } }));
        }
        await Fence(p); // No exec without the capability, no retry or disconnect after an optional RPC error.
        Assert.AreEqual("current folder", Name(s, Local));
    }
}
