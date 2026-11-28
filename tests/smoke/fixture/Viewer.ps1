using namespace System.Windows.Automation
using namespace System.IO.Pipes
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
# Query the fixture HWND's non-client hit test; don't move the mouse or send clicks to the desktop.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PorchlightChrome {
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32", SetLastError=true)] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    public static void Resize(IntPtr hwnd, int w, int h) {
        // SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE
        if (!SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h, 0x16)) throw new System.ComponentModel.Win32Exception();
    }
    [DllImport("user32")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32")] static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    public static int Hit(IntPtr hwnd, int x, int y) =>
        (int)SendMessage(hwnd, 0x0084, IntPtr.Zero, (IntPtr)((x & 0xffff) | (y << 16)));
}
'@
. "$PSScriptRoot/../Find-TerminalItem.ps1"

function FindId($id, $within = $root) {
    $within.FindFirst('Descendants', [PropertyCondition]::new([AutomationElement]::AutomationIdProperty, $id))
}
function WaitFor($description, [scriptblock]$condition) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ($peer.State -in 'Failed','Stopped','Completed') { Receive-Job $peer; throw 'Fixture stopped unexpectedly' }
        if ($viewer.HasExited) { throw "Viewer exited ($($viewer.ExitCode)) waiting for $description" }
        $v = & $condition; if ($v) { return $v }
        Start-Sleep -Milliseconds 80
    } while ($clock.Elapsed.TotalSeconds -lt 10)
    $status = if ($root) { (FindId Status).Current.Name }
    throw "Timed out waiting for $description (Status: $status)"
}
function Pattern($id, $pattern) {
    (WaitFor $id { FindId $id }).GetCurrentPattern($pattern)
}
function SpaceItem($machine, $space) {
    $list = FindId "Spaces-$machine"
    if ($list) {
        $list.FindFirst('Children', [AndCondition]::new(
            [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::ListItem),
            [PropertyCondition]::new([AutomationElement]::NameProperty, $space)))
    }
}
function SpacePattern($machine, $space) {
    (WaitFor "$machine / $space" { SpaceItem $machine $space }).GetCurrentPattern([SelectionItemPattern]::Pattern)
}
function StatusHidden {
    $status = FindId Status
    !$status -or $status.Current.IsOffscreen
}
function TabsMatch([string[]]$expected, [string]$selected) {
    $tabs = FindId TerminalTabs
    if (!$tabs -or !$tabs.Current.IsEnabled) { return $false }
    $items = $tabs.FindAll('Descendants', [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::TabItem))
    if ($items.Count -ne $expected.Count) { return $false }
    foreach ($label in $expected) {
        $item = Find-TerminalItem $tabs $viewer.Id $label
        if (!$item -or $item.GetCurrentPattern([SelectionItemPattern]::Pattern).Current.IsSelected -ne ($label -ceq $selected)) { return $false }
    }
    return $true
}
function AuthorizeAttach($address) {
    if (!$address -or $state.nextTerminal -or $state.expectDetach -or $state.activeAddress -ceq $address) {
        throw "Cannot authorize $address (pending: '$($state.nextTerminal)', active: '$($state.activeAddress)')"
    }
    $state.nextTerminal = $address # Consumed exactly once by the peer, never inferred from a client request.
}
function PaneMatches($address, $label) {
    !$state.nextTerminal -and !$state.expectDetach -and [string]$state.activeAddress -ceq [string]$address -and
        [string](FindId PaneHost).Current.Name -ceq [string]$label
}
function CheckTerminalFocus($address) {
    [void](WaitFor "keyboard focus in the native pane for $address" {
        $hostPane = FindId PaneHost
        if ($hostPane -and $state.activeAddress -ceq $address) {
            $pane = $hostPane.FindFirst('Descendants', [PropertyCondition]::new([AutomationElement]::ClassNameProperty, 'TermControl'))
            $pane -and $pane.Current.HasKeyboardFocus
        }
    })
}
function CheckScope($machine, $space, $terminals, $address) {
    $label = if ($address) { $terminals[$address] } else { '' }
    [void](WaitFor "$machine / $space, tabs [$($terminals.Values -join ', ')], viewer '$address'" {
        $item = SpaceItem $machine $space
        (StatusHidden) -and $item -and $item.GetCurrentPattern([SelectionItemPattern]::Pattern).Current.IsSelected -and
            (TabsMatch $terminals.Values $label) -and (PaneMatches $address $label)
    })
    if ($machine -eq 'core-studio' -and (SpacePattern core-local Work).Current.IsSelected) { throw 'Two spaces selected' }
}
function SelectSpace($machine, $space, $terminals, $address, [switch]$focusSettings) {
    $selection = SpacePattern $machine $space
    if ($terminals.Count) { AuthorizeAttach $address }
    else { $state.expectDetach = [bool]$state.activeAddress } # Empty destination: CLOSE only if leaving a viewer.
    if ($CheckFocus) { (SpaceItem $machine $space).SetFocus() } # Start from the same focus owner as a sidebar click.
    $selection.Select()
    if ($focusSettings) { (FindId SettingsButton).SetFocus() } # The peer is still holding the old channel's CLOSE.
    CheckScope $machine $space $terminals $address
    if ($focusSettings) {
        if (!(FindId SettingsButton).Current.HasKeyboardFocus) { throw 'The new terminal took focus back from Settings.' }
    } elseif ($CheckFocus -and $address) { CheckTerminalFocus $address }
}
function CommitTerminal($terminals, $address) {
    $label = $terminals[$address]; $previous = $state.activeAddress
    $item = WaitFor "terminal tab $label" { Find-TerminalItem (FindId TerminalTabs) $viewer.Id $label }
    $selection = $item.GetCurrentPattern([SelectionItemPattern]::Pattern)
    AuthorizeAttach $address
    $selection.Select()
    if (!(StatusHidden)) { throw "Unexpected notice while switching to $label" }
    [void](WaitFor "selected and attached $label" {
        (TabsMatch $terminals.Values $label) -and (PaneMatches $address $label) -and (StatusHidden)
    })
    [void](WaitFor "repaint of $address in the native pane" {
        $pane = (FindId PaneHost).FindFirst('Descendants', [PropertyCondition]::new([AutomationElement]::ClassNameProperty, 'TermControl'))
        $pattern = $null
        if ($pane -and $pane.TryGetCurrentPattern([TextPattern]::Pattern, [ref]$pattern)) {
            $screen = $pattern.DocumentRange.GetText(-1)
            $screen.Contains("Fixture $address") -and (!$previous -or !$screen.Contains("Fixture $previous"))
        }
    })
}

