using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Porchlight.Core.Configuration;

namespace Porchlight.Core.Tests;

[TestClass]
public class TerminalSchemeTests
{
    static Dictionary<string, string> Colors() => new() {
        ["name"]="Example", ["background"]="#1e1e2e", ["foreground"]="#cdd6f4", ["selectionBackground"]="#585B70", ["cursorColor"]="#F5E0DC",
        ["brightWhite"]="#0f0000", ["brightCyan"]="#0e0000", ["brightPurple"]="#0d0000", ["brightBlue"]="#0c0000",
        ["brightYellow"]="#0b0000", ["brightGreen"]="#0a0000", ["brightRed"]="#090000", ["brightBlack"]="#080000",
        ["white"]="#070000", ["cyan"]="#060000", ["purple"]="#050000", ["blue"]="#040000",
        ["yellow"]="#030000", ["green"]="#020000", ["red"]="#010000", ["black"]="#000000"
    };
    static TerminalScheme Read(Dictionary<string,string> colors, string name = "Example")
    {
        string json = "{ /* settings.json */ \"schemes\":[{\"name\":\"Unrelated\"}," + JsonSerializer.Serialize(colors) + ",],}";
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return TerminalScheme.Read(input,name);
    }
    [TestMethod]
    public void NamedSchemeUsesAnsiIndexOrderAndAllFourSpecialColors()
    {
        var s = Read(Colors());
        Assert.AreEqual(new Rgb(30,30,46),s.Background); Assert.AreEqual(new Rgb(205,214,244),s.Foreground);
        Assert.AreEqual(new Rgb(245,224,220),s.Cursor); Assert.AreEqual(new Rgb(88,91,112),s.Selection);
        Assert.AreEqual(16,s.Ansi.Count);
        for (byte i=0;i<16;i++) Assert.AreEqual(new Rgb(i,0,0),s.Ansi[i]);
    }
    [TestMethod]
    public void MissingSchemeOrIncompletePaletteDoesNotSilentlyDefault()
    {
        var colors = Colors();
        Assert.ThrowsExactly<FormatException>(() => Read(colors,"Absent"));
        colors["blue"]="#gg0000";
        StringAssert.Contains(Assert.ThrowsExactly<FormatException>(() => Read(colors)).Message,"blue");
        colors.Remove("cursorColor");
        StringAssert.Contains(Assert.ThrowsExactly<FormatException>(() => Read(colors)).Message,"cursorColor");
    }
}
