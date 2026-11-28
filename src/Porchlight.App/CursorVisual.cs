using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Microsoft.Terminal.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace Porchlight.App;

// Design metrics in ems. AtlasEngine sizes its default cell from the advance of '0',
// not from a bar, underscore or box character. A missing '0' means half an em.
internal readonly record struct CursorFont(double Advance, double Ascent, double Descent, double LineGap,
    double UnderlinePosition, double UnderlineThickness)
{
    public static CursorFont Fallback => new(.5, .8, .2, 0, -.1, .05);

    public static CursorFont ForFamily(string family, ushort weight)
    {
        var fonts = CanvasFontSet.GetSystemFontSet();
        using var matches = fonts.GetMatchingFonts(family.Length == 0 ? "Consolas" : family,
            new Windows.UI.Text.FontWeight { Weight = weight }, Windows.UI.Text.FontStretch.Normal, Windows.UI.Text.FontStyle.Normal);
        if (matches.Fonts.FirstOrDefault() is not { } face) return Fallback;
        var zero = face.GetGlyphIndices(['0']);
        var advance = zero[0] == 0 ? .5 : face.GetGlyphMetrics(zero, false)[0].AdvanceWidth;
        return new(advance, face.Ascent, face.Descent, face.LineGap, face.UnderlinePosition, face.UnderlineThickness);
    }
}

// Picker icons only. The sample itself uses the terminal's own cursor.
internal sealed class CursorVisual : UserControl
{
    readonly Border ink = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    CursorFont font = CursorFont.Fallback;
    Size? measuredCell;
    XamlRoot? root;
    double scale;

    public CursorStyle Shape { get; }

    public CursorVisual(CursorStyle shape)
    {
        Content = ink;
        IsHitTestVisible = false;
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        ink.SetBinding(Border.BorderBrushProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
        Shape = shape;
        Loaded += (_, _) => { root = XamlRoot; root.Changed += RootChanged; UpdateGeometry(); };
        Unloaded += (_, _) => { if (root != null) root.Changed -= RootChanged; root = null; };
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => UpdateGeometry());
        UpdateGeometry();
    }

    public void UseFont(CursorFont value) { font = value; UpdateGeometry(); }
    // Cell dimensions in ems come from the actual terminal, not a second CSS-length parser.
    public void UseCell(double width, double height)
    {
        var cell = new Size(width, height);
        if (measuredCell == cell) return;
        measuredCell = cell;
        UpdateGeometry();
    }

    void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale != scale) UpdateGeometry();
    }

    void UpdateGeometry()
    {
        static double Round(double n) => Math.Round(n, MidpointRounding.AwayFromZero);
        scale = XamlRoot?.RasterizationScale ?? 1;
        var em = FontSize * scale;
        var advanceHeight = (font.Ascent + font.Descent + font.LineGap) * em;
        var w = Math.Max(1, Round((measuredCell?.Width ?? font.Advance) * em));
        var h = Math.Max(1, Round(measuredCell is { } cell ? cell.Height * em : advanceHeight));
        var baseline = Round(font.Ascent * em + (font.LineGap * em + h - advanceHeight) / 2);
        var thin = Math.Max(1, Round(Math.Max(w / 16, h / 32)));
        Width = w / scale;
        Height = h / scale;

        // A left border fills a bar or a box, a top border fills the underline, and four
        // borders outline the cell. All use the segment's current (including selected) foreground.
        ink.Width = Width;
        ink.Height = Height;
        ink.Margin = new Thickness(0);
        switch (Shape)
        {
            case CursorStyle.Vintage:
                var vintageHeight = Math.Clamp(Round(h * TerminalSettings.DefaultCursorHeight / 100), 1, h);
                ink.Margin = new Thickness(0, (h - vintageHeight) / scale, 0, 0);
                ink.Height = vintageHeight / scale;
                ink.BorderThickness = new Thickness(Width, 0, 0, 0);
                break;
            case CursorStyle.Bar:
                ink.BorderThickness = new Thickness(thin / scale, 0, 0, 0);
                break;
            case CursorStyle.Underscore:
                var thickness = Math.Clamp(Round(font.UnderlineThickness * em), 1, h);
                var top = Math.Clamp(Round(baseline - font.UnderlinePosition * em), 0, h - thickness);
                ink.Margin = new Thickness(0, top / scale, 0, 0);
                ink.Height = thickness / scale;
                ink.BorderThickness = new Thickness(0, thickness / scale, 0, 0);
                break;
            case CursorStyle.FilledBox:
                ink.BorderThickness = new Thickness(Width, 0, 0, 0);
                break;
            case CursorStyle.EmptyBox:
                ink.BorderThickness = new Thickness(thin / scale);
                break;
        }
    }
}
