namespace Porchlight.Core.Configuration;

/// Window-local overrides, independent of which family supplies the candidate list.
/// Missing is the font/shaper default, distinct from an explicit zero.
public sealed class FontFeatureOverrides(IReadOnlyDictionary<string, float> launch)
{
    readonly Dictionary<string, float> values = new(launch, StringComparer.Ordinal);

    public IReadOnlyDictionary<string, float> Values => values;
    public bool CanReset => values.Count != launch.Count || launch.Any(entry =>
        !values.TryGetValue(entry.Key, out var value) || value != entry.Value);

    public bool LigaturesEnabled => CodingLigatures.IsEnabled(values);

    public void SetLigaturesEnabled(bool enabled)
    {
        foreach (var tag in CodingLigatures.Tags) values[tag] = enabled ? 1 : 0;
    }

    public IEnumerable<string> EnabledIndividualTags => values
        .Where(p => p.Value != 0 && !CodingLigatures.IsExcludedFromPicker(p.Key)).Select(p => p.Key);

    // Removing a chip explicitly disables it, even if the font enables it by default.
    // Re-adding an imported alternate restores its original selector rather than coercing it to 1.
    public void SetEnabled(string tag, bool enabled)
    {
        if (CodingLigatures.IsExcludedFromPicker(tag)) throw new ArgumentException("Not an individual feature.", nameof(tag));
        if (!enabled) values[tag] = 0;
        else values[tag] = launch.TryGetValue(tag, out var original) && original != 0 ? original : 1;
    }

    public void Reset()
    {
        values.Clear();
        foreach (var (tag, value) in launch) values.Add(tag, value);
    }
}
