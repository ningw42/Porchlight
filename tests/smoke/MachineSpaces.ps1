#requires -Version 7.4
# Run on an interactive Windows desktop: ./MachineSpaces.ps1 -Exe C:\isolated\Porchlight.exe
using namespace System.Windows.Automation
using namespace System.IO.Pipes
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Exe)
$ErrorActionPreference = 'Stop'
$exePath = (Get-Item -LiteralPath $Exe).FullName
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
# Query the fixture HWND's non-client hit test; don't move the mouse or send clicks to the desktop.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PorchlightChrome {
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32")] static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    public static int Hit(IntPtr hwnd, int x, int y) =>
        (int)SendMessage(hwnd, 0x0084, IntPtr.Zero, (IntPtr)((x & 0xffff) | (y << 16)));
}
'@

. "$PSScriptRoot/Find-TerminalItem.ps1"

# Bind before starting the viewer. One hello and one subscription on this pipe; no real core or program.
$name = 'porchlight-smoke-' + [guid]::NewGuid().ToString('N')
$pipe = [NamedPipeServerStream]::new($name, 'InOut', 1, 'Byte', 'Asynchronous,CurrentUserOnly')
$state = [hashtable]::Synchronized(@{ refresh=$false; closing=$false; leavingSpace=$false; nextTerminal=$null; attaches=0; switchAcks=0 })
$viewer = $peer = $root = $null
$serve = {
    param($pipe, $state)
    $ErrorActionPreference = 'Stop'
    $utf8 = [Text.Encoding]::UTF8
    function Reply($id, $result) {
        ,$utf8.GetBytes((@{ jsonrpc='2.0'; id=$id; result=$result } | ConvertTo-Json -Depth 20 -Compress))
    }
    function WriteFrame([uint32]$channel, [byte]$kind, [byte[]]$bytes) {
        $writer.Write($channel); $writer.Write($kind); $writer.Write($bytes.Length); $writer.Write($bytes)
    }
    $eventChannel = 9
    $attachChannels = @(41, 53, 67) # Fresh even when reattaching the first fixture.
    $targets = @{
        t74 = @{ address='t74'; name='local twin'; state='live'; cwd='C:\fixture\launch'; foreground=@{cwd='C:\fixture\Work\src'} }
        t76 = @{ address='t76'; name='work sibling'; state='live'; labels=@{'herdr.space'='sp1'} }
    }
    $pipe.WaitForConnection()
    $reader = [IO.BinaryReader]::new($pipe, $utf8, $true)
    $writer = [IO.BinaryWriter]::new($pipe, $utf8, $true)
    $line = [Collections.Generic.List[byte]]::new()
    while (($b = $reader.ReadByte()) -ne 10) {
        if ($line.Count -ge 4096) { throw 'Oversized hello' }; $line.Add($b)
    }
    $hello = $utf8.GetString($line.ToArray()) | ConvertFrom-Json
    if ($hello.method -cne 'hello' -or !$hello.params.framed) { throw 'Expected framed hello' }
    $writer.Write([byte[]](Reply $hello.id @{ core=@{ id='core-local'; name='fixture-hostname' }; version='smoke'; generation=1
        framed=$true; methods=@('machine.list','events.subscribe','terminal.attach','doc.get','machine.exec'); features=@() }))
    $writer.Write([byte]10)
    $header = [byte[]]::new(9); $active = 0; $holdingClose = $null; $subscribed = $false
    while ($true) {
        # Keep reading while withholding the old channel's empty CLOSE. Sleeping in the peer
        # and blindly echoing CLOSE would let a pipelined attach sit unread and pass the test.
        $read = $pipe.ReadAsync($header, 0, $header.Length)
        if ($holdingClose -and !$read.Wait([Math]::Max(0, 500 - [int]$holdingClose.ElapsedMilliseconds))) {
            WriteFrame $active 2 ([byte[]]@())
            $active = 0; $holdingClose = $null; $state.switchAcks++
        }
        $got = $read.GetAwaiter().GetResult()
        if (!$got) { if ($state.closing) { return }; throw 'Viewer disconnected from the original fixture pipe' }
        $pipe.ReadExactly($header, $got, $header.Length - $got)
        $channel = [BitConverter]::ToUInt32($header, 0); $kind = $header[4]; $n = [BitConverter]::ToUInt32($header, 5)
        if ($n -gt 4096) { throw "Oversized fixture frame ($n bytes)" }
        $body = $reader.ReadBytes([int]$n); if ($body.Length -ne $n) { throw 'Truncated fixture frame' }

        if ($channel -ne 0) {
            if ($channel -eq $eventChannel -and $subscribed -and $kind -eq 1 -and $n -eq 4) { continue } # event credit
            if ($channel -ne $active) { throw "Unexpected stream channel $channel (active: $active, kind: $kind)" }
            switch ($kind) {
                1 { if ($n -ne 4) { throw 'Invalid terminal credit' } }
                2 {
                    if ($n -or $holdingClose) { throw "Expected one empty CLOSE on channel $active" }
                    if ($state.closing -or $state.leavingSpace) { WriteFrame $active 2 ([byte[]]@()); $active = 0; $state.leavingSpace = $false }
                    elseif ($state.nextTerminal) { $holdingClose = [Diagnostics.Stopwatch]::StartNew() }
                    else { throw "Unrequested CLOSE on channel $active" }
                }
                3 { if ($n -ne 4) { throw 'Invalid terminal resize' } }
                4 { if ($n -ne 1 -or $body[0] -gt 1) { throw 'Invalid terminal focus' } }
                default { throw "Unexpected frame kind $kind on channel $channel" }
            }
            continue
        }
        if ($kind -ne 0) { throw "Unexpected RPC frame kind $kind" }
        $request = $utf8.GetString($body) | ConvertFrom-Json
        switch -CaseSensitive ($request.method) {
            'machine.list' {
                $machines = @(@{ id='core-studio'; name='studio'; state='connected' })
                if ($state.refresh) { $machines += @{ id='core-extra'; name='extra'; state='connected' } }
                $result = @{ machines=$machines }
                if ($active) {
                    # Insert a tab before the selected one; its address, not its index, must stay selected.
                    $newTab = @{ address='t73'; name='new sibling'; state='live'; labels=@{'herdr.space'='sp1'} }
                    $event = @{ method='event'; params=@{ seq=81; type='created'; terminal='t73'; data=$newTab } } | ConvertTo-Json -Depth 8 -Compress
                    WriteFrame $eventChannel 0 ($utf8.GetBytes("$event`n"))
                }
            }
            'events.subscribe' {
                if ($subscribed) { throw 'Viewer subscribed a second time' }; $subscribed = $true
                $result = @{ channel=$eventChannel; seq=80; terminals=@(
                    $targets.t74; $targets.t76
                    @{ address='t75'; name='personal terminal'; state='live'; labels=@{'herdr.space'='sp2'} }
                    @{ address='studio/t74'; name='remote twin'; state='live' }) }
            }
            'terminal.attach' {
                $address = $request.params.terminal
                if ($active) { throw "Attach to $address before empty CLOSE acknowledgement on channel $active" }
                if (!$subscribed -or !$state.nextTerminal -or $address -cne $state.nextTerminal -or !$targets.ContainsKey($address)) {
                    throw "Unexpected terminal.attach: $address (authorized tab selection: '$($state.nextTerminal)')"
                }
                $active = $attachChannels[$state.attaches]; $state.attaches++; $state.nextTerminal = $null
                $result = @{ channel=$active; terminal=$targets[$address] }
            }
            'doc.get' {
                if ($request.params.name -cne 'herdr-app') { throw 'Unexpected document request' }
                $work = @{ id='sp1'; name='Work'; tabs=@(@{ root=@{ type='block'; content=@{ terminal='t74' } } }) }
                $spaces = switch ($request.params.on) {
                    $null {
                        # Saved order differs from alphabetical order; the CLI target is not in the first space.
                        @{ id='sp3'; name='Archive'; tabs=@() }
                        @{ id='sp1'; name=''; folder=$HOME; tabs=$work.tabs }
                        @{ id='sp2'; name='Personal'; tabs=@() }
                    }
                    'core-studio' { $work }
                    'core-extra' { }
                    default { throw 'Unexpected document target' }
                }
                $result = @{ value=@{ spaces=@($spaces | Where-Object { $_ }) }; version=1 }
            }
            'machine.exec' {
                # Reply with metadata only. Never execute the requested program in the fixture.
                if ($request.params.on -or $request.params.line -or
                    ($request.params.argv -join '|') -cne 'git|-C|C:\fixture\Work\src|rev-parse|--show-toplevel|--abbrev-ref|HEAD') {
                    throw 'Unexpected checkout query'
                }
                $stdout = [Convert]::ToBase64String($utf8.GetBytes("C:/fixture/Work`nmain`n"))
                $result = @{ code=0; timed_out=$false; truncated=$false; stdout=$stdout; stderr=''; ms=1 }
            }
            default { throw "Unexpected RPC: $($request.method)" }
        }
        WriteFrame 0 0 (Reply $request.id $result)
        if ($request.method -ceq 'terminal.attach') { WriteFrame $active 0 ($utf8.GetBytes("Fixture $address`r`n")) }
    }
}

