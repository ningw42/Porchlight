using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.Text;
using Windows.UI.ViewManagement;

namespace Porchlight.App;

// A static drawing behind a real TextBlock. The TextBlock supplies the layout, inherited
// foreground and UIA TextPattern; opacity, unlike Visibility, leaves those intact.
public sealed class FeatureComparison : Grid
{
    // Strong left-to-right marks keep the separator and both RTL runs in off → on order.
    const string Arrow = "\u200e → \u200e";
    readonly TextBlock text = new() {
        TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.Normal,
        FontSize = FontFeatureProbe.SampleFontSize, Language = FontFeatureProbe.SampleLocale
    };
    static readonly UISettings Settings = new();
    readonly TextBlock unavailable = new() { Text = "—", VerticalAlignment = VerticalAlignment.Center };
    readonly DispatcherQueue dispatcher;
    XamlRoot? root;
    double scale;
    bool queued;
    CanvasTypographyFeatureName feature;
    string sample = "";

    public FeatureComparison()
    {
        MaxWidth = 175;
        VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(unavailable, "Feature comparison unavailable");
        Children.Add(text);
        Children.Add(unavailable);
        dispatcher = DispatcherQueue;
        Show(null);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => Invalidate();
        ActualThemeChanged += (_, _) => Invalidate();
        text.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, (_, _) => Invalidate());
        text.RegisterPropertyChangedCallback(TextBlock.FontSizeProperty, (_, _) => Invalidate());
    }

    public void SetSample(string family, string tag, string? sample, bool italic)
    {
        this.sample = sample ?? "";
        if (sample == null)
        {
            text.Text = "";
            Invalidate(); // No demonstrated change: show the dash, including for a recycled row.
            return;
        }
        feature = OpenTypeFeatureTags.ToFeatureName(tag);
        text.FontFamily = new FontFamily(family);
        text.FontStyle = italic ? FontStyle.Italic : FontStyle.Normal;
        text.Text = sample + Arrow + sample;
        AutomationProperties.SetName(text, $"{tag} off then on: {sample}{(italic ? " (italic)" : "")}");
        Invalidate();
    }

    void Compare(CanvasTextLayout layout)
    {
        var off = new CanvasTypography();
        var on = new CanvasTypography();
        off.AddFeature(feature, 0);
        on.AddFeature(feature, 1);
        layout.SetFontFamily(0, text.Text.Length, text.FontFamily.Source);
        var middle = sample.Length;
        layout.SetTypography(0, sample.Length, off);
        layout.SetFontFamily(middle, Arrow.Length, "Segoe UI");
        layout.SetFontStyle(middle, Arrow.Length, FontStyle.Normal);
        layout.SetTypography(middle + Arrow.Length, sample.Length, on);
    }

    void OnLoaded(object sender, RoutedEventArgs args)
    {
        root = XamlRoot;
        root.Changed += RootChanged;
        Settings.TextScaleFactorChanged += SettingsChanged;
        Settings.ColorValuesChanged += SettingsChanged;
        Invalidate();
    }

    void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (root != null) root.Changed -= RootChanged;
        root = null;
        Settings.TextScaleFactorChanged -= SettingsChanged;
        Settings.ColorValuesChanged -= SettingsChanged;
        Show(null);
    }

    void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (sender.RasterizationScale != scale) Invalidate();
    }

    void SettingsChanged(UISettings sender, object args) => dispatcher.TryEnqueue(Invalidate);

    void Invalidate()
    {
        if (!queued && root != null) queued = dispatcher.TryEnqueue(Draw);
    }

    void Draw()
    {
        queued = false;
        if (root == null) return;
        try
        {
            scale = root.RasterizationScale;
            var width = (float)ActualWidth;
            var height = (float)ActualHeight;
            if (width <= 0 || height <= 0 || text.Text.Length == 0 || text.Foreground is not SolidColorBrush brush)
            {
                Show(null);
                return;
            }
            // One small bitmap per loaded specimen, no live CanvasControls or per-row GPU devices.
            var device = CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true);
            using var format = new CanvasTextFormat {
                LocaleName = text.Language, FontStyle = text.FontStyle, FontWeight = text.FontWeight,
                FontSize = (float)(text.FontSize * (text.IsTextScaleFactorEnabled ? Settings.TextScaleFactor : 1)),
                WordWrapping = CanvasWordWrapping.EmergencyBreak, Options = CanvasDrawTextOptions.EnableColorFont
            };
            using var layout = new CanvasTextLayout(device, text.Text, format, width, 0);
            Compare(layout);
            // Features/fallback can change line breaks relative to the measuring TextBlock.
            var requiredHeight = Math.Ceiling(layout.LayoutBounds.Height);
            if (MinHeight != requiredHeight) MinHeight = requiredHeight;
            using var target = new CanvasRenderTarget(device, width, height, (float)(96 * scale));
            using (var ds = target.CreateDrawingSession())
            {
                ds.Clear(Colors.Transparent);
                var color = brush.Color;
                color.A = (byte)Math.Round(color.A * brush.Opacity);
                ds.DrawTextLayout(layout, 0, 0, color);
            }
            var pixels = target.SizeInPixels;
            var bitmap = new WriteableBitmap((int)pixels.Width, (int)pixels.Height);
            target.GetPixelBytes(bitmap.PixelBuffer);
            bitmap.Invalidate();
            Show(bitmap);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Fonts/devices may disappear. A later real invalidation will retry; don't spin
            // on a lost device or leave the previous family's drawing in a recycled row.
            System.Diagnostics.Debug.WriteLine($"OpenType preview unavailable: {ex.Message}");
            Show(null);
        }
    }

    void Show(WriteableBitmap? bitmap)
    {
        Background = bitmap == null ? null : new ImageBrush { ImageSource = bitmap, Stretch = Stretch.Fill };
        text.Opacity = 0;
        unavailable.Visibility = bitmap == null ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetAccessibilityView(text, bitmap == null ? AccessibilityView.Raw : AccessibilityView.Content);
    }
}
