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
    Target? requested;
    PaneConnection? connection;
    TermControl? pane;
    bool insidePane;
    long revision;
    Task changingViewer = Task.CompletedTask;

    // UI-thread handoff. Revoke the old control immediately, but keep its renderer alive
    // until its stream is finished. Only the most recent committed choice may open a pane.
    void RequestViewer(Target? target)
    {
        if (requested == target) return;
        requested = target;
        long version = ++revision;
        if (connection != null) _ = connection.CloseAsync();
        AutomationProperties.SetName(PaneHost, "");
        if (target != null) ShowStatus($"Attaching to {target.Address}…");
        changingViewer = ChangeViewer(changingViewer, version, session, target);
    }

    async Task ChangeViewer(Task previous, long version, CoreSession? gateway, Target? target)
    {
        await Task.Yield(); // leave tab selection dispatch before replacing native content
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
            MountPane(gateway, target, terminal.Label, version);
        }
        catch (Exception ex)
        {
            await RemovePane();
            if (version != revision || closing) return;
            requested = null;
            ClearTerminalSelection(ex is CoreException ? ex.Message : "Could not host the terminal. Check the native helper and Windows App Runtime, then select a terminal again.");
        }
    }

    void MountPane(CoreSession gateway, Target target, string label, long version)
    {
        var font = options.Font;
        var settings = new TerminalSettings(options.Scheme) {
            FontFace = font.Face, FontSize = font.Size, FontWeight = new() { Weight = font.Weight },
            CellHeight = font.CellHeight, CellWidth = font.CellWidth,
            EnableBuiltinGlyphs = font.BuiltinGlyphs, EnableColorGlyphs = font.ColorGlyphs,
            FontFeatures = NativeFeatures.Create(font.Features), FontAxes = NativeFeatures.Create(font.Axes)
        };
        var bridge = connection = new PaneConnection(gateway, target.MachineId, target.Address);
        var control = pane = new TermControl(settings, settings, bridge) { OwningHwnd = (ulong)WinRT.Interop.WindowNative.GetWindowHandle(this) };
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
        _ = WatchPane();

        async Task WatchPane()
        {
            if (await bridge.Ready && Current())
            {
                AutomationProperties.SetName(PaneHost, TerminalTabs.SelectedItem is TabViewItem tab ? AutomationProperties.GetName(tab) : label);
                ShowStatus();
                if (active && FocusManager.GetFocusedElement(control.XamlRoot) is null or TabViewItem)
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
        connection = null; pane = null; insidePane = false;
        if (bridge != null) await bridge.CloseAsync();
        try { control?.Close(); }
        finally { PaneHost.Child = null; }
    }

    void SendFocus() => connection?.Focus(!closing && active && insidePane);
}
