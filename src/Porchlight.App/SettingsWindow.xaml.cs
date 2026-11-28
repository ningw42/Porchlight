using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Porchlight.Core.Configuration;
using Windows.Graphics;

namespace Porchlight.App;

public sealed partial class SettingsWindow : Window
{
    readonly TerminalFont launchFont;

    internal SettingsWindow(Window owner, TerminalFont launchFont)
    {
        this.launchFont = launchFont;
        InitializeComponent();
        // Give the window a focus target before the page chooses its first editor.
        CategoryPage.Loaded += (_, _) => TerminalCategory.Focus(FocusState.Programmatic);
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
            CategoryPage.Navigate(typeof(TerminalSettingsPage), launchFont);
    }
}
