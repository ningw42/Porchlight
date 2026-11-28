using Microsoft.VisualStudio.TestTools.UnitTesting;
using Porchlight.Core.Configuration;

namespace Porchlight.Core.Tests;

[TestClass]
public class FontPickerTests
{
    [TestMethod]
    public void ParseLaunchListForDiscoveryWithoutSplittingQuotedOrEscapedNames()
    {
        string[] names = ["Family, One", "O'Brien", "A\\B", "With  spaces", "Has \"quotes\"", "Consolas"];
        CollectionAssert.AreEqual(names, FontFamilyList.Parse(FontFamilyList.Format(names)).ToArray());
        CollectionAssert.AreEqual(new[] { "Family, One", "With Spaces", "Other,Family" },
            FontFamilyList.Parse("  , 'Family, One' , With   Spaces, Other\\,Family, 'unclosed").ToArray());
    }

    [TestMethod]
    public void IndividualChipsAndLigatureSwitchNeverCompeteForTheSameTag()
    {
        var launch = new Dictionary<string, float> { ["calt"] = 1, ["dlig"] = 0, ["rlig"] = 1, ["ss01"] = 0, ["cv11"] = 3 };
        var preview = new FontFeatureOverrides(launch);
        CollectionAssert.AreEquivalent(new[] { "cv11" }, preview.EnabledIndividualTags.ToArray());
        Assert.IsFalse(preview.CanReset);
        foreach (var tag in new[] { "calt", "liga", "clig", "dlig", "hlig", "rlig" })
            Assert.ThrowsExactly<ArgumentException>(() => preview.SetEnabled(tag, false));

        preview.SetEnabled("ss01", true); preview.SetEnabled("cv11", false); preview.SetEnabled("cv11", true);
        foreach (var on in new[] { false, true })
        {
            preview.SetLigaturesEnabled(on);
            Assert.AreEqual(on, preview.LigaturesEnabled);
            foreach (var tag in CodingLigatures.Tags) Assert.AreEqual(on ? 1f : 0f, preview.Values[tag]);
            Assert.AreEqual(1f, preview.Values["rlig"]); Assert.AreEqual(3f, preview.Values["cv11"]);
            CollectionAssert.AreEquivalent(new[] { "ss01", "cv11" }, preview.EnabledIndividualTags.ToArray());
        }
        preview.Reset(); Assert.IsFalse(preview.CanReset);
        Assert.AreEqual(0f, preview.Values["dlig"]); Assert.IsFalse(preview.Values.ContainsKey("liga"));
        CollectionAssert.AreEquivalent(new[] { "cv11" }, preview.EnabledIndividualTags.ToArray());
        Assert.AreEqual(0f, launch["ss01"]);
    }

    [TestMethod]
    public void ReopenedEditorRestoresEditsButStillResetsToLaunchFeatures()
    {
        var launch = new Dictionary<string, float> { ["cv11"] = 3, ["calt"] = 1 };
        var current = new Dictionary<string, float> { ["cv11"] = 0, ["zero"] = 1, ["calt"] = 1 };
        var editor = new FontFeatureOverrides(launch, current);
        Assert.IsTrue(editor.CanReset);
        CollectionAssert.AreEqual(new[] { "zero" }, editor.EnabledIndividualTags.ToArray());
        editor.SetEnabled("cv11", true);
        Assert.AreEqual(3f, editor.Values["cv11"]);
        editor.Reset();
        Assert.IsFalse(editor.CanReset);
        Assert.IsFalse(editor.Values.ContainsKey("zero"));
        Assert.AreEqual(0f, current["cv11"]);
    }

    [TestMethod]
    public void ReadingTheBinarySwitchDoesNotRewriteDefaultOrMixedLaunchMaps()
    {
        var launch = new Dictionary<string, float>();
        var preview = new FontFeatureOverrides(launch);
        Assert.IsTrue(preview.LigaturesEnabled); Assert.IsEmpty(preview.Values);
        launch = new() { ["liga"] = 0, ["clig"] = 0, ["calt"] = 0 };
        preview = new(launch);
        Assert.IsFalse(preview.LigaturesEnabled); // Optional historical/discretionary ligatures default off.
        launch = new(launch) { ["hlig"] = 1 };
        preview = new(launch);
        Assert.IsTrue(preview.LigaturesEnabled); Assert.IsFalse(preview.CanReset);
        preview.SetEnabled("kern", false); Assert.AreEqual(1f, preview.Values["hlig"]);
        Assert.IsFalse(preview.Values.ContainsKey("dlig"));
    }
}
