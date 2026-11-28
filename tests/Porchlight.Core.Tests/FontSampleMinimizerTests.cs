using Microsoft.VisualStudio.TestTools.UnitTesting;
using Porchlight.Core.Configuration;

namespace Porchlight.Core.Tests;

[TestClass]
public class FontSampleMinimizerTests
{
    [TestMethod]
    public void SingleGlyphSubstitutionNeedsOnlyTheChangedLetter()
    {
        Assert.AreEqual("r", FontSampleMinimizer.ShortestVerified("cursive", s => s.Contains('r'), default));
    }

    [TestMethod]
    public void KerningNeedsThePairAndContextualSubstitutionCanNeedBothNeighbors()
    {
        Assert.AreEqual("AV", FontSampleMinimizer.ShortestVerified(" AV To", s => s.Contains("AV"), default));
        // Neither one-character trim of the specimen works. Greedy trimming would miss this.
        Assert.AreEqual("(H)", FontSampleMinimizer.ShortestVerified("x(H)y", s => s is "(H)" or "x(H)y", default));
    }

    [TestMethod]
    public void DoesNotSplitCombiningSequencesSurrogatePairsOrJoinedGraphemes()
    {
        const string mark = "q\u0323\u0301", joined = "\U0001F469\u200D\U0001F4BB";
        var tried = new List<string>();
        var shortest = FontSampleMinimizer.ShortestVerified(mark + joined + "x", s =>
        {
            tried.Add(s);
            return s == mark + joined;
        }, default);
        Assert.AreEqual(mark + joined, shortest);
        CollectionAssert.AreEqual(new[] { mark, joined, "x", mark + joined }, tried);
    }

    [TestMethod]
    public void KeepsVerifiedWholeContextIfNoShorterSliceWorksAndSkipsDuplicateTrials()
    {
        var tried = new List<string>();
        Assert.AreEqual("===", FontSampleMinimizer.ShortestVerified("===", s => { tried.Add(s); return false; }, default));
        CollectionAssert.AreEqual(new[] { "=", "==" }, tried);
    }

    [TestMethod]
    public void CancelsBetweenTrials()
    {
        using var cts = new CancellationTokenSource();
        int trials = 0;
        Assert.ThrowsExactly<OperationCanceledException>(() => FontSampleMinimizer.ShortestVerified("abcdef", s =>
        {
            trials++;
            cts.Cancel();
            return false;
        }, cts.Token));
        Assert.AreEqual(1, trials);
    }
}
