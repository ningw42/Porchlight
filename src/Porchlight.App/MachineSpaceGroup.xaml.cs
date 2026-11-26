using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Porchlight.Core;

namespace Porchlight.App;

public sealed partial class MachineSpaceGroup : UserControl
{
    internal HerdMachine Machine { get; private set; } = null!;
    internal event Action<MachineSpaceGroup>? SpaceChanged;
    IReadOnlyList<TerminalSpace> shownSpaces = [];
    internal TerminalSpace? SelectedSpace => Spaces.SelectedItem as TerminalSpace;

    public MachineSpaceGroup() => InitializeComponent();

    internal void Display(HerdMachine machine, IReadOnlyList<TerminalSpace> spaces, TerminalSpace? selected)
    {
        if (Machine != machine)
        {
            Machine = machine;
            MachineName.Text = machine.DisplayName;
            StateLabel.Text = machine.State.Replace('_', ' ');
            OnlineIcon.Visibility = machine.Available ? Visibility.Visible : Visibility.Collapsed;
            OfflineIcon.Visibility = machine.Available ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(Group, $"{machine.DisplayName}, {StateLabel.Text}");
            AutomationProperties.SetAutomationId(Group, $"MachineGroup-{machine.Id}");
            AutomationProperties.SetName(Spaces, $"Spaces on {machine.DisplayName}");
            AutomationProperties.SetAutomationId(Spaces, $"Spaces-{machine.Id}");
        }
        if (!shownSpaces.SequenceEqual(spaces)) Spaces.ItemsSource = shownSpaces = spaces;
        Spaces.SelectedItem = shownSpaces.FirstOrDefault(s => s == selected);
        Spaces.Visibility = spaces.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NoSpaces.Visibility = spaces.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SpaceSelectionChanged(object sender, SelectionChangedEventArgs e) => SpaceChanged?.Invoke(this);
}
