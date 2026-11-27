#requires -Version 7.4
# Run on an interactive Windows desktop: ./MachineSpaces.ps1 -Exe C:\isolated\Porchlight.exe
using namespace System.Windows.Automation
using namespace System.IO.Pipes
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Exe, [ValidateRange(0,96)][int]$MeasureSwitchTabs = 0,
    [ValidateSet('Default','Select','Browse')][string]$Startup = 'Default', [switch]$CheckFocus, [switch]$CheckSidebarPixels)
$ErrorActionPreference = 'Stop'
$exePath = (Get-Item -LiteralPath $Exe).FullName
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
# Query the fixture HWND's non-client hit test; don't move the mouse or send clicks to the desktop.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class PorchlightChrome {
    [DllImport("user32")] public static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32")] static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    public static int Hit(IntPtr hwnd, int x, int y) =>
        (int)SendMessage(hwnd, 0x0084, IntPtr.Zero, (IntPtr)((x & 0xffff) | (y << 16)));
}
'@

. "$PSScriptRoot/Find-TerminalItem.ps1"

if ($MeasureSwitchTabs -and $CheckSidebarPixels) { throw 'Run the tab benchmark and sidebar pixel check separately.' }
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
    function PaintCells($rows, [bool]$wide = $false) {
        # Keep row one for the banner. Cyan marks the leading cells, magenta the body. Only a
        # fresh wide repaint can put yellow at the trailing marker; a narrow repaint uses green.
        $ink = if ($wide) { "255;255;0" } else { "0;255;0" }
        $row = "`e[48;2;0;255;255m  `e[48;2;255;0;255m`e[K`e[$($state.columns-2)G`e[48;2;${ink}m  "
        $fill = (2..$rows | ForEach-Object { "`e[$($_);1H$row" }) -join ''
        WriteFrame $active 0 ($utf8.GetBytes("$fill`e[0m`e[H"))
    }
    function WriteEvent($e) {
        $event = @{ method='event'; params=$e } | ConvertTo-Json -Depth 8 -Compress
        WriteFrame $eventChannel 0 ($utf8.GetBytes("$event`n"))
    }
    $eventChannel = 9; $nextChannel = 41; $studioSpaceName = 'Work'
    $targets = @{
        t74 = @{ address='t74'; name='local twin'; state='live'; argv=@('pwsh.exe'); cwd='C:\fixture\launch'; foreground=@{cwd='C:\fixture\Work\src'} }
        t75 = @{ address='t75'; title='π - ScopeJobs'; status=@{app='pi'}; state='live'; labels=@{'herdr.space'='sp2'} }
        t76 = @{ address='t76'; name='work sibling'; state='live'; labels=@{'herdr.space'='sp1'} }
        t77 = @{ address='t77'; name='loose first'; state='live' }
        t78 = @{ address='t78'; name='loose second'; state='live' }
        'core-studio/t74' = @{ address='studio/t74'; name='remote twin'; state='live' }
        'core-studio/t76' = @{ address='studio/t76'; name='remote sibling'; state='live'; labels=@{'herdr.space'='sp1'} }
    }
    foreach ($group in 1,2) { for ($i = 0; $i -lt $state.measureTabs; ++$i) {
        $id = 't{0}{1:d2}' -f $group,$i
        $targets[$id] = @{ address=$id; name="Tab $i"; state='live'; labels=@{'herdr.space'="bench-$group"} }
    } }
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
        framed=$true; methods=@('machine.list','events.subscribe','terminal.attach','terminal.kill','doc.get','machine.exec'); features=@() }))
    $writer.Write([byte]10)
    $header = [byte[]]::new(9); $active = 0; $holdingClose = $null; $subscribed = $false
    :messages while ($true) {
        # Keep reading while withholding the old channel's empty CLOSE. Sleeping in the peer
        # and blindly echoing CLOSE would let a pipelined attach sit unread and pass the test.
        $read = $pipe.ReadAsync($header, 0, $header.Length)
        if ($holdingClose -and !$read.Wait([Math]::Max(0, 500 - [int]$holdingClose.ElapsedMilliseconds))) {
            WriteFrame $active 2 ([byte[]]@())
            $active = 0; $state.activeAddress = $null; $holdingClose = $null
        }
        $got = $read.GetAwaiter().GetResult()
        if (!$got) { if ($state.closing -and !$active) { return }; throw "Unexpected disconnect (active channel: $active)" }
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
                    if ($state.closing -or $state.expectDetach) {
                        WriteFrame $active 2 ([byte[]]@())
                        $active = 0; $state.activeAddress = $null; $state.expectDetach = $false
                    } elseif ($state.nextTerminal) { $holdingClose = [Diagnostics.Stopwatch]::StartNew() }
                    else { throw "Unrequested CLOSE on channel $active" }
                }
                3 {
                    if ($n -ne 4) { throw 'Invalid terminal resize' }
                    $previous = $state.columns
                    $state.columns = [BitConverter]::ToUInt16($body, 0)
                    $state.resizeCount++
                    $wide = $state.columns -gt $previous
                    # On shrink, null leaves renderer flashes exposed; zero requests an immediate fixture repaint.
                    if ($state.paintCells -and ($wide -or $null -ne $state.repaintDelayMs)) {
                        if ($wide -and $state.repaintDelayMs) { [Threading.Thread]::Sleep([int]$state.repaintDelayMs) }
                        PaintCells ([BitConverter]::ToUInt16($body, 2)) $wide
                    }
                }
                4 { if ($n -ne 1 -or $body[0] -gt 1) { throw 'Invalid terminal focus' } }
                default { throw "Unexpected frame kind $kind on channel $channel" }
            }
            continue
        }
        if ($kind -ne 0) { throw "Unexpected RPC frame kind $kind" }
        $request = $utf8.GetString($body) | ConvertFrom-Json
        if ($state.switchesOnly -and $request.method -cne 'terminal.attach') { throw "Unexpected benchmark RPC: $($request.method)" }
        switch -CaseSensitive ($request.method) {
            'machine.list' {
                $machines = @(@{ id='core-studio'; name='studio'; state='connected' })
                if ($state.extraMachineState) { $machines += @{ id='core-extra'; name='extra'; state=$state.extraMachineState } }
                $result = @{ machines=$machines }
                switch ($state.catalogEdit) {
                    'retitle' {
                        $targets.t75.title = 'π - Porchlight'
                        WriteEvent @{ seq=80; type='title'; terminal='t75'; data=@{title=$targets.t75.title} }
                    }
                    'insert' {
                        if ($state.activeAddress -cne 't74') { throw 'Expected the local twin during catalog insertion' }
                        # Insert before the selected tab; preserve its address, not its index.
                        $targets.t73 = @{ address='t73'; name='new sibling'; state='live'; labels=@{'herdr.space'='sp1'} }
                        WriteEvent @{ seq=81; type='created'; terminal='t73'; data=$targets.t73 }
                        $targets.t74.foreground = @{argv=@('C:\tools\lazygit.exe'); cwd='C:\fixture\Work\src'}
                        WriteEvent @{ seq=82; type='foreground'; terminal='t74'; data=$targets.t74.foreground }
                    }
                    'finish-hidden' {
                        if ($state.activeAddress -cne 'core-studio/t76') { throw 'Expected the remote t76 while local t76 finishes' }
                        $targets.Remove('t76')
                        WriteEvent @{ seq=83; type='finished'; terminal='t76' }
                        $targets.t74.foreground = @{argv=@('pwsh.exe'); cwd='C:\fixture\Work\src'}
                        WriteEvent @{ seq=84; type='foreground'; terminal='t74'; data=$targets.t74.foreground }
                        $studioSpaceName = 'Renamed Work' # Same core and space ID in the next doc.get.
                    }
                }
                $state.catalogEdit = $null
            }
            'events.subscribe' {
                if ($subscribed) { throw 'Viewer subscribed a second time' }; $subscribed = $true
                $result = @{ channel=$eventChannel; seq=79; terminals=@($targets.Values) }
            }
            'terminal.attach' {
                $address = $request.params.terminal
                if ($active) { throw "Attach to $address before empty CLOSE acknowledgement on channel $active" }
                if (!$subscribed -or !$state.nextTerminal -or $address -cne $state.nextTerminal -or !$targets.ContainsKey($address)) {
                    throw "Unexpected terminal.attach: $address (authorized selection: '$($state.nextTerminal)')"
                }
                $active = $nextChannel++; $state.activeAddress = $address; $state.nextTerminal = $null
                $state.columns = $request.params.cols
                $result = @{ channel=$active; terminal=$targets[$address] }
            }
            'terminal.kill' {
                if (!$state.allowRemoteClose -or $request.params.terminal -cne 'core-studio/t74' -or !$active -or $state.activeAddress -cne $request.params.terminal) {
                    throw "Unexpected terminal.kill: $($request.params.terminal)"
                }
                $state.allowRemoteClose = $false
                if ($state.refuseClose) {
                    $state.refuseClose = $false
                    $errorReply = @{ jsonrpc='2.0'; id=$request.id; error=@{code=-32000; message='fixture refusal'} } | ConvertTo-Json -Compress
                    WriteFrame 0 0 ($utf8.GetBytes($errorReply)); continue messages
                }
                $targets.Remove('core-studio/t74')
                WriteEvent @{ seq=85; type='finished'; terminal='studio/t74'; data=@{reason='killed'} }
                WriteFrame $active 2 ($utf8.GetBytes('{"reason":"killed"}'))
                $active = 0; $state.activeAddress = $null
                $result = @{ address='studio/t74'; state='finished'; reason='killed' }
            }
            'doc.get' {
                if ($request.params.name -cne 'herdr-app') { throw 'Unexpected document request' }
                $work = @{ id='sp1'; name='Work'; tabs=@(@{ root=@{ type='block'; content=@{ terminal='t74' } } }) }
                $spaces = switch ($request.params.on) {
                    $null {
                        # First saved space still has a tab, but t70 is not in the live snapshot.
                        @{ id='sp3'; name='Archive'; tabs=@(@{root=@{type='block'; content=@{terminal='t70'}}}) }
                        @{ id='sp1'; name=''; folder=$HOME; tabs=$work.tabs }
                        @{ id='sp2'; name='Personal'; tabs=@() }
                    }
                    'core-studio' { $work.name = $studioSpaceName; $work.tabs[0].name = 'remote twin'; $work }
                    'core-extra' { }
                    default { throw 'Unexpected document target' }
                }
                $version = if ($request.params.on -ceq 'core-studio' -and $studioSpaceName -cne 'Work') { 2 } else { 1 }
                $result = @{ value=@{ spaces=@($spaces | Where-Object { $_ }) }; version=$version }
                if ($state.deferLocalLayout -and !$request.params.on) {
                    # The initial snapshot must be usable even when this optional reply is late.
                    $state.deferLocalLayout = $false; $deferredLayout = Reply $request.id $result
                    continue messages
                }
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
        if ($request.method -ceq 'terminal.attach') {
            WriteFrame $active 0 ($utf8.GetBytes("Fixture $address`r`n"))
            if ($state.paintCells) { PaintCells $request.params.rows }
            if ($deferredLayout) { WriteFrame 0 0 $deferredLayout; $deferredLayout = $null }
        }
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
function SidebarStatus {
    $status = FindId Status
    if ($status -and !$status.Current.IsOffscreen -and !$status.Current.BoundingRectangle.IsEmpty -and
        $status.Current.BoundingRectangle.Right -le (FindId WorkspaceFrame).Current.BoundingRectangle.Left) { $status.Current.Name }
}
function TabsInCaption($firstTabLabel) {
    $strip = FindId TerminalTabs
    $tabs = $strip.Current.BoundingRectangle
    $minimize = (FindId Minimize).Current.BoundingRectangle
    $button = (FindId SidebarToggle).Current.BoundingRectangle
    if (!$tabs -or $tabs.IsEmpty -or !$minimize -or $minimize.IsEmpty -or !$button -or $button.IsEmpty) { return $false }
    $scale = [PorchlightChrome]::GetDpiForWindow($hwnd) / 96.0
    if ([PorchlightChrome]::Hit($hwnd, $button.Left + $button.Width / 2, $button.Top + $button.Height / 2) -ne 1) { return $false }
    $y = [int]($minimize.Top + $minimize.Height / 2)
    if ($tabs.Top -ge $y -or $tabs.Bottom -le $y -or $tabs.Right -gt $minimize.Left + 1) { return $false }
    if ([PorchlightChrome]::Hit($hwnd, $minimize.Left + $minimize.Width / 2, $y) -ne 8) { return $false } # HTMINBUTTON
    $open = $toggle.Current.ToggleState -eq 'On'
    if ($open -and [PorchlightChrome]::Hit($hwnd, ($button.Right + $tabs.Left) / 2, $y) -ne 2) { return $false } # HTCAPTION above the sidebar
    if ([PorchlightChrome]::Hit($hwnd, $tabs.Right - 8, $y) -ne 2) { return $false } # HTCAPTION in the footer
    if (!$firstTabLabel) { return $true }
    $tab = (Find-TerminalItem $strip $viewer.Id $firstTabLabel).Current.BoundingRectangle
    $frame = (FindId WorkspaceFrame).Current.BoundingRectangle
    if (!$frame -or $frame.IsEmpty -or !$tab -or $tab.IsEmpty) { return $false }
    # With the pane closed, the frame starts at the left client edge: mirror the toggle's outer gap.
    $expectedLeft = if ($open) { $frame.Left } else { $button.Right + ($button.Left - $frame.Left) }
    if ($open -and ([PorchlightChrome]::Hit($hwnd, $expectedLeft - 1, $y) -ne 2 -or
                    [PorchlightChrome]::Hit($hwnd, $expectedLeft + 1, $y) -ne 1)) { return $false }
    [Math]::Abs($tab.Left - $expectedLeft) -le 1 -and $tab.Top -lt $y -and $tab.Bottom -gt $y -and
        [Math]::Abs($button.Top - $tab.Top) -le $scale -and $tabs.Height -le $tab.Height + 9 * $scale -and
        [PorchlightChrome]::Hit($hwnd, $tab.Left + $tab.Width / 2, $y) -eq 1 # HTCLIENT on the tab itself
}
# UIA bounds include the visible feet. Anchor both sides to the close button inside the same
# selected tab: this distinguishes aligning the fill from aligning the tip, without assuming theme colors.
function SelectedTabSides($label) {
    $item = Find-TerminalItem (FindId TerminalTabs) $viewer.Id $label
    if (!$item) { return }
    $close = $item.FindFirst('Descendants', [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::Button))
    $tab = $item.Current.BoundingRectangle; $glyph = $close.Current.BoundingRectangle
    if ($glyph -and !$glyph.IsEmpty -and !$tab.IsEmpty) {
        [pscustomobject]@{ Left=$glyph.Left - $tab.Left; Right=$tab.Right - $glyph.Right }
    }
}
# Sample the moving control, not just the two settled layouts. An inline SplitView can give
# the terminal its final width and then translate it a whole sidebar-width off its right edge.
function ToggleSidebarWithViewer {
    $frame = FindId WorkspaceFrame
    $term = (FindId PaneHost).FindFirst('Descendants', [PropertyCondition]::new([AutomationElement]::ClassNameProperty, 'TermControl'))
    $before = $frame.Current.BoundingRectangle; $right = $term.Current.BoundingRectangle.Right
    $resizes = $state.resizeCount; $columns = $state.columns; $closingSidebar = $toggle.Current.ToggleState -eq 'On'
    $toggle.Toggle()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        $bounds = $frame.Current.BoundingRectangle
        if ([Math]::Abs($bounds.Right - $before.Right) -gt 1 -or [Math]::Abs($term.Current.BoundingRectangle.Right - $right) -gt 1) {
            throw 'Sidebar toggle moved the right edge of the workspace or its terminal'
        }
        Start-Sleep -Milliseconds 5
    } while ($clock.ElapsedMilliseconds -lt 450)
    [void](WaitFor 'sidebar grid resize' { $state.resizeCount -gt $resizes })
    if (($state.resizeCount - $resizes) -ne 1) { throw 'Expected one grid resize, not a resize for each animation frame' }
    $direction = if ($closingSidebar) { 1 } else { -1 }
    if (($before.Left - $bounds.Left) * $direction -le 0 -or ($state.columns - $columns) * $direction -le 0) {
        throw 'The left workspace edge or terminal column count did not follow the sidebar'
    }
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
# Expected wire routes and their UI labels are deliberately distinct on the relayed core.
$localWork = @{ t74='t74 · local twin · pwsh'; t76='t76 · work sibling' }
$remoteWork = @{ 'core-studio/t74'='studio/t74 · remote twin'; 'core-studio/t76'='studio/t76 · remote sibling' }
$unassigned = @{ t77='t77 · loose first'; t78='t78 · loose second' }
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
function SelectSpace($machine, $space, $terminals, $address, [switch]$focusRefresh) {
    $selection = SpacePattern $machine $space
    if ($terminals.Count) { AuthorizeAttach $address }
    else { $state.expectDetach = [bool]$state.activeAddress } # Empty destination: CLOSE only if leaving a viewer.
    if ($CheckFocus) { (SpaceItem $machine $space).SetFocus() } # Start from the same focus owner as a sidebar click.
    $selection.Select()
    if ($focusRefresh) { (FindId RefreshMachinesButton).SetFocus() } # The peer is still holding the old channel's CLOSE.
    CheckScope $machine $space $terminals $address
    if ($focusRefresh) {
        if (!(FindId RefreshMachinesButton).Current.HasKeyboardFocus) { throw 'The new terminal took focus back from Refresh.' }
    } elseif ($CheckFocus -and $address) { CheckTerminalFocus $address }
}
function CheckLocalOrder {
    $items = (FindId Spaces-core-local).FindAll('Children', [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::ListItem))
    $names = ($items | ForEach-Object { $_.Current.Name }) -join ', '
    if ($names -cne 'Archive, Work, Personal, Unassigned') { throw "Wrong saved space order: $names" }
}
function CheckNoAttachButtons {
    foreach ($id in 'Attach','ConnectionButton','Disconnect') { if (FindId $id) { throw "Obsolete control: $id" } }
    foreach ($name in 'Attach','Disconnect') {
        if ($root.FindFirst('Descendants', [AndCondition]::new(
            [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::Button),
            [PropertyCondition]::new([AutomationElement]::NameProperty, $name)))) { throw "Obsolete button: $name" }
    }
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

function CheckMachineSpaces {
    $toggle = Pattern SidebarToggle ([TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() } # Narrow windows start collapsed.
    CheckScope core-local Work $localWork $startupTerminal # No manual tab selection at launch.
    if ($MeasureSwitchTabs) {
        # Time selection to the first laid-out destination header, including UI Automation overhead.
        # Allow one viewer per selection. Settle it outside the sample; the first pair warms both spaces.
        $spaces = @(SpacePattern core-local bench-1; SpacePattern core-local bench-2)
        $strip = FindId TerminalTabs
        $state.switchesOnly = $true
        $times = for ($i = 0; $i -lt 14; $i++) {
            $which = $i % 2; $address = "t$($which+1)00"
            $match = [PropertyCondition]::new([AutomationElement]::AutomationIdProperty, "TerminalTab-$address")
            AuthorizeAttach $address
            $clock = [Diagnostics.Stopwatch]::StartNew()
            $spaces[$which].Select()
            do {
                $tab = $strip.FindFirst('Descendants', $match)
                if ($tab -and !$tab.Current.BoundingRectangle.IsEmpty -and !$tab.Current.IsOffscreen) { break }
                if ($clock.Elapsed.TotalSeconds -gt 5) {
                    $items = $strip.FindAll('Descendants', [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::TabItem))
                    throw "Missing destination in switch $i; tabs: $(($items | ForEach-Object { $_.Current.Name + ' [' + $_.Current.AutomationId + ']' }) -join ', ')"
                }
            } while ($true)
            $clock.Stop()
            if ($i -ge 2) { $clock.Elapsed.TotalMilliseconds }
            [void](WaitFor "benchmark viewer $address" {
                (PaneMatches $address "$address · Tab 0") -and (StatusHidden) -and
                    $tab.GetCurrentPattern([SelectionItemPattern]::Pattern).Current.IsSelected
            })
        }
        $sorted = @($times | Sort-Object)
        $state.closing = $true
        $root.GetCurrentPattern([WindowPattern]::Pattern).Close()
        if (!$viewer.WaitForExit(5000) -or $viewer.ExitCode -ne 0) { throw 'Benchmark viewer did not close normally' }
        if (!(Wait-Job $peer -Timeout 5)) { throw 'Benchmark peer did not disconnect' }; Receive-Job $peer
        return [pscustomobject]@{ TabsPerSpace=$MeasureSwitchTabs; Switches=$times.Count;
            MedianMs=[math]::Round(($sorted[5]+$sorted[6])/2,1); MaxMs=[math]::Round($sorted[-1],1) }
    }
    CheckNoAttachButtons; CheckLocalOrder
    [void](WaitFor 'full-height windowed caption buttons' { CaptionFits 40 })
    if ((FindId WindowLabel) -or (FindId ScopeLabel)) { throw 'Duplicate title or machine/space label in the title bar' }
    if (!(IsExpanded core-local) -or !(IsExpanded core-studio)) { throw 'Machines must start expanded' }
    if ((FindId MachineGroup-core-local).Current.Name -cne 'Local, connected') { throw 'Missing accessible machine name or state' }
    if ((FindId CoreLabel).Current.Name -cne 'HerdR smoke · core core-local') { throw 'Missing gateway identity' }
    if (FindId StateLabel) { throw 'Connected machines should not have a state caption' }
    if (FindId PipePath) { throw 'The main pane still contains a pipe editor' }
    foreach ($expanded in $false,$true) {
        ExpandGroup core-studio $expanded; CheckScope core-local Work $localWork $startupTerminal
    }
    SelectSpace core-local Archive @{}
    [void](WaitFor 'draggable title bar above the open sidebar and the empty workspace' { TabsInCaption })
    # Check first-tab alignment with a strip that fits. WinUI adds scroll buttons before overflowing tabs.
    $personal = @{ t75='t75 · π - ScopeJobs' }
    SelectSpace core-local Personal $personal t75
    # These headers round in opposite directions at 200% DPI. UIA includes the visible corner in the tab bounds.
    [void](WaitFor 'ScopeJobs tab corner at the workspace edge' { TabsInCaption $personal.t75 })
    $state.catalogEdit = 'retitle'
    (Pattern RefreshMachinesButton ([InvokePattern]::Pattern)).Invoke()
    $personal.t75 = 't75 · π - Porchlight'
    CheckScope core-local Personal $personal t75
    [void](WaitFor 'same leading corner after the selected tab is retitled' { TabsInCaption $personal.t75 })

    $openSides = WaitFor 'selected tab outline with the sidebar open' { SelectedTabSides $personal.t75 }
    $toggleBounds = (FindId SidebarToggle).Current.BoundingRectangle
    ToggleSidebarWithViewer
    [void](WaitFor 'hidden sidebar with toggle unmoved' {
        $group = FindId MachineGroup-core-studio
        $toggle.Current.ToggleState -eq 'Off' -and (!$group -or $group.Current.IsOffscreen) -and
        $toggleBounds.Equals((FindId SidebarToggle).Current.BoundingRectangle) -and (TabsInCaption $personal.t75)
    })
    [void](WaitFor 'closing the sidebar restores only the 4-DIP left foot' {
        $sides = SelectedTabSides $personal.t75
        $scale = [PorchlightChrome]::GetDpiForWindow($hwnd) / 96.0
        $sides -and [Math]::Abs(($sides.Left - $openSides.Left) - 4 * $scale) -le 1 -and
            [Math]::Abs($sides.Right - $openSides.Right) -le 1
    })
    if ($CheckSidebarPixels) {
        $timings = & "$PSScriptRoot/Test-SidebarPixels.ps1" -Hwnd $hwnd -Frame (FindId WorkspaceFrame) -Toggle $toggle `
            -State $state -Instant:$Instant -Baseline $Baseline
        if ($Instant) { CloseFixture; return $timings }
    }
    ToggleSidebarWithViewer
    [void](WaitFor 'visible sidebar with toggle unmoved' {
        $group = FindId MachineGroup-core-studio
        $toggle.Current.ToggleState -eq 'On' -and $group -and !$group.Current.IsOffscreen -and
        $toggleBounds.Equals((FindId SidebarToggle).Current.BoundingRectangle)
    }); CheckScope core-local Personal $personal t75
    [void](WaitFor 'tabs in the caption row, to the right of the open sidebar' {
        $sidebar = (FindId MachineBrowser).Current.BoundingRectangle
        $tabs = (FindId TerminalTabs).Current.BoundingRectangle
        $sidebar -and $tabs -and !$sidebar.IsEmpty -and !$tabs.IsEmpty -and
        $sidebar.Width -gt 0 -and $tabs.Width -gt 0 -and $sidebar.Right -le $tabs.Left -and (TabsInCaption $personal.t75)
    })

    $window = $root.GetCurrentPattern([WindowPattern]::Pattern)
    foreach ($visualState in 'Maximized','Normal') {
        (Pattern Maximize ([InvokePattern]::Pattern)).Invoke()
        $height = if ($visualState -eq 'Maximized') { 32 } else { 40 }
        $action = if ($visualState -eq 'Maximized') { 'Restore' } else { 'Maximize' }
        [void](WaitFor "$visualState with a $height DIP caption and $action button" {
            $window.Current.WindowVisualState -eq $visualState -and (FindId Maximize).Current.Name -ceq $action -and
            (TabsInCaption $personal.t75) -and (CaptionFits $height)
        })
    }
    (Pattern Minimize ([InvokePattern]::Pattern)).Invoke()
    [void](WaitFor 'caption minimized the window' { $window.Current.WindowVisualState -eq 'Minimized' })
    $window.SetWindowVisualState('Normal')
    [void](WaitFor 'restored full-height caption buttons' { $window.Current.WindowVisualState -eq 'Normal' -and (CaptionFits 40) })
    if ($toggle.Current.ToggleState -ne 'On') { $toggle.Toggle() } # Minimizing can collapse the sidebar.

    SelectSpace core-studio Work $remoteWork core-studio/t74
    ExpandGroup core-local $false
    $state.extraMachineState = 'needs_login'
    (Pattern RefreshMachinesButton ([InvokePattern]::Pattern)).Invoke()
    [void](WaitFor 'unavailable machine with a specific reason' {
        (FindId MachineGroup-core-extra).Current.Name -ceq 'extra, needs login' -and (FindId StateLabel).Current.Name -ceq 'needs login'
    })
    if (!(IsExpanded core-extra) -or (IsExpanded core-local)) { throw 'Refresh reset expansion state' }
    ExpandGroup core-local $true; CheckScope core-studio Work $remoteWork core-studio/t74; CheckLocalOrder
    CommitTerminal $remoteWork core-studio/t76
    # The local startup choice is independent of studio's t76. Browse left no remembered tab.
    SelectSpace core-local Work $localWork ($startupTerminal ?? 't74')
    if ($startupTerminal -ceq 't76') { CommitTerminal $localWork t74 }
    [void](WaitFor 'selected first tab aligned with the workspace' { TabsInCaption $localWork.t74 })
    $localWork.t74 = 't74 · local twin · Git Changes'; $localWork.t73 = 't73 · new sibling'
    $state.extraMachineState = $null; $state.catalogEdit = 'insert'
    (Pattern RefreshMachinesButton ([InvokePattern]::Pattern)).Invoke()
    [void](WaitFor 'removed extra machine' { !(FindId MachineGroup-core-extra) })
    CheckScope core-local Work $localWork t74 # Inserted t73 and renamed t74; no new viewer.
    CommitTerminal $localWork t76
    CommitTerminal $localWork t74
    CheckNoAttachButtons

    # Remember non-first tabs independently, including the null space ID and two cores with identical sp1/t74/t76 IDs.
    SelectSpace core-local Unassigned $unassigned t77
    CommitTerminal $unassigned t78
    SelectSpace core-local Work $localWork t74 -focusRefresh:$CheckFocus
    SelectSpace core-local Unassigned $unassigned t78
    SelectSpace core-local Archive @{} # The active viewer must detach, without attaching a neighbor.
    SelectSpace core-studio Work $remoteWork core-studio/t76
    SelectSpace core-local Work $localWork t74
    CommitTerminal $localWork t76
    SelectSpace core-studio Work $remoteWork core-studio/t76 # Same terminal ID, different viewer/route.

    $state.extraMachineState = 'connected'; $state.catalogEdit = 'finish-hidden'
    (Pattern RefreshMachinesButton ([InvokePattern]::Pattern)).Invoke()
    [void](WaitFor 'original gateway still connected after leaving the space' { FindId MachineGroup-core-extra })
    CheckScope core-studio 'Renamed Work' $remoteWork core-studio/t76 # Same viewer through the sidebar rename.
    # Even a reused strip must drop finished local t76 and show t74's new title. Fall back to first live t73.
    $localWork.Remove('t76'); $localWork.t74 = 't74 · local twin · pwsh'
    SelectSpace core-local Work $localWork t73
    SelectSpace core-studio 'Renamed Work' $remoteWork core-studio/t76
    CommitTerminal $remoteWork core-studio/t74
    # The native close glyph ends the addressed terminal on its own core, not the window or the local twin.
    $remoteLabel = $remoteWork['core-studio/t74']
    $remoteTab = Find-TerminalItem (FindId TerminalTabs) $viewer.Id $remoteLabel
    $glyph = WaitFor 'close glyph on selected tab' {
        $remoteTab.FindFirst('Descendants', [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::Button))
    }
    $state.refuseClose = $true; $state.allowRemoteClose = $true
    $glyph.GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    [void](WaitFor 'refused close kept the terminal and its viewer' {
        !$state.allowRemoteClose -and (SidebarStatus) -like 'Could not confirm closing studio/t74.*' -and
        (PaneMatches core-studio/t74 $remoteLabel) -and (TabsMatch $remoteWork.Values $remoteLabel)
    })
    $state.allowRemoteClose = $true
    $glyph.GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    [void](WaitFor 'remote terminal closed without closing the window or attaching its sibling' {
        !$state.allowRemoteClose -and (PaneMatches $null '') -and (TabsMatch $remoteWork['core-studio/t76'] '') -and
            (SidebarStatus) -ceq 'Program killed.'
    })
    SelectSpace core-local Work $localWork t73
    CloseFixture
}

function CloseFixture {
    $state.closing = $true
    (Pattern Close ([InvokePattern]::Pattern)).Invoke()
    if (!$viewer.WaitForExit(5000)) { throw 'Viewer did not close normally' }
    if ($viewer.ExitCode -ne 0) { throw "Viewer exit code $($viewer.ExitCode)" }
    if (!(Wait-Job $peer -Timeout 5)) { throw 'Fixture did not see viewer disconnect' }
    Receive-Job $peer
}

function RunFixture([switch]$Instant, $Baseline) {
    # Bind before starting the viewer. One hello and one subscription on this pipe; no real core or program.
    $name = 'porchlight-smoke-' + [guid]::NewGuid().ToString('N')
    $pipe = [NamedPipeServerStream]::new($name, 'InOut', 1, 'Byte', 'Asynchronous,CurrentUserOnly')
    $state = [hashtable]::Synchronized(@{ extraMachineState=$null; catalogEdit=$null; closing=$false; expectDetach=$false
        nextTerminal=$null; activeAddress=$null; resizeCount=0; columns=0; paintCells=$CheckSidebarPixels.IsPresent; measureTabs=$MeasureSwitchTabs; deferLocalLayout=($Startup -eq 'Select') })
    $viewer = $peer = $root = $null
    try {
        $start = [Diagnostics.ProcessStartInfo]::new($exePath); $start.UseShellExecute = $false
        $start.Environment['HERDR_APP_HOME'] = Join-Path ([IO.Path]::GetTempPath()) $name # private, no programs.json overrides
        $start.ArgumentList.Add("--pipe=\\.\pipe\$name")
        if ($Instant) { $start.ArgumentList.Add('--no-sidebar-animation') }
        if ($Startup -ne 'Default') { $start.ArgumentList.Add('--select=t76') }
        if ($Startup -eq 'Browse') { $start.ArgumentList.Add('--browse') }
        $startupTerminal = switch ($Startup) { 'Default' { 't74' } 'Select' { 't76' } 'Browse' { $null } }
        if ($startupTerminal) { AuthorizeAttach $startupTerminal }
        $viewer = [Diagnostics.Process]::Start($start)
        $hwnd = WaitFor 'viewer HWND' { $viewer.Refresh(); if ($viewer.MainWindowHandle -ne [IntPtr]::Zero) { $viewer.MainWindowHandle } }
        $root = [AutomationElement]::FromHandle($hwnd)
        if ($root.Current.ProcessId -ne $viewer.Id) { throw 'Wrong HWND owner' }
        if ($CheckFocus) {
            # Allow startup auto-selection only after the fixture owns keyboard focus, not in a background window.
            [void](WaitFor 'foreground fixture window for keyboard focus checks' {
                [PorchlightChrome]::GetForegroundWindow() -eq $hwnd -or [PorchlightChrome]::SetForegroundWindow($hwnd)
            })
        }
        $peer = Start-ThreadJob -ScriptBlock $serve -ArgumentList $pipe,$state
        if ($CheckFocus -and $startupTerminal) { CheckTerminalFocus $startupTerminal } # No click or tab switch at launch.
        CheckMachineSpaces
    } finally {
        try {
            if ($viewer -and !$viewer.HasExited) { $viewer.Kill(); $viewer.WaitForExit() }
        } finally {
            $pipe.Dispose(); if ($peer) { Remove-Job $peer -Force }; if ($viewer) { $viewer.Dispose() }
        }
    }
}

$reference = if ($CheckSidebarPixels) { RunFixture -Instant }
RunFixture -Baseline $reference
if (!$MeasureSwitchTabs) { 'Machine/space sidebar and terminal tab switching smoke test passed.' }
