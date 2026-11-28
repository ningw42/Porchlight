using Microsoft.Terminal.Control;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Porchlight.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Porchlight.App;

public sealed partial class MainWindow
{
    TerminalAppearance appearance = new(new());
    (TerminalAppearance Appearance, TerminalSettings Settings)? mountedAppearance;
    Target? requested;
    PaneConnection? connection;
    TermControl? pane;
    bool insidePane;
    long revision;
    Task changingViewer = Task.CompletedTask;

    void ITerminalAppearanceTarget.ApplyAppearance(TerminalAppearance next)
    {
        if (closing) return;
        appearance = next; // New panes start with the latest edits, including edits made during a handoff.
        if (mountedAppearance is not { } mounted || mounted.Appearance == next || pane is not { } control || connection is not { } bridge) return;
        const string failure = "Could not apply the appearance to this terminal.";
        try
        {
            var settings = next.UpdateControl(control, mounted.Settings, mounted.Appearance);
            if (next.Blink != mounted.Appearance.Blink && next.Blink is bool blink) bridge.SetCursorBlink(blink);
            mountedAppearance = (next, settings);
            if (Status.Text == failure) ShowStatus();
        }
        catch (Exception ex) when (TerminalAppearance.IsNativeFailure(ex))
        {
            // Leave the attachment alive. The next edit retries from the last successful appearance.
            ShowStatus(failure);
        }
    }

    // UI-thread handoff. Revoke the old control immediately, but keep its renderer alive
    // until its stream is finished. Only the most recent committed choice may open a pane.
    void RequestViewer(Target? target)
    {
        if (requested == target) return;
        requested = target;
        mountedAppearance = null; // Stop applying edits to the outgoing control immediately.
        long version = ++revision;
        if (connection != null) _ = connection.CloseAsync();
        AutomationProperties.SetName(PaneHost, "");
        if (target != null) ShowStatus();
        changingViewer = ChangeViewer(changingViewer, version, session, target);
    }

    async Task ChangeViewer(Task previous, long version, CoreSession? gateway, Target? target)
    {
        await Task.Yield(); // let selection and its focus change settle before replacing native content
        var selectionFocus = version == revision && target != null && PaneHost.XamlRoot is { } root
            ? FocusManager.GetFocusedElement(root) : null;
        await previous;
        if (version != revision) return;
        try
        {
            await RemovePane();
            if (version != revision || closing || target == null || gateway == null || session != gateway) return;
            var directory = gateway.Directory;
            var machine = directory.Machines.FirstOrDefault(m => m.Id == target.MachineId && m.Available);
            var terminal = machine != null && selectedSpace != null && selectedMachine?.Id == machine.Id ?
                directory.TerminalsIn(machine, selectedSpace).FirstOrDefault(t => t.Address == target.Address) : null;
            if (terminal == null) throw new CoreException("That terminal or space is no longer available. Choose a live terminal.");
            MountPane(gateway, target, terminal.Label, version, selectionFocus);
        }
        catch (Exception ex)
        {
            await RemovePane();
            if (version != revision || closing) return;
            requested = null;
            ClearTerminalSelection(ex is CoreException ? ex.Message : "Could not host the terminal. Check the native helper and Windows App Runtime, then select a terminal again.");
        }
    }

    void MountPane(CoreSession gateway, Target target, string label, long version, object? selectionFocus)
    {
        // TermControl requires native settings at construction. Give the same snapshot to the
        // connection before Start, so its initial blink precedes the program's first DATA.
        var initial = appearance;
        var settings = initial.CreateSettings();
        var bridge = connection = new PaneConnection(gateway, target.MachineId, target.Address, initial);
        var control = pane = new TermControl(settings, null, bridge) { OwningHwnd = (ulong)WinRT.Interop.WindowNative.GetWindowHandle(this) };
        bool Current() => version == revision && connection == bridge && !closing;
        control.KeyBindings(new AppKeyBindings(control));
        control.GotFocus += (_, _) => { if (Current()) { insidePane = true; SendFocus(); } };
        control.LostFocus += (_, _) => { if (Current()) { insidePane = false; SendFocus(); } };
        control.WriteToClipboard += (_, a) => {
            if (!Current()) return;
            try { var data = new DataPackage(); data.SetText(a.Plain); Clipboard.SetContent(data); }
            catch { ShowStatus("Clipboard unavailable."); }
        };
        control.PasteFromClipboard += async (_, a) => {
            if (!Current()) return;
            try {
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.Text)) {
                    var text = await content.GetTextAsync();
                    if (Current()) a.HandleClipboardData(text);
                }
            } catch { if (Current()) ShowStatus("Clipboard unavailable."); }
        };
        PaneHost.Child = control;
        mountedAppearance = (initial, settings);
        _ = WatchPane();

        async Task WatchPane()
        {
            if (await bridge.Ready && Current())
            {
                AutomationProperties.SetName(PaneHost, TerminalTabs.SelectedItem is TabViewItem tab ? AutomationProperties.GetName(tab) : label);
                ShowStatus();
                // Hand focus from the space or tab to its terminal unless the user moved elsewhere while switching.
                var focused = FocusManager.GetFocusedElement(control.XamlRoot);
                if (active && (focused == selectionFocus || focused is null || focused == TerminalTabs.SelectedItem))
                    control.Focus(FocusState.Programmatic);
            }
            var end = await bridge.Completion;
            if (!Current()) return;
            RequestViewer(null);
            ClearTerminalSelection(end.Message);
        }
    }

    void ClearTerminalSelection(string notice)
    {
        viewerNotice = notice;
        updatingSelection = true;
        try { TerminalTabs.SelectedItem = null; }
        finally { updatingSelection = false; }
        SelectionStatus(); Controls();
    }

    async Task RemovePane()
    {
        var bridge = connection; var control = pane;
        mountedAppearance = null; connection = null; pane = null; insidePane = false;
        if (bridge != null) await bridge.CloseAsync();
        // Release any size held on the outgoing surface; mount the next viewer at its settled grid.
        if (sidebarAnimation != null) UpdateSidebar(animate: false);
        try { control?.Close(); }
        finally { PaneHost.Child = null; }
    }

    void SendFocus() => connection?.Focus(!closing && active && insidePane);
}
