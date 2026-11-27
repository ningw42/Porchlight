using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics;

namespace Porchlight.App;

public sealed partial class MainWindow
{
    InputNonClientPointerSource? titleBarInput;
    (RectInt32 Sidebar, RectInt32 Footer, RectInt32 Buttons)? lastRegions;
    bool regionCheckQueued;
    bool? maximizedChrome;
    NonClientRegionKind? pressedCaption;

    // At an open sidebar, align the tab fill and clip its 4-DIP overhang at the workspace edge.
    // Clip the strip, not each tab, so tabs away from the sidebar retain both curves.
    readonly RectangleGeometry tabViewportClip = new();
    RectangleGeometry? TabStripClip(bool paneOpen) => paneOpen ? tabViewportClip : null;
    Thickness TabStripMargin(bool paneOpen) => new(paneOpen ? -4 : 0, 0, 0, 0);
    void TabViewportSizeChanged(object sender, SizeChangedEventArgs e) =>
        tabViewportClip.Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height);

    void ConfigureTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        ExtendsContentIntoTitleBar = true;
        // The system offers 32 or 48 DIPs, not the restored strip's 40. Draw the buttons
        // in that row but register their real caption roles for native input and Snap Layouts.
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        CaptionButtons.Visibility = Visibility.Visible;
        titleBarInput = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        titleBarInput.PointerEntered += (_, e) => CaptionVisual(e.RegionKind, pressedCaption == e.RegionKind ? "Pressed" : "PointerOver");
        titleBarInput.PointerExited += (_, e) => CaptionVisual(e.RegionKind, "Normal");
        titleBarInput.PointerPressed += (_, e) => { pressedCaption = e.RegionKind; CaptionVisual(e.RegionKind, "Pressed"); };
        titleBarInput.PointerReleased += (_, e) => { pressedCaption = null; CaptionVisual(e.RegionKind, e.IsPointInRegion ? "PointerOver" : "Normal"); };
        // The system can clear native button regions on restore without changing their XAML bounds.
        titleBarInput.RegionsChanged += (_, _) => QueueRegionCheck();
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidSizeChange || e.DidPresenterChange)
            {
                UpdateChromeState();
                QueueRegionCheck();
            }
        };
        UpdateChromeState();
        AppTitleBar.Loaded += (_, _) => AppTitleBar.XamlRoot.Changed += (_, _) => UpdateTitleBarRegions();
        AppTitleBar.LayoutUpdated += (_, _) => UpdateTitleBarRegions();
    }

    void UpdateChromeState()
    {
        bool maximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
        if (maximizedChrome == maximized) return;
        maximizedChrome = maximized;
        int inset = maximized ? 0 : 8;
        TerminalTabs.Padding = new Thickness(0, inset, 0, 0);
        SidebarToggle.Margin = SidebarToggle.Margin with { Top = inset };
        TabDragArea.MinHeight = 32 + inset;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        var action = maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, action);
        ToolTipService.SetToolTip(MaximizeButton, action);
    }

    void CaptionVisual(NonClientRegionKind kind, string state)
    {
        var button = kind switch {
            NonClientRegionKind.Minimize => MinimizeButton, NonClientRegionKind.Maximize => MaximizeButton,
            NonClientRegionKind.Close => CloseButton, _ => null
        };
        if (button != null) VisualStateManager.GoToState(button, state, true);
    }

    // Keyboard and automation invoke the same system commands as a native caption click,
    // including the cancellable WM_CLOSE path that detaches the active viewer.
    void CaptionButtonClicked(object sender, RoutedEventArgs e)
    {
        const uint WM_SYSCOMMAND = 0x0112;
        const int SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120, SC_CLOSE = 0xF060;
        var button = (Button)sender;
        int command;
        if (button == MinimizeButton) command = SC_MINIMIZE;
        else if (button == MaximizeButton) command = maximizedChrome == true ? SC_RESTORE : SC_MAXIMIZE;
        else command = SC_CLOSE;
        PostMessageW(WinRT.Interop.WindowNative.GetWindowHandle(this), WM_SYSCOMMAND, command, 0);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    void QueueRegionCheck()
    {
        if (closing || regionCheckQueued) return;
        regionCheckQueued = true;
        DispatcherQueue.TryEnqueue(() => { regionCheckQueued = false; UpdateTitleBarRegions(verifyNative: true); });
    }

    void UpdateTitleBarRegions(bool verifyNative = false)
    {
        if (closing || titleBarInput == null || !TabDragArea.IsLoaded) return;
        if (verifyNative && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
        var scale = TabDragArea.XamlRoot.RasterizationScale;
        var sidebar = PixelBounds(SidebarDragArea, scale);
        var footer = PixelBounds(TabDragArea, scale);
        var regions = (sidebar, footer, PixelBounds(CaptionButtons, scale));
        if (!verifyNative && lastRegions is { } previous && previous.Equals(regions)) return;
        Set(NonClientRegionKind.Caption, sidebar.Width > 0 ? [sidebar, footer] : [footer]);
        Set(NonClientRegionKind.Minimize, [PixelBounds(MinimizeButton, scale)]);
        Set(NonClientRegionKind.Maximize, [PixelBounds(MaximizeButton, scale)]);
        Set(NonClientRegionKind.Close, [PixelBounds(CloseButton, scale)]);
        lastRegions = regions;

        void Set(NonClientRegionKind kind, RectInt32[] rects)
        {
            if (!titleBarInput.GetRegionRects(kind).AsSpan().SequenceEqual(rects))
                titleBarInput.SetRegionRects(kind, rects);
        }
    }

    static RectInt32 PixelBounds(FrameworkElement element, double scale)
    {
        var bounds = element.TransformToVisual(null).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        int Px(double dip) => (int)Math.Round(dip * scale);
        int left = Px(bounds.Left), top = Px(bounds.Top);
        return new RectInt32(left, top, Px(bounds.Right) - left, Px(bounds.Bottom) - top);
    }
}
