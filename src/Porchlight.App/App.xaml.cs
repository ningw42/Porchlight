using Microsoft.UI.Xaml;

namespace Porchlight.App;

public partial class DesktopApp : Application
{
    MainWindow? window;
    public DesktopApp() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.Activate();
        window.Initialize(Environment.GetCommandLineArgs().Skip(1));
    }
}
