using Microsoft.Terminal.Control;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Porchlight.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace Porchlight.App;

public sealed partial class MainWindow : Window
{
    readonly CancellationTokenSource lifetime = new();
    LaunchOptions options = new();
    CoreSession? session;
    TermControl? pane;
    LiveTerminal? viewing;
    IReadOnlyList<HerdMachine> shownMachines = [];
    IReadOnlyList<LiveTerminal> shownTerminals = [];
    Task? connecting;
    bool busy, closing, mayClose, active, insidePane, updatingPickers;

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
            await Task.Yield(); // leave this synchronous cancellable event before calling Close
            if (connecting != null) await connecting;
            if (session != null) { var s = session; await s.DisposeAsync(); Release(s); }
            mayClose = true; Close();
        };
        Closed += (_, _) => lifetime.Dispose();
        Controls();
    }

    internal void Initialize(IEnumerable<string> args)
    {
        try
        {
            try { options = LaunchOptions.Parse(args); }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                ConnectButton.IsEnabled = PipePath.IsEnabled = false;
                Status.Text = $"Launch settings: {ex.Message} Correct the options and reopen Porchlight.";
                return;
            }
            if (options.Scheme is { } scheme)
            {
                Title = $"Porchlight · {scheme.Name}";
                var bg = scheme.Background;
                PaneHost.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, bg.R, bg.G, bg.B));
            }
            else if (options.ImportedFont) Title = "Porchlight · Windows Terminal font";
            var ep = options.Pipe == null ? CoreEndpoint.Discover() : CoreEndpoint.Select(options.Pipe);
            PipePath.Text = ep.Path;
            connecting = ConnectTo(ep);
        }
        catch (Exception ex) { Status.Text = SetupMessage(ex); }
    }
    static string SetupMessage(Exception ex) => ex is CoreException or FormatException or ArgumentException or IOException
        ? ex.Message : "Cannot resolve the core. Enter its full local pipe and choose Connect.";

    void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (busy || closing || session != null) return;
        try { connecting = ConnectTo(CoreEndpoint.Select(PipePath.Text)); }
        catch (ArgumentException ex) { Status.Text = ex.Message; }
    }

    async Task ConnectTo(CoreEndpoint endpoint)
    {
        busy = true; Controls();
        CoreLabel.Text = $"Endpoint from {endpoint.Source}"; Status.Text = "Connecting to the existing core…";
        try
        {
            var s = await CoreSession.ConnectAsync(endpoint, lifetime.Token);
            if (closing) { await s.DisposeAsync(); return; }
            session = s;
            var id = s.Identity;
            CoreLabel.Text = $"Gateway: {id.Name} · HerdR {id.Version} · core {id.Id}";
            s.CatalogChanged += () => DispatcherQueue.TryEnqueue(() => RefreshList(s));
            RefreshList(s, initial: true);
            _ = WatchEnd(s);
        }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex) { Status.Text = SetupMessage(ex); }
        finally { busy = false; Controls(); }
    }

    void RefreshList(CoreSession s, bool initial = false)
    {
        if (session != s || viewing != null || closing) return;
        var catalog = s.Directory;
        var prior = MachinePicker.SelectedItem as HerdMachine;
        string? address = (Picker.SelectedItem as LiveTerminal)?.Address;
        var machine = catalog.Machines.FirstOrDefault(m => m.Id == prior?.Id);
        if (initial)
        {
            address = options.Select;
            var alias = options.Machine ?? (address?.Contains('/') == true ? address.Split('/')[0] : null);
            machine = catalog.Machines.FirstOrDefault(m => alias == null ? m.IsLocal : m.Name == alias);
            options.Machine = options.Select = null;
        }
        updatingPickers = true;
        try
        {
            if (!shownMachines.SequenceEqual(catalog.Machines))
                MachinePicker.ItemsSource = shownMachines = catalog.Machines;
            // XAML selects an object from its actual ItemsSource, not a value-equal
            // replacement from a later metadata reply.
            MachinePicker.SelectedItem = shownMachines.FirstOrDefault(m => m.Id == machine?.Id);
            ShowTerminals(catalog, machine, address);
        }
        finally { updatingPickers = false; }
        SelectionStatus(); Controls();
    }
    void ShowTerminals(MachineDirectory catalog, HerdMachine? machine, string? address)
    {
        var items = catalog.Terminals.Where(t => machine is { Available: true } &&
            t.MachineAlias == (machine.IsLocal ? null : machine.Name)).ToArray();
        if (!shownTerminals.SequenceEqual(items)) Picker.ItemsSource = shownTerminals = items;
        Picker.SelectedItem = shownTerminals.FirstOrDefault(t => t.Address == address);
    }
    void MachineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updatingPickers || session == null || viewing != null) return;
        // A machine change clears the terminal, even if both machines have the same tN.
        ShowTerminals(session.Directory, MachinePicker.SelectedItem as HerdMachine, null);
        SelectionStatus(); Controls();
    }
    void MachinesOpened(object sender, object e) => session?.RefreshMachines();
    void SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!updatingPickers) { SelectionStatus(); Controls(); } }
    void SelectionStatus()
    {
        if (viewing != null || session == null || Status == null) return;
        Status.Text = MachinePicker.SelectedItem switch {
            not HerdMachine => "Select a machine. Open the machine list to refresh its connections.",
            HerdMachine { Available: false } m => $"{m.Name}: {m.State.Replace('_', ' ')}. Check the connection in HerdR, then reopen the machine list.",
            _ when Picker.SelectedItem is LiveTerminal t => "Selected · " + t.Label,
            HerdMachine m when Picker.Items.Count == 0 => $"No live terminals on {m.Name}. This viewer does not start one.",
            _ => "Select a live terminal, then Attach."
        };
    }
    void Controls()
    {
        if (Attach == null || Disconnect == null) return;
        bool idle = !busy && !closing;
        PipePath.IsEnabled = ConnectButton.IsEnabled = idle && session == null;
        MachinePicker.IsEnabled = idle && session != null && viewing == null;
        Picker.IsEnabled = MachinePicker.IsEnabled && MachinePicker.SelectedItem is HerdMachine { Available: true } && Picker.Items.Count > 0;
        Attach.IsEnabled = Picker.IsEnabled && Picker.SelectedItem is LiveTerminal;
        Disconnect.IsEnabled = idle && session != null;
        Disconnect.Content = viewing == null ? "Disconnect" : "Detach";
    }

    async void Attach_Click(object sender, RoutedEventArgs e)
    {
        if (session is not { } s || MachinePicker.SelectedItem is not HerdMachine { Available: true } machine ||
            Picker.SelectedItem is not LiveTerminal target || viewing != null || busy || closing) return;
        viewing = target; Controls(); Status.Text = "Attaching to " + target.Label + "…";
        try
        {
            var font = options.Font;
            var settings = new TerminalSettings(options.Scheme) {
                FontFace = font.Face, FontSize = font.Size, FontWeight = new() { Weight = font.Weight },
                CellHeight = font.CellHeight, CellWidth = font.CellWidth,
                EnableBuiltinGlyphs = font.BuiltinGlyphs, EnableColorGlyphs = font.ColorGlyphs,
                FontFeatures = NativeFeatures.Create(font.Features), FontAxes = NativeFeatures.Create(font.Axes)
            };
            var conn = new PaneConnection(s, machine.Id, target.Address, ok => DispatcherQueue.TryEnqueue(() => {
                if (session != s || closing || busy) return;
                Status.Text = ok ? "Attached · " + target.Label : "Attach failed. Reconnect and select a live terminal.";
                if (ok) pane?.Focus(FocusState.Programmatic);
            }));
            pane = new TermControl(settings, settings, conn) { OwningHwnd = (ulong)WinRT.Interop.WindowNative.GetWindowHandle(this) };
            pane.KeyBindings(new AppKeyBindings(pane));
            pane.GotFocus += (_, _) => { insidePane = true; SendFocus(); };
            pane.LostFocus += (_, _) => { insidePane = false; SendFocus(); };
            pane.WriteToClipboard += (_, a) => {
                try { var data = new DataPackage(); data.SetText(a.Plain); Clipboard.SetContent(data); }
                catch { Status.Text = "Clipboard unavailable."; }
            };
            pane.PasteFromClipboard += async (_, a) => {
                try {
                    var content = Clipboard.GetContent();
                    if (content.Contains(StandardDataFormats.Text)) {
                        var text = await content.GetTextAsync();
                        if (session == s && !busy && !closing) a.HandleClipboardData(text);
                    }
                } catch { Status.Text = "Clipboard unavailable."; }
            };
            PaneHost.Child = pane;
        }
        catch
        {
            await s.DisposeAsync(); Release(s);
            Status.Text = "Could not host the terminal. Rebuild the x64 app and native feature helper; check Windows App Runtime 1.8 x64.";
        }
    }
    void SendFocus() { if (!closing && !busy && pane != null) session?.Focus(active && insidePane); }

    async Task WatchEnd(CoreSession s)
    {
        var end = await s.Completion;
        if (session != s) return;
        Release(s);
        Status.Text = end.Message + " Choose Connect for a fresh list.";
    }
    async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (session is not { } s || busy || closing) return;
        busy = true; Controls();
        await s.DisposeAsync();
        busy = false; Controls();
    }
    void Release(CoreSession s)
    {
        if (session != s) return;
        session = null; viewing = null; insidePane = false;
        // The reader is joined before unsubscribing/destroying native output callbacks.
        pane?.Close(); PaneHost.Child = null; pane = null;
        shownMachines = []; shownTerminals = [];
        MachinePicker.ItemsSource = null; Picker.ItemsSource = null; CoreLabel.Text = "Not connected";
        Controls();
    }
}
