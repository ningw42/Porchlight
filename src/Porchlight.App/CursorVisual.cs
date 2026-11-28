using Microsoft.Graphics.Canvas.Text;
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

    public static CursorFont FromFace(CanvasFontFace? face)
    {
        if (face == null) return Fallback;
        var zero = face.GetGlyphIndices(['0']);
        var advance = zero[0] == 0 ? .5 : face.GetGlyphMetrics(zero, false)[0].AdvanceWidth;
        return new(advance, face.Ascent, face.Descent, face.LineGap, face.UnderlinePosition, face.UnderlineThickness);
    }
}

// Geometry shared by the picker and sample. Mirrors Windows Terminal's AtlasEngine font
// metrics and BackendD2D::_drawCursor: resolve in physical pixels, then lay out in DIPs.
internal sealed class CursorVisual : UserControl
{
    readonly Border ink = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    CursorFont font = CursorFont.Fallback;
    CursorStyle shape;
    XamlRoot? root;
    double scale;

    public CursorStyle Shape
    {
        get => shape;
        set { shape = value; UpdateGeometry(); }
    }
    public double Baseline { get; private set; }

    public CursorVisual(CursorStyle shape)
    {
        Content = ink;
        IsHitTestVisible = false;
        IsTabStop = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        ink.SetBinding(Border.BorderBrushProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
        this.shape = shape;
        Loaded += (_, _) => { root = XamlRoot; root.Changed += RootChanged; UpdateGeometry(); };
        Unloaded += (_, _) => { if (root != null) root.Changed -= RootChanged; root = null; };
        RegisterPropertyChangedCallback(FontSizeProperty, (_, _) => UpdateGeometry());
        UpdateGeometry();
    }

    public void UseFont(CursorFont value) { font = value; UpdateGeometry(); }
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
        var w = Math.Max(1, Round(font.Advance * em));
        var h = Math.Max(1, Round(advanceHeight));
        var baseline = Round(font.Ascent * em + (font.LineGap * em + h - advanceHeight) / 2);
        var thin = Math.Max(1, Round(Math.Max(w / 16, h / 32)));
        Width = w / scale;
        Height = h / scale;
        Baseline = baseline / scale;

        // A left border fills a bar or a box, a top border fills the underline, and four
        // borders outline the cell. All use the segment's current (including selected) foreground.
        ink.Width = Width;
        ink.Height = Height;
        ink.Margin = new Thickness(0);
        switch (shape)
        {
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
