using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Porchlight.App;

public sealed partial class SettingsWindow : Window
{
    readonly string launchFace;

    internal SettingsWindow(Window owner, string launchFace)
    {
        this.launchFace = launchFace;
        InitializeComponent();
        Categories.SelectedItem = TerminalCategory;

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
        if (args.SelectedItemContainer == TerminalCategory)
            CategoryPage.Navigate(typeof(TerminalSettingsPage), launchFace);
    }
}
