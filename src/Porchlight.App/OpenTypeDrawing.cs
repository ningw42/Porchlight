using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.ViewManagement;

namespace Porchlight.App;

// A static drawing behind a real TextBlock. The TextBlock supplies the layout, inherited
// foreground and UIA TextPattern; opacity, unlike Visibility, leaves those intact.
internal sealed class OpenTypeDrawing
{
    static readonly UISettings Settings = new();
    readonly Grid host;
    readonly TextBlock text;
    readonly Action<CanvasTextLayout> configure;
    readonly TextBlock? unavailable;
    readonly DispatcherQueue dispatcher;
    XamlRoot? root;
    double scale;
    bool queued;

    public OpenTypeDrawing(Grid host, TextBlock text, Action<CanvasTextLayout> configure, TextBlock? unavailable = null)
    {
        this.host = host;
        this.text = text;
        this.configure = configure;
        this.unavailable = unavailable;
        dispatcher = host.DispatcherQueue;
        if (unavailable != null) Show(null);
        host.Loaded += Loaded;
        host.Unloaded += Unloaded;
        host.SizeChanged += (_, _) => Invalidate();
        host.ActualThemeChanged += (_, _) => Invalidate();
        text.RegisterPropertyChangedCallback(TextBlock.ForegroundProperty, (_, _) => Invalidate());
        text.RegisterPropertyChangedCallback(TextBlock.FontSizeProperty, (_, _) => Invalidate());
    }

    public static CanvasTypographyFeatureName Tag(string tag)
    {
        if (tag.Length != 4 || tag.Any(ch => ch is < ' ' or > '~'))
            throw new ArgumentException("Expected a four-character OpenType tag.", nameof(tag));
        // DWRITE_MAKE_OPENTYPE_TAG, including names absent from the Win2D enum.
        return (CanvasTypographyFeatureName)(tag[0] | tag[1] << 8 | tag[2] << 16 | tag[3] << 24);
    }

    void Loaded(object sender, RoutedEventArgs args)
    {
        root = host.XamlRoot;
        root.Changed += RootChanged;
        Settings.TextScaleFactorChanged += SettingsChanged;
        Settings.ColorValuesChanged += SettingsChanged;
        Invalidate();
    }

    void Unloaded(object sender, RoutedEventArgs args)
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

    public void Invalidate()
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
            var width = (float)host.ActualWidth;
            var height = (float)host.ActualHeight;
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
            configure(layout);
            // Features/fallback can change line breaks relative to the measuring TextBlock.
            var requiredHeight = Math.Ceiling(layout.LayoutBounds.Height);
            if (host.MinHeight != requiredHeight) host.MinHeight = requiredHeight;
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
        host.Background = bitmap == null ? null : new ImageBrush { ImageSource = bitmap, Stretch = Stretch.Fill };
        text.Opacity = bitmap != null || unavailable != null ? 0 : 1;
        if (unavailable != null)
        {
            unavailable.Visibility = bitmap == null ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetAccessibilityView(text, bitmap == null ? AccessibilityView.Raw : AccessibilityView.Content);
        }
    }
}