# Each invocation owns its viewer, pipe, peer and mutable expectations. Suite scriptblocks
# run in this scope chain so UI helpers always address this viewer and its authorized channel.
function Invoke-SmokeFixture([Parameter(Mandatory)][scriptblock]$Test, [scriptblock]$BeforeConnect, [switch]$Instant, [switch]$PaintCells, [string[]]$FontArguments = @()) {
    # Expected wire routes and their UI labels are deliberately distinct on the relayed core.
    $localWork = @{ t74='t74 · local twin · pwsh'; t76='t76 · work sibling' }
    $remoteWork = @{ 'core-studio/t74'='studio/t74 · remote twin'; 'core-studio/t76'='studio/t76 · remote sibling' }
    $unassigned = @{ t77='t77 · loose first'; t78='t78 · loose second' }
    $personal = @{ t75='t75 · π - ScopeJobs' }
    # Bind before starting the viewer. One hello and one subscription on this pipe; no real core or program.
    $name = 'porchlight-smoke-' + [guid]::NewGuid().ToString('N')
    $pipe = [NamedPipeServerStream]::new($name, 'InOut', 1, 'Byte', 'Asynchronous,CurrentUserOnly')
    $state = [hashtable]::Synchronized(@{ catalogEdit=[Threading.Tasks.TaskCompletionSource[string]]::new(); closing=$false; expectDetach=$false
        nextTerminal=$null; activeAddress=$null; resizeCount=0; columns=0; paintCells=$PaintCells.IsPresent; measureTabs=$MeasureSwitchTabs; deferLocalLayout=($Startup -eq 'Select') })
    $viewer = $peer = $root = $null
    try {
        $start = [Diagnostics.ProcessStartInfo]::new($exePath); $start.UseShellExecute = $false
        $start.Environment['HERDR_APP_HOME'] = Join-Path ([IO.Path]::GetTempPath()) $name # private, no programs.json overrides
        $start.ArgumentList.Add("--pipe=\\.\pipe\$name")
        foreach ($arg in $FontArguments) { $start.ArgumentList.Add($arg) }
        if ($Instant) { $start.ArgumentList.Add('--no-sidebar-animation') }
        if ($Startup -ne 'Default') { $start.ArgumentList.Add('--select=t76') }
        if ($Startup -eq 'Browse') { $start.ArgumentList.Add('--browse') }
        $startupTerminal = switch ($Startup) { 'Default' { 't74' } 'Select' { 't76' } 'Browse' { $null } }
        if ($startupTerminal) { AuthorizeAttach $startupTerminal }
        $viewer = [Diagnostics.Process]::Start($start)
        $hwnd = WaitFor 'viewer HWND' { $viewer.Refresh(); if ($viewer.MainWindowHandle -ne [IntPtr]::Zero) { $viewer.MainWindowHandle } }
        $root = [AutomationElement]::FromHandle($hwnd)
        if ($root.Current.ProcessId -ne $viewer.Id) { throw 'Wrong HWND owner' }
        $toggle = Pattern SidebarToggle ([TogglePattern]::Pattern)
        if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() } # Narrow windows start collapsed.
        if ($BeforeConnect) { & $BeforeConnect }
        if ($CheckFocus) {
            # Allow startup auto-selection only after the fixture owns keyboard focus, not in a background window.
            [void](WaitFor 'foreground fixture window for keyboard focus checks' {
                [PorchlightChrome]::GetForegroundWindow() -eq $hwnd -or [PorchlightChrome]::SetForegroundWindow($hwnd)
            })
        }
        $peer = Start-ThreadJob -FilePath "$PSScriptRoot/FakeGateway.ps1" -ArgumentList $pipe,$state
        if ($CheckFocus -and $startupTerminal) { CheckTerminalFocus $startupTerminal } # No click or tab switch at launch.
        CheckScope core-local Work $localWork $startupTerminal # No manual tab selection at launch.
        & $Test

        $state.closing = $true
        (Pattern Close ([InvokePattern]::Pattern)).Invoke()
        if (!$viewer.WaitForExit(5000)) { throw 'Viewer did not close normally' }
        if ($viewer.ExitCode -ne 0) { throw "Viewer exit code $($viewer.ExitCode)" }
        if (!(Wait-Job $peer -Timeout 5)) { throw 'Fixture did not see viewer disconnect' }
        Receive-Job $peer
    } finally {
        try {
            if ($viewer -and !$viewer.HasExited) { $viewer.Kill(); $viewer.WaitForExit() }
        } finally {
            $pipe.Dispose(); if ($peer) { Remove-Job $peer -Force }; if ($viewer) { $viewer.Dispose() }
        }
    }
}
