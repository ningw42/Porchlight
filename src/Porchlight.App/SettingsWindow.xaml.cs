using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Porchlight.Core.Configuration;
using Windows.Graphics;

namespace Porchlight.App;

public sealed partial class SettingsWindow : Window
{
    internal sealed record EditorContext(TerminalFont LaunchFont, TerminalAppearance Initial,
        ITerminalAppearanceTarget Live, ulong Hwnd);
    readonly MainWindow owner;

    internal SettingsWindow(MainWindow owner)
    {
        this.owner = owner;
        InitializeComponent();
        // Give the window a focus target before the page chooses its first editor.
        CategoryPage.Loaded += (_, _) => AppearanceCategory.Focus(FocusState.Programmatic);
        Categories.SelectedItem = AppearanceCategory;
        // Stop the renderer while the Settings HWND and its XAML island are still valid.
        Closed += (_, _) => (CategoryPage.Content as TerminalSettingsPage)?.ClosePreview();

        // Size in DIPs on the viewer's monitor; AppWindow coordinates are physical pixels.
        var parent = owner.AppWindow;
        var area = DisplayArea.GetFromWindowId(parent.Id, DisplayAreaFallback.Nearest).WorkArea;
        var scale = owner.Content.XamlRoot.RasterizationScale;
        int width = Math.Min((int)(920 * scale), area.Width), height = Math.Min((int)(560 * scale), area.Height);
        int x = Math.Clamp(parent.Position.X + (parent.Size.Width - width) / 2, area.X, area.X + area.Width - width);
        int y = Math.Clamp(parent.Position.Y + (parent.Size.Height - height) / 2, area.Y, area.Y + area.Height - height);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    void CategorySelected(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer == AppearanceCategory)
            CategoryPage.Navigate(typeof(AppearanceSettingsPage), owner.AppearancePreferences);
        else if (args.SelectedItemContainer == TerminalCategory)
        {
            var hwnd = (ulong)WinRT.Interop.WindowNative.GetWindowHandle(this);
            CategoryPage.Navigate(typeof(TerminalSettingsPage), owner.CreateTerminalEditorContext(hwnd));
        }
    }
}
