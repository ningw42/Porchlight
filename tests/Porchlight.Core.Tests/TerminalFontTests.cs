using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Porchlight.Core.Configuration;

namespace Porchlight.Core.Tests;

[TestClass]
public class TerminalFontTests
{
    static TerminalFont Read(string json)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return TerminalFont.ReadWindowsTerminalDefaults(input);
    }

    [TestMethod]
    public void ImportPreservesQuotedOrderMetricsAndOnlyRequestedTags()
    {
        var font = Read("""
            { "profiles": { "defaults": { "font": {
              // Keep the face grammar intact, even a comma inside a family name.
              "face": "'A, B', 'C', 'D'", "size": 14, "weight": 375,
              "cellHeight": "1.18", "cellWidth": 1.05, "colorGlyphs": true, "builtinGlyphs": false,
              "features": { "ss02": 1, "zero": 0 }, "axes": { "wdth": 92.5 },
            } }, "list": [{ "font": { "face": "Not the shared default" } }] } }
            """);
        Assert.AreEqual("'A, B', 'C', 'D'",font.Face);
        Assert.AreEqual(14f,font.Size); Assert.AreEqual((ushort)375,font.Weight);
        Assert.AreEqual("1.18",font.CellHeight); Assert.AreEqual("1.05",font.CellWidth);
        Assert.IsFalse(font.BuiltinGlyphs); Assert.IsTrue(font.ColorGlyphs);
        Assert.AreEqual(2,font.Features.Count); Assert.AreEqual(1f,font.Features["ss02"]); Assert.AreEqual(0f,font.Features["zero"]);
        Assert.AreEqual(92.5f,font.Axes["wdth"]);
    }

    [TestMethod]
    public void NoFeatureMapMeansNoForcedFeatures()
    {
        var font = Read("""{"profiles":{"defaults":{"font":{"weight":"semi-bold"}}}}""");
        Assert.AreEqual((ushort)600,font.Weight); Assert.IsEmpty(font.Features);
        Assert.ThrowsExactly<FormatException>(() => Read("""{"profiles":{"list":[]}}"""));
    }
}
