using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

[TestClass]
public class SpaceNameTests
{
    [TestMethod]
    [DataRow("/home/me/", "/home/me", "~")]
    [DataRow("", "/home/me", "~")]
    [DataRow("/", "/home/me", "/")]
    [DataRow(@"\", @"C:\Users\me", @"\")]
    [DataRow(@"C:\", @"C:\Users\me", "C:")]
    [DataRow(@"\\?\C:\repos\worktree\", @"C:\Users\me", "worktree")]
    [DataRow(@"C:/repos\mixed/path/", @"C:\Users\me", "path")]
    public void FormatsFoldersLikeHerdR(string folder, string home, string expected) =>
        Assert.AreEqual(expected, SpaceLayout.FolderName(folder, home));

    [TestMethod]
    [DataRow(@"\\server\share\checkout")]
    [DataRow(@"/\server\share\checkout")]
    [DataRow(@"\\?\UNC\server\share")]
    [DataRow(@"\\.\pipe\test")]
    [DataRow("relative/project")]
    public void UntrustedNonLocalOrRelativePathNeverStartsACheckoutProbe(string path)
    {
        var probes = new CheckoutNames();
        Assert.IsNull(probes.Lookup(new("core-id", "machine", "connected", false), path));
        probes.FinishProjection(() => throw new AssertFailedException("Unexpected request"), _ => Assert.Fail());
    }

    [TestMethod]
    [DataRow("/repo/worktree\nHEAD\n", 0, false, "/repo/worktree")]
    [DataRow("C:/src/repo\r\nmain\r\n", 0, false, "C:/src/repo")]
    [DataRow("/repo\n", 0, false, null)]
    [DataRow("/repo\nmain\nextra\n", 0, false, null)]
    [DataRow("/repo\nmain\n", 1, false, null)]
    [DataRow("/repo\nmain\n", 0, true, null)]
    public void OnlyCompleteTwoLineGitSuccessSuppliesACheckout(string output, int code, bool truncated, string? expected)
    {
        var machine = new HerdMachine("core", "local", "connected", true);
        var probes = new CheckoutNames();
        probes.Lookup(machine, "/repo/sub");
        probes.FinishProjection(() => 17, _ => { });
        var reply = System.Text.Json.JsonSerializer.SerializeToElement(new { result = new { code, truncated,
            stdout = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(output)) } });
        Assert.IsTrue(probes.Receive(17, reply));
        Assert.AreEqual(expected, probes.Lookup(machine, "/repo/sub"));
    }

    [TestMethod]
    public void WindowsHomeComparisonFoldsAsciiCaseOnly()
    {
        Assert.AreEqual(OperatingSystem.IsWindows() ? "~" : "ME", SpaceLayout.FolderName(@"c:\Üsers\ME\", @"C:\Üsers\me"));
        Assert.AreEqual("me", SpaceLayout.FolderName(@"C:\üsers\me", @"C:\Üsers\me"));
    }

    [TestMethod]
    public void IdenticallyNumberedLocalAndRemotePanesAreNamedFromTheirOwnMachine()
    {
        var catalog = new TerminalCatalog(viewerHome: @"C:\Users\me");
        catalog.Snapshot(Wire.Parse("""
          {"seq":1,"terminals":[{"state":"live","address":"t1","cwd":"C:\\Users\\me"},
                                 {"state":"live","address":"studio/t1","cwd":"/src/remote-project"}]}
          """u8.ToArray()));
        var layout = SpaceLayout.Read(Wire.Parse("""
          {"spaces":[{"id":"s","folder":"/fallback/saved","tabs":[{"root":{"id":"pane","type":"block","content":{"terminal":"t1"}}}]},
                     {"id":"empty","folder":"C:\\Users\\me"}]}
          """u8.ToArray()));
        var local = new HerdMachine("a", "localhost", "connected", true);
        var remote = new HerdMachine("b", "studio", "connected", false);
        var directory = catalog.Directory([local, remote], new Dictionary<string,SpaceLayout> { ["a"] = layout, ["b"] = layout });
        var localSpaces = directory.SpacesFor(local);
        Assert.AreEqual(2, localSpaces.Count);
        Assert.IsTrue(localSpaces.All(s => s.DisplayName == "~"));
        Assert.AreEqual("~", directory.Terminals.Single(t => t.Address == "t1").Space.DisplayName);
        Assert.AreEqual("remote-project", directory.Terminals.Single(t => t.Address == "studio/t1").Space.DisplayName);
    }

    [TestMethod]
    public void LetterInTerminalIdDoesNotHideThePaneOrFallBackToTheSavedHome()
    {
        var catalog = new TerminalCatalog(viewerHome: @"C:\Users\me");
        catalog.Snapshot(Wire.Parse("""
          {"seq":1,"terminals":[{"state":"live","address":"t1z","foreground":{"cwd":"C:\\Repos\\Eureka"}},
                                 {"state":"live","address":"studio/t1z","foreground":{"cwd":"/repos/Remote"}}]}
          """u8.ToArray()));
        var layout = SpaceLayout.Read(Wire.Parse("""
          {"spaces":[{"id":"s","folder":"C:\\Users\\me","tabs":[{"root":{"type":"block","content":{"terminal":"t1z"}}}]}]}
          """u8.ToArray()));
        var local = new HerdMachine("local", "host", "connected", true);
        var remote = new HerdMachine("other", "studio", "connected", false);
        var directory = catalog.Directory([local, remote], new Dictionary<string,SpaceLayout> { [local.Id] = layout, [remote.Id] = layout });
        var space = directory.SpacesFor(local).Single();
        Assert.AreEqual("Eureka", space.DisplayName);
        Assert.AreEqual("t1z", directory.TerminalsIn(local, space).Single().Address);
        Assert.AreEqual("Remote", directory.SpacesFor(remote).Single().DisplayName);
    }

    [TestMethod]
    public void NameUsesFirstTerminalInTabAndTreeOrder_NotActivePaneFloatOrFirstKnownFolder()
    {
        var layout = SpaceLayout.Read(Wire.Parse("""
          {"spaces":[{"id":"s","folder":"/saved/project","tabs":[
            {"root":{"type":"block","content":{"mod":"dashboard"},"floating":[{"content":{"terminal":"t9"}}]}},
            {"root":{"type":"stack","active":"p2","children":[
              {"type":"row","children":[{"type":"block","id":"p1","hidden":true,"content":{"terminal":"t1"}}]},
              {"type":"block","id":"p2","content":{"terminal":"t2"}}]}},
            {"root":{"type":"block","content":{"terminal":"t3"}}}]},
            {"id":"explicit","name":"Chosen","tabs":[{"root":{"type":"block","content":{"terminal":"t4"}}}]},
            {"id":"empty"}]}
          """u8.ToArray()));
        string? firstFolder = null;
        Dictionary<string,TerminalSpace> Names() => layout.NameSpaces(address => {
            Assert.AreEqual("t1", address, "Do not inspect a later pane or one in an explicitly named space");
            return firstFolder;
        }, "/home/me");
        Assert.AreEqual("project", Names()["s"].DisplayName);
        firstFolder = "/home/me"; Assert.AreEqual("~", Names()["s"].DisplayName);
        firstFolder = "/repos/linked-checkout"; Assert.AreEqual("linked-checkout", Names()["s"].DisplayName);
        Assert.AreEqual("Chosen", Names()["explicit"].DisplayName);
        Assert.AreEqual("~", Names()["empty"].DisplayName);
    }
}
