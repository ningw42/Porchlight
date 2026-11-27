using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Porchlight.Core;
using Windows.Graphics;

namespace Porchlight.App;

public sealed partial class MainWindow : Window
{
    sealed record Target(string MachineId, string Address);
    readonly CancellationTokenSource lifetime = new();
    readonly SpaceTabMemory spaceTabs = new();
    LaunchOptions options = new();
    CoreEndpoint? endpoint;
    CoreSession? session;
    HerdMachine? selectedMachine;
    TerminalSpace? selectedSpace;
    Task? connecting;
    bool configured, busy, closing, mayClose, active, updatingSelection, startupPending;
    string? viewerNotice;

    public MainWindow()
    {
        InitializeComponent();
        ConfigureTitleBar();
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
            ShowStatus($"Launch settings: {ex.Message} Correct the options and reopen Porchlight.");
        }
    }
    void ShowStatus(string message = "")
    {
        Status.Text = message;
        Status.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    static string SetupMessage(Exception ex) => ex switch {
        CoreException { IsConnectionTimeout: true } => "",
        CoreException or FormatException or ArgumentException or IOException => ex.Message,
        _ => "Cannot connect to the configured gateway."
    };

    void Connect()
    {
        if (!configured || busy || closing || session != null) return;
        try
        {
            endpoint ??= options.Pipe == null ? CoreEndpoint.Discover() : CoreEndpoint.Select(options.Pipe);
            connecting = ConnectTo(endpoint);
        }
        catch (Exception ex) { ShowStatus(SetupMessage(ex)); Controls(); }
    }

    async Task ConnectTo(CoreEndpoint endpoint)
    {
        busy = true; Controls();
        ShowStatus();
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
        catch (Exception ex) { ShowStatus(SetupMessage(ex)); }
        finally { busy = false; Controls(); }
    }

    readonly Windows.UI.ViewManagement.UISettings uiSettings = new();
    Storyboard? sidebarAnimation;
    SwapChainPanel? expandingSurface;

    void LayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        Sidebar.Width = Math.Min(280, e.NewSize.Width / 2);
        if (e.NewSize.Width < 800 && (e.PreviousSize.Width == 0 || e.PreviousSize.Width >= 800))
            SidebarToggle.IsChecked = false;
        UpdateSidebar(animate: false);
    }

    void SidebarToggled(object sender, RoutedEventArgs e)
    {
        if (SidebarSlot?.IsLoaded == true) UpdateSidebar(animate: !options.NoSidebarAnimation);
    }

    void UpdateSidebar(bool animate)
    {
        bool open = SidebarToggle.IsChecked == true;
        double from = SidebarSlot.ActualWidth, to = open ? Sidebar.Width : 0;
        if (sidebarAnimation is { } previous)
        {
            previous.Completed -= SidebarAnimationCompleted;
            previous.Stop();
            sidebarAnimation = null;
        }
        if (expandingSurface is { } held)
        {
            held.ClearValue(FrameworkElement.MinWidthProperty);
            expandingSurface = null;
        }
        SidebarSlot.Width = to;
        if (!animate || from == to || !uiSettings.AnimationsEnabled)
        {
            Sidebar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            PaneHost.Width = double.NaN;
            PaneHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            return;
        }

        Sidebar.Visibility = Visibility.Visible;
        // On expansion move column one with the divider, but don't shrink the swap chain until
        // its origin has arrived. Otherwise DWM can show the narrow buffer at the old origin.
        // Hold only the drawing surface so the padding and scrollbar still fit.
        // Cleanup uses ClearValue, so don't replace an existing local MinWidth or binding.
        if (open && pane?.FindName("SwapChainPanel") is SwapChainPanel { ActualWidth: > 0 } surface &&
            surface.ReadLocalValue(FrameworkElement.MinWidthProperty) == DependencyProperty.UnsetValue)
        {
            expandingSurface = surface;
            surface.MinWidth = surface.ActualWidth;
            PaneHost.Width = double.NaN;
            PaneHost.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        else
        {
            // Collapse reveals the wide grid from the left. If there is no known drawing
            // surface to hold on expansion, clip the whole host from the right instead.
            PaneHost.Width = Math.Max(0, Workspace.ActualWidth - WorkspaceFrame.Padding.Left - WorkspaceFrame.Padding.Right);
            PaneHost.HorizontalAlignment = open ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        }
        var slide = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(open ? 220 : 140),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }, EnableDependentAnimation = true };
        Storyboard.SetTarget(slide, SidebarSlot);
        Storyboard.SetTargetProperty(slide, "Width");
        var animation = sidebarAnimation = new Storyboard();
        animation.Children.Add(slide);
        animation.Completed += SidebarAnimationCompleted;
        animation.Begin();
    }

    void SidebarAnimationCompleted(object? sender, object e)
    {
        if (sender is Storyboard finished && finished == sidebarAnimation) UpdateSidebar(animate: false);
    }

    void RefreshList(CoreSession s, bool initial = false, bool useAvailableLayout = false)
    {
        if (session != s || closing) return;
        var catalog = s.Directory;
        var machine = catalog.Machines.FirstOrDefault(m => m.Id == selectedMachine?.Id);
        if (initial)
        {
            var address = options.Select;
            var alias = options.Machine ?? (address?.Contains('/') == true ? address.Split('/')[0] : null);
            machine = catalog.Machines.FirstOrDefault(m => alias == null ? m.IsLocal : m.Name == alias);
            startupPending = true;
        }
        if (startupPending)
        {
            // Space order and membership can change with the first layout reply. Wait only for
            // this machine, not for checkout names or other machines, before committing a viewer.
            if (machine != null && catalog.AwaitingLayout(machine) && !useAvailableLayout)
            {
                if (initial) _ = FinishStartupIfLayoutIsLate(s);
                ShowScope(catalog, machine, null);
                return;
            }
            startupPending = false;
            var start = machine == null ? null : catalog.StartupSpace(machine, options.Select);
            viewerNotice = machine == null ? "The requested machine is not available." :
                machine.Available && options.Select != null && start == null ? "The requested terminal is not live on this machine." : null;
            if (!options.Browse && start != null && options.Select is { } exact)
                spaceTabs.Remember(machine!, start, exact);
            ShowScope(catalog, machine, start, enterSpace: !options.Browse);
            return;
        }
        var spaces = machine == null ? [] : catalog.SpacesFor(machine);
        var space = spaces.FirstOrDefault(x => selectedSpace != null && x.Id == selectedSpace.Id);
        if (requested is { } locate && machine != null && locate.MachineId == machine.Id)
        {
            // Never let a reused alias, or a late metadata reply, select a different viewer.
            var t = catalog.Terminals.FirstOrDefault(t => t.Address == locate.Address && t.MachineAlias == (machine.IsLocal ? null : machine.Name));
            if (t != null) space = t.Space;
        }
        ShowScope(catalog, machine, space);
    }

    async Task FinishStartupIfLayoutIsLate(CoreSession s)
    {
        // doc.get is optional. An unresponsive peer must not hide a live tab indefinitely.
        try { await Task.Delay(TimeSpan.FromSeconds(3), lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (session == s && startupPending) RefreshList(s, useAvailableLayout: true);
    }

    void ShowScope(MachineDirectory catalog, HerdMachine? machine, TerminalSpace? space, bool enterSpace = false)
    {
        updatingSelection = true;
        try
        {
            selectedMachine = machine; selectedSpace = space;
            ShowMachineGroups(catalog);
            var items = machine != null && space != null ? catalog.TerminalsIn(machine, space) : [];
            if (enterSpace)
            {
                // Startup or a sidebar switch hands off the viewer; a catalog repaint never does.
                RequestViewer(machine != null && space != null && spaceTabs.Enter(machine, space, items) is { } address
                    ? new(machine.Id, address) : null);
            }
            ShowTerminalTabs(machine, items);
        }
        finally { updatingSelection = false; }
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
    }

    void SpaceChanged(MachineSpaceGroup group)
    {
        if (updatingSelection || session == null || busy || closing) return;
        var catalog = session.Directory;
        var machine = catalog.Machines.FirstOrDefault(m => m.Id == group.Machine.Id);
        var space = machine != null && group.SelectedSpace is { } pick ? catalog.SpacesFor(machine).FirstOrDefault(s => s.Id == pick.Id) : null;
        startupPending = false; viewerNotice = null;
        ShowScope(catalog, machine, space, enterSpace: true);
    }

    void RefreshMachines_Click(object sender, RoutedEventArgs e)
    {
        if (session == null) Connect(); else session.RefreshMachines();
    }

    void TerminalSelected(object sender, SelectionChangedEventArgs e)
    {
        if (updatingSelection || closing || busy || session == null || selectedMachine is not { Available: true } machine ||
            selectedSpace is not { } space || TerminalTabs.SelectedItem is not TabViewItem { Tag: Target target } || target.MachineId != machine.Id) return;
        startupPending = false; viewerNotice = null;
        spaceTabs.Remember(machine, space, target.Address);
        RequestViewer(target);
    }

    void SelectionStatus()
    {
        if (requested != null || session == null || closing) return;
        ShowStatus(startupPending ? "" : viewerNotice ?? (selectedMachine is { Available: false } m
            ? $"{m.DisplayName}: {m.State.Replace('_', ' ')}. Check the connection in HerdR, then refresh the machines." : ""));
    }
    void Controls()
    {
        if (TerminalTabs == null || RefreshMachinesButton == null) return;
        bool idle = !busy && !closing;
        CoreLabel.Visibility = session == null ? Visibility.Collapsed : Visibility.Visible;
        MachineBrowser.IsEnabled = idle && session != null;
        RefreshMachinesButton.IsEnabled = idle && configured && (session == null || session.SupportsMachineList);
        var refresh = session == null ? "Retry gateway connection" : "Refresh machines";
        AutomationProperties.SetName(RefreshMachinesButton, refresh);
        ToolTipService.SetToolTip(RefreshMachinesButton, refresh);
        TerminalTabs.IsEnabled = MachineBrowser.IsEnabled && selectedMachine is { Available: true };
    }

    async Task WatchGateway(CoreSession s)
    {
        var end = await s.Completion;
        if (session != s || closing) return;
        session = null; startupPending = false;
        RequestViewer(null);
        ShowScope(new([], []), null, null);
        ShowStatus(end.Message);
    }
}
