using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Porchlight.Core.Tests;

[TestClass]
public class SpaceTabMemoryTests
{
    static readonly HerdMachine Local = new("core-a", "host", "connected", true);
    static readonly HerdMachine Remote = new("core-b", "studio", "connected", false);
    static readonly TerminalSpace Work = new("sp1", "Work");
    static LiveTerminal[] Tabs(params string[] addresses) => addresses.Select(a => new LiveTerminal(a, null)).ToArray();

    [TestMethod]
    public void RestoreTheLastChoiceOrTheFirstLiveTabInStripOrder()
    {
        var memory = new SpaceTabMemory();
        Assert.AreEqual("t3", memory.Enter(Local, Work, Tabs("t3", "t2")));
        Assert.AreEqual("t3", memory.Enter(Local, Work, Tabs("t1", "t2", "t3")));
        memory.Remember(Local, Work, "t2");
        Assert.AreEqual("t2", memory.Enter(Local, Work with { DisplayName = "Renamed" }, Tabs("t1", "t2")));
        Assert.AreEqual("t1", memory.Enter(Local, Work, Tabs("t1", "t3")));
        Assert.IsNull(memory.Enter(Local, Work, []));
        Assert.AreEqual("t3", memory.Enter(Local, Work, Tabs("t3", "t1")));
    }

    [TestMethod]
    public void ChoicesBelongToTheWindowCoreAndSpaceNotTheAliasOrCaption()
    {
        var memory = new SpaceTabMemory();
        memory.Remember(Remote, Work, "studio/t2");
        memory.Remember(Local, TerminalSpace.Unassigned, "t3");
        Assert.AreEqual("t1", memory.Enter(Local, Work, Tabs("t1", "t2", "t3")));
        Assert.AreEqual("t3", memory.Enter(Local, TerminalSpace.Unassigned, Tabs("t1", "t3")));
        Assert.AreEqual("studio/t1", memory.Enter(Remote, Work with { Id = "sp2" }, Tabs("studio/t1", "studio/t2")));
        var renamed = Remote with { Name = "lab" };
        Assert.AreEqual("lab/t2", memory.Enter(renamed, Work, Tabs("lab/t1", "lab/t2")));
        Assert.AreEqual("studio/t1", memory.Enter(Remote with { Id = "replacement" }, Work, Tabs("studio/t1", "studio/t2")));
        Assert.AreEqual("studio/t1", new SpaceTabMemory().Enter(Remote, Work, Tabs("studio/t1", "studio/t2")));
    }
}
