using System.Globalization;

namespace Porchlight.Core.Configuration;

public static class FontSampleMinimizer
{
    /// Given a short specimen already verified to change, find its shortest contiguous
    /// witness (leftmost on ties). Shape each trial on its own: a shaping rule need not
    /// keep working when a character is removed, or even when one is added.
    public static string ShortestVerified(string specimen, Func<string, bool> changesInIsolation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var starts = StringInfo.ParseCombiningCharacters(specimen);
        var tried = new HashSet<string>(StringComparer.Ordinal);
        for (int length = 1; length < starts.Length; length++)
        {
            for (int i = 0; i + length <= starts.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int end = i + length == starts.Length ? specimen.Length : starts[i + length];
                var trial = specimen[starts[i]..end];
                if (tried.Add(trial) && changesInIsolation(trial)) return trial;
            }
        }
        return specimen;
    }
}
