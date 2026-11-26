using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

[TestClass]
public class TerminalNameTests
{
    static readonly HerdMachine Local = new("local", "home", "connected", true), Remote = new("remote", "studio", "connected", false);
    static SpaceLayout NamedTab(string? name) => SpaceLayout.Read(JsonSerializer.SerializeToElement(new { spaces = new[] {
        new { id = "sp1", tabs = new[] { new { name, root = new { type = "row", children = new[] {
            new { type = "block", content = new { terminal = "t1" } }, new { type = "block", content = new { terminal = "t2" } } } } } } }
    } }));

    // Run against shipped defaults even on a developer desktop with a personal programs.json.
    static TerminalCatalog Catalog() => new(programNames: ProgramNames.Load(null));
    static bool Send(TerminalCatalog catalog, int seq, string type, object? data, string address = "t1") =>
        catalog.Consume([..JsonSerializer.SerializeToUtf8Bytes(new { method = "event", @params = new { seq, type, terminal = address, data } }), 10]);

    [TestMethod]
    public void ForegroundRoundTripUsesArgvAndNeverUsesTheOscTitleOfANonAgent()
    {
        var catalog = Catalog();
        catalog.Snapshot(Wire.Parse("""
            {"seq":4,"terminals":[{"state":"live","address":"t1","argv":["C:\\bin\\pwsh.exe"],"title":"OSC is not the program"},
                               {"state":"live","address":"studio/t1","foreground":{"argv":["/bin/tig"]}}]}
            """u8.ToArray()));
        string Title(string address = "t1") => catalog.Directory([Local, Remote]).Terminals.Single(t => t.Address == address).Title;
        Assert.AreEqual("pwsh", Title()); Assert.AreEqual("Git Changes", Title("studio/t1"));
        Assert.IsTrue(Send(catalog, 5, "foreground", new { name = "ignored", argv = new[] { @"C:\Tools\LAZYGIT.EXE" } }));
        Assert.AreEqual("Git Changes", Title());
        Assert.IsFalse(Send(catalog, 4, "foreground", null)); Assert.AreEqual("Git Changes", Title());
        Assert.IsTrue(Send(catalog, 6, "foreground", null)); Assert.AreEqual("pwsh", Title());
        Assert.AreEqual("Git Changes", Title("studio/t1"));
    }

    [TestMethod]
    public void RootAgentReportEnablesTheLiveTitleAndClearingItRestoresTheCurrentProgram()
    {
        var catalog = Catalog();
        catalog.Snapshot(Wire.Parse("""
            {"seq":1,"terminals":[{"state":"live","address":"t1","argv":["bash"],"name":"Launch name",
              "status":{"app":"claude-code","title":"Not the terminal title"},"title":"✳ Fix tests"}]}
            """u8.ToArray()));
        var layouts = new Dictionary<string, SpaceLayout> { [Local.Id] = NamedTab("") };
        string Title() => catalog.Directory([Local], layouts).Terminals.Single().Title;
        Assert.AreEqual("Fix tests", Title());
        layouts[Local.Id] = NamedTab("Review"); Assert.AreEqual("Review", Title());
        layouts[Local.Id] = NamedTab("Claude Code"); Assert.AreEqual("Fix tests", Title());
        layouts[Local.Id] = NamedTab("");
        Send(catalog, 2, "title", new { title = "✻ ~/checkout" }); Assert.AreEqual("Claude Code", Title());
        Send(catalog, 3, "title", new { title = "[ ! ] Update README" }); Assert.AreEqual("Update README", Title());
        Send(catalog, 4, "foreground", new { argv = new[] { "gitui" } }); Assert.AreEqual("Update README", Title());
        Send(catalog, 5, "status", new { status = (object?)null, records = new[] { new { app = "pi" } } });
        Assert.AreEqual("Git Changes", Title());
        Send(catalog, 6, "status", new { status = new { app = "pi" } }); Assert.AreEqual("Update README", Title());
        Send(catalog, 7, "title", new { title = "" }); Assert.AreEqual("Pi", Title());
        layouts.Clear(); Assert.AreEqual("Launch name", Title()); // No saved tab for the terminal.
    }

    [TestMethod]
    [DataRow("⠹ [topic] Review", "[topic] Review")]
    [DataRow("✻ [ . ] Ready", "Ready")]
    [DataRow("  π ⠇ Plan", "π ⠇ Plan")]
    [DataRow("π ⠇ Plan", "π Plan")]
    [DataRow("✓ “Opening quote”", "“Opening quote”")]
    [DataRow("💡 𐐀 topic", "𐐀 topic")]
    [DataRow("t1", "Claude Code")]
    [DataRow("CLAUDE", "Claude Code")]
    [DataRow(@"✻ C:\work", "Claude Code")]
    [DataRow("✓ /work", "Claude Code")]
    [DataRow("user@host", "Claude Code")]
    public void FiltersAgentTitlesLikeDefaultTabStrip(string title, string expected) =>
        Assert.AreEqual(expected, TabTitles.Format("t1", null, new("claude", "Claude Code"), true, TabTitles.Candidate(title)));

    [TestMethod]
    public void ExplicitTabNamesFollowPlacementNotTerminalIdOrSpaceLabel()
    {
        var catalog = Catalog();
        catalog.Snapshot(Wire.Parse("""
            {"seq":1,"terminals":[{"state":"live","address":"t1"},{"state":"live","address":"studio/t1"},
              {"state":"live","address":"studio/t2"},{"state":"live","address":"studio/t3","labels":{"herdr.space":"sp1"}}]}
            """u8.ToArray()));
        var layouts = new Dictionary<string, SpaceLayout> { [Local.Id] = NamedTab("Local work"), [Remote.Id] = NamedTab("Deploy") };
        Dictionary<string, string> Titles() => catalog.Directory([Local, Remote], layouts).Terminals.ToDictionary(t => t.Address, t => t.Title);
        var titles = Titles();
        Assert.AreEqual("Local work", titles["t1"]);
        Assert.AreEqual("Deploy", titles["studio/t1"]); Assert.AreEqual("Deploy", titles["studio/t2"]);
        Assert.AreNotEqual("Deploy", titles["studio/t3"]); // sharing a space is not sharing a tab
        layouts[Remote.Id] = NamedTab("Review"); Assert.AreEqual("Review", Titles()["studio/t1"]);
        layouts[Remote.Id] = NamedTab(""); Assert.AreNotEqual("Review", Titles()["studio/t1"]);
        Assert.AreEqual("Local work", Titles()["t1"]);
    }
}
