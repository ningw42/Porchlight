using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Porchlight.Core;

namespace Porchlight.App;

public sealed partial class MainWindow
{
    // Reuse headers from at most one displaced strip; never cache a space's membership or a viewer.
    TabViewItem[] shownTabs = [], previousStrip = [];

    // Keep the native tab (and keyboard focus) when only the foreground title changes.
    // Neither a display name nor a position in the strip identifies a terminal.
    void ShowTerminalTabs(HerdMachine? machine, IReadOnlyList<LiveTerminal> terminals)
    {
        if (session == null) previousStrip = [];
        var existing = shownTabs.ToDictionary(tab => (Target)tab.Tag);
        foreach (var tab in previousStrip) existing.TryAdd((Target)tab.Tag, tab);
        var next = new TabViewItem[terminals.Count];
        TabViewItem? selected = null;
        for (int i = 0; i < terminals.Count; ++i)
        {
            var terminal = terminals[i];
            var target = new Target(machine!.Id, terminal.Address);
            if (!existing.TryGetValue(target, out var tab))
            {
                tab = new TabViewItem { Tag = target, IsClosable = session?.SupportsKill == true, MaxWidth = 240 };
                AutomationProperties.SetAutomationId(tab, $"TerminalTab-{terminal.Address}");
            }
            next[i] = tab;
            if (!Equals(tab.Header, terminal.Title)) tab.Header = terminal.Title;
            if (AutomationProperties.GetName(tab) != terminal.Label)
            {
                AutomationProperties.SetName(tab, terminal.Label);
                ToolTipService.SetToolTip(tab, terminal.Label);
            }
            if (target == requested) selected = tab;
        }
        // Publish a changed strip once, not one WinUI removal/insertion per terminal.
        if (!shownTabs.AsSpan().SequenceEqual(next))
        {
            previousStrip = session != null ? shownTabs : [];
            TerminalTabs.TabItemsSource = shownTabs = next;
        }
        // A refresh (including the selected terminal's exit) must never attach a neighbor.
        TerminalTabs.SelectedItem = selected;
        if (selected != null && !string.IsNullOrEmpty(AutomationProperties.GetName(PaneHost)))
            AutomationProperties.SetName(PaneHost, AutomationProperties.GetName(selected));
    }

    async void TerminalCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs e)
    {
        // The close glyph may belong to an unselected tab. Do not select it or detach a viewer.
        var tab = e.Tab;
        if (updatingSelection || closing || !tab.IsClosable || !shownTabs.Contains(tab) || tab.Tag is not Target target ||
            session is not { SupportsKill: true } gateway || selectedMachine?.Id != target.MachineId) return;
        tab.IsClosable = false;
        try
        {
            await gateway.KillTerminalAsync(target.Address, target.MachineId);
            // Keep the tab until the gateway's terminal catalog says the program is gone.
        }
        catch (CoreException ex)
        {
            if (closing || session != gateway) return;
            if (shownTabs.Contains(tab))
            {
                viewerNotice = $"Could not confirm closing {target.Address}. {ex.Message}";
                ShowStatus(viewerNotice);
            }
        }
        finally
        {
            // Acknowledgment need not mean exit. If the program survives, don't strand its close button.
            if (!closing && session == gateway) tab.IsClosable = true;
        }
    }
}
