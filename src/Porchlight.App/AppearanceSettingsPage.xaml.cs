using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Porchlight.App;

// UI choices for this run; they don't change WinUI or the terminal renderer yet.
internal sealed class AppearancePreferences
{
    public int ColorModeIndex { get; set; }
    public bool TintApp { get; set; }
}

public sealed partial class AppearanceSettingsPage : Page
{
    AppearancePreferences Preferences { get; set; } = null!;

    public AppearanceSettingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e) => Preferences = (AppearancePreferences)e.Parameter;
}
