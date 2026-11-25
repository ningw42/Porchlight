using Microsoft.VisualStudio.TestTools.UnitTesting;
using Porchlight.Core;

namespace Porchlight.Core.Tests;

[TestClass]
public class EndpointTests
{
    [TestMethod]
    public void EndpointResolutionMatchesWindowsCore_NotHostingTerminalHint()
    {
        var env = new Dictionary<string,string> { ["USERNAME"] = "A_B!", ["HC_SESSION"] = " work ",
            ["HC_TERMINAL_SOCKET"] = @"\\.\pipe\parent", ["XDG_CONFIG_HOME"] = @"C:\config" };
        CoreEndpoint Resolve(string config) => CoreEndpoint.Discover(k => env.GetValueOrDefault(k), path => {
            Assert.AreEqual(@"C:\config\herdr-core\config.toml", path); return config;
        });
        Assert.AreEqual(@"\\.\pipe\herdr-core-AB-work.sock", Resolve("[template]\nsocket='not top level'").Path);
        Assert.AreEqual(@"\\.\pipe\custom", Resolve("socket = '\\\\.\\pipe\\custom' # comment").Path);
        Assert.ThrowsExactly<FormatException>(() => Resolve("[[invalid"));
        Assert.ThrowsExactly<ArgumentException>(() => Resolve("socket='/tmp/not-a-windows-pipe'"));
        env["HC_SOCKET"] = @"\\.\pipe\chosen";
        Assert.AreEqual(env["HC_SOCKET"], Resolve("[[invalid").Path);
        env["HC_SOCKET"] = @"\\remote\pipe\denied";
        Assert.ThrowsExactly<ArgumentException>(() => Resolve(""));
    }
}
