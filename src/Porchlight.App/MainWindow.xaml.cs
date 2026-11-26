using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Porchlight.Core;
using Windows.Graphics;

namespace Porchlight.App;

public sealed partial class MainWindow : Window
{
    sealed record Target(string MachineId, string Address);
    readonly CancellationTokenSource lifetime = new();
    LaunchOptions options = new();
    CoreEndpoint? endpoint;
    CoreSession? session;
    HerdMachine? selectedMachine;
    TerminalSpace? selectedSpace;
    Target? suggested;
    IReadOnlyList<LiveTerminal> shownTerminals = [];
    Task? connecting;
    bool configured, busy, closing, mayClose, active, updatingPickers, userChoseScope;
    string? viewerNotice;

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1440, 960));
        Activated += (_, e) => { active = e.WindowActivationState != WindowActivationState.Deactivated; SendFocus(); };
        AppWindow.Closing += async (_, e) =>
        {
            if (mayClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true; Controls(); lifetime.Cancel();
            RequestViewer(null);
            await Task.Yield(); // leave this synchronous cancellable event before calling Close
            var stop = session?.DisposeAsync().AsTask();
            if (connecting != null) await connecting;
            await changingViewer;
            if (stop != null) await stop;
            mayClose = true; Close();
        };
        Closed += (_, _) => lifetime.Dispose();
        Controls();
    }

    internal void Initialize(IEnumerable<string> args)
    {
        try
        {
            options = LaunchOptions.Parse(args);
            if (options.Scheme is { } scheme)
            {
                Title = $"Porchlight · {scheme.Name}";
                var bg = scheme.Background;
                PaneHost.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, bg.R, bg.G, bg.B));
            }
            else if (options.ImportedFont) Title = "Porchlight · Windows Terminal font";
            configured = true;
            Connect();
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            Status.Text = $"Launch settings: {ex.Message} Correct the options and reopen Porchlight.";
        }
    }
    static string SetupMessage(Exception ex) => ex is CoreException or FormatException or ArgumentException or IOException
        ? ex.Message : "Cannot connect to the configured gateway.";

    void Connect()
    {
        if (!configured || busy || closing || session != null) return;
        try
        {
            endpoint ??= options.Pipe == null ? CoreEndpoint.Discover() : CoreEndpoint.Select(options.Pipe);
            connecting = ConnectTo(endpoint);
        }
        catch (Exception ex) { Status.Text = SetupMessage(ex) + " Use the sidebar refresh to retry."; Controls(); }
    }

    async Task ConnectTo(CoreEndpoint endpoint)
    {
        busy = true; Controls();
        Status.Text = "Connecting to the gateway…";
        try
        {
            var s = await CoreSession.ConnectAsync(endpoint, lifetime.Token);
            if (closing) { await s.DisposeAsync(); return; }
            session = s;
            CoreLabel.Text = $"HerdR {s.Identity.Version} · core {s.Identity.Id}";
            s.CatalogChanged += () => DispatcherQueue.TryEnqueue(() => RefreshList(s));
            RefreshList(s, initial: true);
            _ = WatchGateway(s);
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex) { Status.Text = SetupMessage(ex) + " Use the sidebar refresh to retry."; }
        finally { busy = false; Controls(); }
    }

    void LayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var mode = e.NewSize.Width < 800 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        if (Workspace.DisplayMode == mode) return;
        Workspace.DisplayMode = mode;
        if (mode == SplitViewDisplayMode.Overlay) Workspace.IsPaneOpen = false;
    }

    void RefreshList(CoreSession s, bool initial = false)
    {
        if (session != s || closing) return;
        var catalog = s.Directory;
        var machine = catalog.Machines.FirstOrDefault(m => m.Id == selectedMachine?.Id);
        if (initial)
        {
            var address = options.Select;
            var alias = options.Machine ?? (address?.Contains('/') == true ? address.Split('/')[0] : null);
            machine = catalog.Machines.FirstOrDefault(m => alias == null ? m.IsLocal : m.Name == alias);
            suggested = machine != null && address != null ? new(machine.Id, address) : null;
            userChoseScope = false; viewerNotice = null;
            options.Machine = options.Select = null;
        }
        var spaces = machine == null ? [] : catalog.SpacesFor(machine);
        var space = spaces.FirstOrDefault(x => selectedSpace != null && x.Id == selectedSpace.Id);
        var locate = requested ?? suggested;
        if (locate != null && machine != null && locate.MachineId == machine.Id)
        {
            // Never let a reused alias, or a late metadata reply, select a different viewer.
            var t = catalog.Terminals.FirstOrDefault(t => t.Address == locate.Address && t.MachineAlias == (machine.IsLocal ? null : machine.Name));
            if (t != null) space = t.Space;
        }
        if (!userChoseScope) space ??= spaces.FirstOrDefault();
        ShowScope(catalog, machine, space);
    }

    void ShowScope(MachineDirectory catalog, HerdMachine? machine, TerminalSpace? space)
    {
        updatingPickers = true;
        try
        {
            selectedMachine = machine; selectedSpace = space;
            ScopeLabel.Text = space == null || machine == null ? "No space selected" : $"{machine.DisplayName} / {space.DisplayName}";
            ShowMachineGroups(catalog);
            var items = machine != null && space != null ? catalog.TerminalsIn(machine, space) : [];
            if (!shownTerminals.SequenceEqual(items)) Picker.ItemsSource = shownTerminals = items;
            // A launch hint or an automatic selection made by the ComboBox isn't user intent.
            Picker.SelectedItem = requested is { } pick && pick.MachineId == machine?.Id ?
                shownTerminals.FirstOrDefault(t => t.Address == pick.Address) : null;
        }
        finally { updatingPickers = false; }
        SelectionStatus(); Controls();
    }

    void ShowMachineGroups(MachineDirectory catalog)
    {
        // Reuse each Expander by core ID so directory updates don't reset expansion or focus.
        var machines = catalog.Machines;
        var rows = MachineGroups.Children;
        var existing = rows.Cast<MachineSpaceGroup>().ToDictionary(g => g.Machine.Id);
        for (int i = 0; i < machines.Count; ++i)
        {
            var m = machines[i];
            if (!existing.TryGetValue(m.Id, out var group))
            {
                group = new MachineSpaceGroup();
                group.SpaceChanged += SpaceChanged;
                rows.Insert(i, group);
            }
            else if (rows[i] != group)
            {
                rows.Remove(group);
                rows.Insert(i, group);
            }
            group.Display(m, catalog.SpacesFor(m), m.Id == selectedMachine?.Id ? selectedSpace : null);
        }
        while (rows.Count > machines.Count) rows.RemoveAt(rows.Count - 1);
        EmptyMachines.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SpaceChanged(MachineSpaceGroup group)
    {
        if (updatingPickers || session == null || busy || closing) return;
        var catalog = session.Directory;
        var machine = catalog.Machines.FirstOrDefault(m => m.Id == group.Machine.Id);
        var space = machine != null && group.SelectedSpace is { } pick ? catalog.SpacesFor(machine).FirstOrDefault(s => s.Id == pick.Id) : null;
        suggested = null; viewerNotice = null; userChoseScope = true;
        RequestViewer(null); // release the old pane, not the catalog connection or the program
        ShowScope(catalog, machine, space);
    }

    void RefreshMachines_Click(object sender, RoutedEventArgs e)
    {
        if (session == null) Connect(); else session.RefreshMachines();
    }

    void TerminalSelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPickers || closing || busy || session == null || selectedMachine is not { Available: true } machine ||
            selectedSpace == null || Picker.SelectedItem is not LiveTerminal terminal) return;
        suggested = null; viewerNotice = null; userChoseScope = true;
        RequestViewer(new(machine.Id, terminal.Address));
    }

    void SelectionStatus()
    {
        if (requested != null || session == null || closing) return;
        var hint = suggested is { } locate && locate.MachineId == selectedMachine?.Id ?
            shownTerminals.FirstOrDefault(t => t.Address == locate.Address) : null;
        Status.Text = viewerNotice ?? (selectedMachine switch {
            { Available: false } m => $"{m.DisplayName}: {m.State.Replace('_', ' ')}. Check the connection in HerdR, then refresh the machines.",
            _ when selectedSpace == null => "Select a space in the sidebar.",
            _ when hint != null => $"Choose {hint.Label} in Live terminal to view it.",
            _ when Picker.Items.Count == 0 => "No live terminals in this space.",
            _ => "Choose a live terminal to view it."
        });
    }
    void Controls()
    {
        if (Picker == null || RefreshMachinesButton == null) return;
        bool idle = !busy && !closing;
        MachineBrowser.IsEnabled = idle && session != null;
        RefreshMachinesButton.IsEnabled = idle && configured && (session == null || session.SupportsMachineList);
        var refresh = session == null ? "Retry gateway connection" : "Refresh machines";
        AutomationProperties.SetName(RefreshMachinesButton, refresh);
        ToolTipService.SetToolTip(RefreshMachinesButton, refresh);
        Picker.IsEnabled = MachineBrowser.IsEnabled && selectedMachine is { Available: true } && Picker.Items.Count > 0;
    }

    async Task WatchGateway(CoreSession s)
    {
        var end = await s.Completion;
        if (session != s || closing) return;
        session = null; suggested = null;
        RequestViewer(null);
        CoreLabel.Text = "Not connected";
        ShowScope(new([], []), null, null);
        Status.Text = end.Message + " Use the sidebar refresh to retry.";
    }
}