function FindId($id) {
    $root.FindFirst('Descendants', [PropertyCondition]::new([AutomationElement]::AutomationIdProperty, $id))
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
function ExpandGroup($machine, $expanded) {
    $id = "MachineGroup-$machine"; $pattern = [ExpandCollapsePattern]::Pattern
    $target = if ($expanded) { 'Expanded' } else { 'Collapsed' }
    $p = Pattern $id $pattern
    if ($p.Current.ExpandCollapseState -ne $target) {
        if ($expanded) { $p.Expand() } else { $p.Collapse() }
    }
    [void](WaitFor "$id $target" { (FindId $id).GetCurrentPattern($pattern).Current.ExpandCollapseState -eq $target })
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
function IsExpanded($machine) {
    (Pattern "MachineGroup-$machine" ([ExpandCollapsePattern]::Pattern)).Current.ExpandCollapseState -eq 'Expanded'
}
function StatusHidden {
    $status = FindId Status
    !$status -or $status.Current.IsOffscreen
}
function TabsInCaption($label) {
    $strip = FindId TerminalTabs
    $tabs = $strip.Current.BoundingRectangle
    $minimize = (FindId Minimize).Current.BoundingRectangle
    $button = (FindId SidebarToggle).Current.BoundingRectangle
    if (!$tabs -or $tabs.IsEmpty -or !$minimize -or $minimize.IsEmpty -or !$button -or $button.IsEmpty) { return $false }
    $scale = [PorchlightChrome]::GetDpiForWindow($hwnd) / 96.0
    $left = (FindId AppTitleBar).Current.BoundingRectangle.Left
    if ([Math]::Abs($button.Left - $left) -gt 8 * $scale -or $tabs.Left -lt $button.Right - 1) { return $false }
    if ($toggle.Current.ToggleState -eq 'Off' -and $tabs.Left -gt $button.Right + 8 * $scale) { return $false }
    if ([PorchlightChrome]::Hit($hwnd, $button.Left + $button.Width / 2, $button.Top + $button.Height / 2) -ne 1) { return $false }
    $y = [int]($minimize.Top + $minimize.Height / 2)
    if ($tabs.Top -ge $y -or $tabs.Bottom -le $y -or $tabs.Right -gt $minimize.Left + 1) { return $false }
    if ([PorchlightChrome]::Hit($hwnd, $minimize.Left + $minimize.Width / 2, $y) -ne 8) { return $false } # HTMINBUTTON
    if ($toggle.Current.ToggleState -eq 'On' -and
        [PorchlightChrome]::Hit($hwnd, ($button.Right + $tabs.Left) / 2, $y) -ne 2) { return $false } # HTCAPTION above the sidebar
    if ([PorchlightChrome]::Hit($hwnd, $tabs.Right - 8, $y) -ne 2) { return $false } # HTCAPTION in the footer
    if (!$label) { return $true }
    $tab = (Find-TerminalItem $strip $viewer.Id $label).Current.BoundingRectangle
    $tab -and !$tab.IsEmpty -and $tab.Top -lt $y -and $tab.Bottom -gt $y -and
        [Math]::Abs($button.Top - $tab.Top) -le $scale -and $tabs.Height -le $tab.Height + 9 * $scale -and
        [PorchlightChrome]::Hit($hwnd, $tab.Left + $tab.Width / 2, $y) -eq 1 # HTCLIENT on the tab itself
}
function CaptionFits($expectedHeight) {
    $scale = [PorchlightChrome]::GetDpiForWindow($hwnd) / 96.0
    $row = (FindId AppTitleBar).Current.BoundingRectangle
    if (!$row -or $row.IsEmpty -or [Math]::Abs($row.Height / $scale - $expectedHeight) -gt 1) { return $false }
    # Check the visual bounds AND the native hit at the bottom of each button, not just its center.
    $roles = @{ Minimize=8; Maximize=9; Close=20 }
    foreach ($id in $roles.Keys) {
        $r = (FindId $id).Current.BoundingRectangle
        if (!$r -or $r.IsEmpty -or [Math]::Abs($r.Top - $row.Top) -gt 1 -or [Math]::Abs($r.Bottom - $row.Bottom) -gt 1 -or
            [PorchlightChrome]::Hit($hwnd, $r.Left + $r.Width / 2, $r.Bottom - 2 * $scale) -ne $roles[$id]) { return $false }
    }
    return $true
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
$localWork = @('t74 · local twin', 't76 · work sibling')
function CheckScope($machine, $display) {
    $expected = if ($machine -eq 'core-local') { $localWork } else { 'studio/t74 · remote twin' }
    [void](WaitFor "$display / Work selected with only its unselected terminal tabs" {
        $space = SpaceItem $machine Work
        (StatusHidden) -and (TabsMatch $expected '') -and $space -and $space.GetCurrentPattern([SelectionItemPattern]::Pattern).Current.IsSelected
    })
    if ($machine -eq 'core-studio' -and (SpacePattern core-local Work).Current.IsSelected) { throw 'Two spaces selected' }
}
function CheckLocalOrder {
    $items = (FindId Spaces-core-local).FindAll('Children', [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::ListItem))
    $names = ($items | ForEach-Object { $_.Current.Name }) -join ', '
    if ($names -cne 'Archive, Work, Personal') { throw "Wrong saved space order: $names" }
}
function CheckNoAttachButtons {
    foreach ($id in 'Attach','ConnectionButton','Disconnect') { if (FindId $id) { throw "Obsolete control: $id" } }
    foreach ($name in 'Attach','Disconnect') {
        if ($root.FindFirst('Descendants', [AndCondition]::new(
            [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::Button),
            [PropertyCondition]::new([AutomationElement]::NameProperty, $name)))) { throw "Obsolete button: $name" }
    }
}
function CommitTerminal($address, $name) {
    $label = "$address · $name"
    $item = WaitFor "terminal tab $label" { Find-TerminalItem (FindId TerminalTabs) $viewer.Id $label }
    $selection = $item.GetCurrentPattern([SelectionItemPattern]::Pattern)
    $state.nextTerminal = $address # Only this exact commit may cause one RPC.
    $selection.Select()
    [void](WaitFor "selected and attached $label" {
        (TabsMatch $localWork $label) -and (FindId PaneHost).Current.Name -ceq $label -and (StatusHidden) -and !$state.nextTerminal
    })
    [void](WaitFor "repaint of $address in the native pane" {
        $pane = (FindId PaneHost).FindFirst('Descendants', [PropertyCondition]::new([AutomationElement]::ClassNameProperty, 'TermControl'))
        $pattern = $null
        if ($pane -and $pane.TryGetCurrentPattern([TextPattern]::Pattern, [ref]$pattern)) {
            $screen = $pattern.DocumentRange.GetText(-1)
            $other = if ($address -eq 't74') { 't76' } else { 't74' }
            $screen.Contains("Fixture $address") -and !$screen.Contains("Fixture $other")
        }
    })
}

try {
    $peer = Start-ThreadJob -ScriptBlock $serve -ArgumentList $pipe,$state
    $start = [Diagnostics.ProcessStartInfo]::new($exePath); $start.UseShellExecute = $false
    $start.ArgumentList.Add("--pipe=\\.\pipe\$name"); $start.ArgumentList.Add('--select=t74')
    $viewer = [Diagnostics.Process]::Start($start)
    $hwnd = WaitFor 'viewer HWND' { $viewer.Refresh(); if ($viewer.MainWindowHandle -ne [IntPtr]::Zero) { $viewer.MainWindowHandle } }
    $root = [AutomationElement]::FromHandle($hwnd)
    if ($root.Current.ProcessId -ne $viewer.Id) { throw 'Wrong HWND owner' }
    $toggle = Pattern SidebarToggle ([TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() } # Narrow windows start collapsed.
    CheckScope core-local Local; CheckNoAttachButtons; CheckLocalOrder
    [void](WaitFor 'full-height windowed caption buttons' { CaptionFits 40 })
    if ((FindId WindowLabel) -or (FindId ScopeLabel)) { throw 'Duplicate title or machine/space label in the title bar' }
    if (!(IsExpanded core-local) -or !(IsExpanded core-studio)) { throw 'Machines must start expanded' }
    if ((FindId MachineGroup-core-local).Current.Name -cne 'Local, connected') { throw 'Local machine must be labeled Local' }
    if (FindId PipePath) { throw 'The main pane still contains a pipe editor' }
    foreach ($expanded in $false,$true) {
        ExpandGroup core-studio $expanded; CheckScope core-local Local
    }
    (SpacePattern core-local Archive).Select()
    [void](WaitFor 'draggable title bar above the open sidebar and the empty workspace' {
        (FindId Status).Current.Name -ceq 'No live terminals in this space.' -and (TabsMatch @() '') -and (TabsInCaption)
    })
    (SpacePattern core-studio Work).Select(); CheckScope core-studio studio

    $toggleBounds = (FindId SidebarToggle).Current.BoundingRectangle
    $toggle.Toggle()
    [void](WaitFor 'hidden sidebar with toggle unmoved' {
        $group = FindId MachineGroup-core-studio
        $toggle.Current.ToggleState -eq 'Off' -and (!$group -or $group.Current.IsOffscreen) -and
        $toggleBounds.Equals((FindId SidebarToggle).Current.BoundingRectangle) -and (TabsInCaption 'studio/t74 · remote twin')
    })
    $toggle.Toggle()
    [void](WaitFor 'visible sidebar with toggle unmoved' {
        $group = FindId MachineGroup-core-studio
        $toggle.Current.ToggleState -eq 'On' -and $group -and !$group.Current.IsOffscreen -and
        $toggleBounds.Equals((FindId SidebarToggle).Current.BoundingRectangle)
    }); CheckScope core-studio studio
    [void](WaitFor 'tabs in the caption row, to the right of the open sidebar' {
        $sidebar = (FindId MachineBrowser).Current.BoundingRectangle
        $tabs = (FindId TerminalTabs).Current.BoundingRectangle
        $sidebar -and $tabs -and !$sidebar.IsEmpty -and !$tabs.IsEmpty -and
        $sidebar.Width -gt 0 -and $tabs.Width -gt 0 -and $sidebar.Right -le $tabs.Left -and (TabsInCaption 'studio/t74 · remote twin')
    })

    ExpandGroup core-local $false
    $state.refresh = $true
    (Pattern RefreshMachinesButton ([InvokePattern]::Pattern)).Invoke()
    [void](WaitFor 'new machine from refresh reply' { FindId MachineGroup-core-extra })
    if (!(IsExpanded core-extra) -or (IsExpanded core-local)) { throw 'Refresh reset expansion state' }
    ExpandGroup core-local $true; CheckScope core-studio studio; CheckLocalOrder
    (SpacePattern core-local Work).Select(); CheckScope core-local Local

    # No attach has been authorized during launch, browsing, sidebar toggles or refresh.
    # Commit two different terminals from Work, then switch back on a third, fresh channel.
    CommitTerminal t74 'local twin'
    $localWork += 't73 · new sibling'
    $state.refresh = $false
    (Pattern RefreshMachinesButton ([InvokePattern]::Pattern)).Invoke()
    [void](WaitFor 'live catalog refresh without losing the attachment' {
        !(FindId MachineGroup-core-extra) -and (FindId PaneHost).Current.Name -ceq 't74 · local twin' -and
        (TabsMatch $localWork 't74 · local twin') -and (StatusHidden)
    })
    CommitTerminal t76 'work sibling'
    CommitTerminal t74 'local twin'
    CheckNoAttachButtons
    if ($state.attaches -ne 3 -or $state.switchAcks -ne 2) { throw 'Incomplete attach/close sequence on original pipe' }
    $state.leavingSpace = $true
    (SpacePattern core-studio Work).Select(); CheckScope core-studio studio
    [void](WaitFor 'space change released only the viewer' { !$state.leavingSpace })
    $state.refresh = $true
    (Pattern RefreshMachinesButton ([InvokePattern]::Pattern)).Invoke()
    [void](WaitFor 'original gateway still connected after leaving the space' { FindId MachineGroup-core-extra })
    $window = $root.GetCurrentPattern([WindowPattern]::Pattern)
    foreach ($visualState in 'Maximized','Normal') {
        (Pattern Maximize ([InvokePattern]::Pattern)).Invoke()
        $height = if ($visualState -eq 'Maximized') { 32 } else { 40 }
        $action = if ($visualState -eq 'Maximized') { 'Restore' } else { 'Maximize' }
        [void](WaitFor "$visualState with a $height DIP caption and $action button" {
            $window.Current.WindowVisualState -eq $visualState -and (FindId Maximize).Current.Name -ceq $action -and
            (TabsInCaption 'studio/t74 · remote twin') -and (CaptionFits $height)
        })
    }
    (Pattern Minimize ([InvokePattern]::Pattern)).Invoke()
    [void](WaitFor 'caption minimized the window' { $window.Current.WindowVisualState -eq 'Minimized' })
    $window.SetWindowVisualState('Normal')
    [void](WaitFor 'restored full-height caption buttons' { $window.Current.WindowVisualState -eq 'Normal' -and (CaptionFits 40) })
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
'Machine/space sidebar and terminal tab switching smoke test passed.'
