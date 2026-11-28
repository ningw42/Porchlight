using Microsoft.Graphics.Canvas.Text;
using Microsoft.Terminal.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Porchlight.Core.Configuration;
using System.Globalization;
using System.Text;
using Windows.UI.Text;

namespace Porchlight.App;

public sealed partial class TerminalAppearancePreview : UserControl
{
    readonly OpenTypeDrawing drawing;
    readonly List<(int Start, int Length, string Family)> fallbacks = [];
    string? resolvedFace;
    string primary = "Consolas";
    readonly CursorVisual cursor = new(CursorStyle.Bar);
    internal event Action<CursorFont>? CursorFontChanged;
    Dictionary<string, float> features = [];

    public TerminalAppearancePreview()
    {
        InitializeComponent();
        CursorLayer.Children.Add(cursor);
        drawing = new(DrawingHost, TerminalPreviewSample, Shape);
        RegisterPropertyChangedCallback(FontFamilyProperty, (_, _) => drawing.Invalidate());
    }

    public void Update(string face, IReadOnlyDictionary<string, float> features)
    {
        if (FontFamily.Source != face) FontFamily = new FontFamily(face);
        if (this.features.Count == features.Count && features.All(kv =>
                this.features.TryGetValue(kv.Key, out var value) && value == kv.Value)) return;
        this.features = new(features, StringComparer.Ordinal);
        drawing.Invalidate();
    }

    public void SetCursorShape(CursorStyle shape)
    {
        cursor.Shape = shape;
        AutomationProperties.SetHelpText(TerminalPreviewSample, $"{shape} cursor");
    }

    void Shape(CanvasTextLayout layout)
    {
        var face = FontFamily.Source;
        if (resolvedFace != face)
        {
            ResolveFamilies(face);
            resolvedFace = face;
        }
        var length = TerminalPreviewSample.Text.Length;
        layout.SetFontFamily(0, length, primary);
        foreach (var run in fallbacks) layout.SetFontFamily(run.Start, run.Length, run.Family);
        if (features.Count != 0)
        {
            var typography = new CanvasTypography();
            // The terminal seeds these standard shaping features when there are explicit overrides.
            foreach (var tag in CodingLigatures.DefaultOnTags)
                if (!features.ContainsKey(tag)) typography.AddFeature(OpenTypeDrawing.Tag(tag), 1);
            foreach (var (tag, value) in features)
                typography.AddFeature(OpenTypeDrawing.Tag(tag), uint.CreateSaturating(value));
            layout.SetTypography(0, length, typography);
        }

        // The last space reserves the cursor cell. Align the primary face's cell to the shaped line's baseline.
        layout.GetCaretPosition(length - 1, false, out var region);
        cursor.FontSize = layout.DefaultFontSize;
        var scale = XamlRoot.RasterizationScale;
        Canvas.SetLeft(cursor, Math.Round(region.LayoutBounds.X * scale) / scale);
        Canvas.SetTop(cursor, Math.Round((region.LayoutBounds.Y + layout.LineMetrics[^1].Baseline) * scale) / scale - cursor.Baseline);
    }

    void ResolveFamilies(string face)
    {
        // Win2D takes one family, not a Terminal FontFace list. Prefer the first installed
        // face covering each grapheme; let DirectWrite do system fallback for the rest.
        var system = CanvasFontSet.GetSystemFontSet();
        var fonts = FontFamilyList.Parse(face).Select(name => (Name: name, Face: system.GetMatchingFonts(
            name, FontWeights.Normal, FontStretch.Normal, FontStyle.Normal).Fonts.FirstOrDefault()))
            .Where(font => font.Face != null).ToArray();
        primary = fonts.FirstOrDefault().Name ?? "Consolas";
        var primaryFace = fonts.FirstOrDefault().Face ?? system.GetMatchingFonts(
            primary, FontWeights.Normal, FontStretch.Normal, FontStyle.Normal).Fonts.FirstOrDefault();
        var cursorFont = CursorFont.FromFace(primaryFace);
        cursor.UseFont(cursorFont);
        CursorFontChanged?.Invoke(cursorFont);
        fallbacks.Clear();
        var text = TerminalPreviewSample.Text;
        var elements = StringInfo.GetTextElementEnumerator(text);
        int start = 0;
        string current = primary;
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var name = fonts.FirstOrDefault(font => element.EnumerateRunes()
                .All(rune => font.Face!.HasCharacter((uint)rune.Value))).Name ?? primary;
            if (current == name) continue;
            EndRun(elements.ElementIndex);
            current = name;
            start = elements.ElementIndex;
        }
        EndRun(text.Length);

        void EndRun(int end)
        {
            if (current != primary) fallbacks.Add((start, end - start, current));
        }
    }
}
