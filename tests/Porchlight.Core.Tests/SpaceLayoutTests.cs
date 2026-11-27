using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

[TestClass]
public class SpaceLayoutTests
{
    static readonly HerdMachine Local = new("core-a", "actual-hostname", "connected", true);
    static readonly HerdMachine Remote = new("core-b", "studio", "connected", false);
    static object Terminal(string address, string? label = null) => new { address, state = "live", labels = new Dictionary<string, string?> { ["herdr.space"] = label } };

    [TestMethod]
    public void LayoutPanesWinOverLabelsAndMachineLocalIdsDoNotCollide()
    {
        var catalog = new TerminalCatalog();
        catalog.Snapshot(JsonSerializer.SerializeToElement(new { seq = 1, terminals = new[] {
            Terminal("t1", "sp2"), Terminal("t2"), Terminal("t3", "sp2"), Terminal("studio/t1", "sp1"), Terminal("studio/t2") } }));
        var local = SpaceLayout.Read(Wire.Parse("""
          {"spaces":[{"id":"sp1","name":"Review","tabs":[{"root":{"type":"stack","children":[{"type":"row","children":[{"type":"block","content":{"terminal":"t1"}}]}],"floating":[{"content":{"terminal":"t2"}}]}}]},
                     {"id":"sp2","name":"","folder":"C:\\projects\\Porchlight\\"},{"id":"sp3","name":"Empty"}]}
          """u8.ToArray()));
        var remote = SpaceLayout.Read(Wire.Parse("""{"spaces":[{"id":"sp1","name":"Remote work"}]}"""u8.ToArray()));
        var dir = catalog.Directory([Local, Remote], new Dictionary<string, SpaceLayout> { [Local.Id] = local, [Remote.Id] = remote });
        Assert.AreEqual("Local", Local.DisplayName); Assert.AreEqual("studio", Remote.DisplayName);
        CollectionAssert.AreEqual(new[] { "Review", "Porchlight", "Empty" }, dir.SpacesFor(Local).Select(s => s.DisplayName).ToArray());
        CollectionAssert.AreEqual(new[] { "t1", "t2" }, dir.TerminalsIn(Local, new("sp1", "Review")).Select(t => t.Address).ToArray());
        Assert.AreEqual("t3", dir.TerminalsIn(Local, new("sp2", "Porchlight")).Single().Address);
        Assert.AreEqual("studio/t1", dir.TerminalsIn(Remote, new("sp1", "Remote work")).Single().Address);
        Assert.AreEqual("studio/t2", dir.TerminalsIn(Remote, TerminalSpace.Unassigned).Single().Address);
    }

    [TestMethod]
    public void StartupUsesSidebarOrderButSkipsSpacesWithNoLiveTerminals()
    {
        var archive = new TerminalSpace("sp3", "Archive");
        var work = new TerminalSpace("sp2", "Work");
        var personal = new TerminalSpace("sp1", "Personal");
        var saved = new Dictionary<string, TerminalSpace[]> { [Local.Id] = [archive, work, personal] };
        var dir = new MachineDirectory([Local, Remote], [new("t1", null) { Space = personal },
            new("t2", null) { Space = work }, new("studio/t1", null)]) { LayoutSpaces = saved };
        Assert.AreEqual(work, dir.StartupSpace(Local, null));
        Assert.AreEqual(personal, dir.StartupSpace(Local, "t1"));
        Assert.AreEqual(TerminalSpace.Unassigned, dir.StartupSpace(Remote, null));
        Assert.IsNull(dir.StartupSpace(Local, "studio/t1")); // An explicit miss must not attach a neighbor.
        Assert.IsNull(dir.StartupSpace(Local, "t9"));
        var empty = new MachineDirectory([Local, Remote], []) { LayoutSpaces = saved };
        Assert.AreEqual(archive, empty.StartupSpace(Local, null));
        Assert.IsNull(empty.StartupSpace(Remote, null));
    }

    [TestMethod]
    public void OnlyAChangeToTheDocumentOrderMovesSavedRows()
    {
        var catalog = new TerminalCatalog();
        var layouts = new Dictionary<string, SpaceLayout>();
        void Publish(params TerminalSpace[] rows) => layouts[Local.Id] = SpaceLayout.Read(JsonSerializer.SerializeToElement(new {
            spaces = rows.Select(s => new { id = s.Id, name = s.DisplayName }) }));
        void AssertRows(params TerminalSpace[] expected) => CollectionAssert.AreEqual(expected,
            catalog.Directory([Local], layouts).SpacesFor(Local).ToArray());

        var first = new TerminalSpace("sp3", "Zulu");
        var second = new TerminalSpace("sp1", "Project");
        var third = new TerminalSpace("sp2", "Project");
        Publish(first, second, third, second with { DisplayName = "Ignored duplicate ID" });
        AssertRows(first, second, third);

        var a = new TerminalSpace("a", "a");
        var z = new TerminalSpace("z", "z");
        catalog.Snapshot(JsonSerializer.SerializeToElement(new { seq = 1, terminals = new[] {
            Terminal("t1", "z"), Terminal("t2"), Terminal("t3", "a"), Terminal("t4", "sp1"), Terminal("t5", "z") } }));
        AssertRows(first, second, third, a, z, TerminalSpace.Unassigned);

        first = first with { DisplayName = "Alpha" };
        Publish(first, second, third);
        AssertRows(first, second, third, a, z, TerminalSpace.Unassigned);
        Publish(third, first, second);
        AssertRows(third, first, second, a, z, TerminalSpace.Unassigned);

        layouts.Clear();
        AssertRows(a, new TerminalSpace("sp1", "sp1"), z, TerminalSpace.Unassigned);
    }

    [TestMethod]
    public void ReplacingLabelsMovesOrClearsMembershipWithoutInventingSpaceNames()
    {
        var catalog = new TerminalCatalog();
        catalog.Snapshot(JsonSerializer.SerializeToElement(new { seq = 1, terminals = new[] { Terminal("t1", "sp1") } }));
        void Labels(int seq, object labels) => Assert.IsTrue(catalog.Consume([..JsonSerializer.SerializeToUtf8Bytes(new {
            method = "event", @params = new { seq, type = "labels", terminal = "t1", data = new { labels } } }), 10]));
        Assert.AreEqual("sp1", catalog.Directory([Local]).SpacesFor(Local).Single().DisplayName);
        Labels(2, new Dictionary<string,string> { ["herdr.space"] = "sp2" });
        Assert.AreEqual("sp2", catalog.Directory([Local]).SpacesFor(Local).Single().Id);
        Labels(3, new { });
        Assert.AreEqual(TerminalSpace.Unassigned, catalog.Directory([Local]).SpacesFor(Local).Single());
    }
}
