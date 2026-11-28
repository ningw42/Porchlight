using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;
using Porchlight.Core.Configuration;

namespace Porchlight.App;

public sealed class FeatureComparison : Grid
{
    // Strong left-to-right marks keep the separator and both RTL runs in off → on order.
    const string Arrow = "\u200e → \u200e";
    readonly TextBlock text = new() {
        TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Normal,
        FontSize = FontFeatureProbe.SampleFontSize, Language = FontFeatureProbe.SampleLocale
    };
    readonly OpenTypeDrawing drawing;
    IReadOnlyList<string> tags = [];
    string sample = "";

    public FeatureComparison()
    {
        MaxWidth = 175;
        VerticalAlignment = VerticalAlignment.Center;
        var unavailable = new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(unavailable, "Feature comparison unavailable");
        Children.Add(text);
        Children.Add(unavailable);
        drawing = new(this, text, Compare, unavailable);
    }

    public void SetSample(string family, string tag, string? sample, bool italic) =>
        Configure(family, tag, [tag], sample, italic);

    public void SetLigatureSample(string family, string sample) =>
        Configure(family, "Coding ligatures", CodingLigatures.Tags, sample, false);

    void Configure(string family, string label, IReadOnlyList<string> tags, string? sample, bool italic)
    {
        this.tags = tags;
        this.sample = sample ?? "";
        if (sample == null)
        {
            text.Text = "";
            drawing.Invalidate(); // No demonstrated change: show the dash, including for a recycled row.
            return;
        }
        text.FontFamily = new FontFamily(family);
        text.FontStyle = italic ? FontStyle.Italic : FontStyle.Normal;
        text.Text = sample + Arrow + sample;
        AutomationProperties.SetName(text, $"{label} off then on: {sample}{(italic ? " (italic)" : "")}");
        drawing.Invalidate();
    }

    void Compare(CanvasTextLayout layout)
    {
        var off = new CanvasTypography();
        var on = new CanvasTypography();
        foreach (var tag in tags)
        {
            var name = OpenTypeDrawing.Tag(tag);
            off.AddFeature(name, 0);
            on.AddFeature(name, 1);
        }
        layout.SetFontFamily(0, text.Text.Length, text.FontFamily.Source);
        var middle = sample.Length;
        layout.SetTypography(0, sample.Length, off);
        layout.SetFontFamily(middle, Arrow.Length, "Segoe UI");
        layout.SetFontStyle(middle, Arrow.Length, FontStyle.Normal);
        layout.SetTypography(middle + Arrow.Length, sample.Length, on);
    }
}
