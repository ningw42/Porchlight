# Run from an interactive x64 Windows desktop (PowerShell 7). Never accepts a terminal ID.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Pipe, [ValidateSet('Debug','Release')][string]$Configuration = 'Debug', [string]$WindowsTerminalSettings)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path "$PSScriptRoot/../../src/Porchlight.App/bin/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64/Porchlight.exe").Path
$program = (Resolve-Path "$PSScriptRoot/program.py").Path
$python = (Get-Command python.exe -CommandType Application | Select-Object -First 1).Source
$prefix = '\\.\pipe\'
if (!$Pipe.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or $Pipe.Substring($prefix.Length) -notmatch '^[^/:\\]+$') { throw 'Supply the exact existing local core pipe.' }
$nonce = [Guid]::NewGuid().ToString('N'); $name = 'pl-smoke-' + $nonce.Substring(0,16)
$wire = [IO.Pipes.NamedPipeClientStream]::new('.', $Pipe.Substring($prefix.Length), 'InOut','Asynchronous,CurrentUserOnly','Identification')
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
. "$PSScriptRoot/Find-TerminalItem.ps1"
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PorchlightSmokeWindow {
  [DllImport("user32")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint owner);
  [DllImport("user32")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32")] public static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int width,int height,uint flags);
}
'@
$sequence = 0; $id = $null; $viewer = $null
function Call($method,$data) {
    $script:sequence++; $msg = @{jsonrpc='2.0';id=$sequence;method=$method;params=$data} | ConvertTo-Json -Depth 8 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes("$msg`n"); $wire.Write($bytes); $wire.Flush()
    $response = $reader.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(8)).GetAwaiter().GetResult() | ConvertFrom-Json -Depth 64
    if ($response.id -ne $sequence -or $response.error) { throw "Core refused $method. No retry or replacement." }
    $response.result
}
function Owned {
    $r = Call terminal.get @{terminal=$id}
    if ($r.address -cne $id -or $r.name -cne $name -or $r.labels.'porchlight-smoke' -cne $nonce -or $r.created_ms -ne $created) { throw 'Ownership check failed; refusing to act.' }
    $r
}
function Await($description,[scriptblock]$condition) {
    $until = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $v = & $condition; if ($v) { return $v }
        if ($viewer -and $viewer.HasExited) { throw "Viewer exited awaiting $description (code $($viewer.ExitCode))" }
        Start-Sleep -Milliseconds 80
    } while ([DateTime]::UtcNow -lt $until)
    throw "Timed out awaiting $description"
}
function Find($property,$value) {
    $root.FindFirst('Descendants',[Windows.Automation.PropertyCondition]::new($property,$value))
}
function CheckOwner([IntPtr]$h) {
    [uint32]$owner = 0
    if ($h -eq [IntPtr]::Zero -or ![PorchlightSmokeWindow]::GetWindowThreadProcessId($h,[ref]$owner) -or $owner -ne $viewer.Id) { throw 'Wrong HWND owner.' }
}
function SendFixtureKey([int]$vk) {
    $site = Find ([Windows.Automation.AutomationElement]::ClassNameProperty) 'InputSiteWindowClass'
    $child = [IntPtr]$site.Current.NativeWindowHandle; CheckOwner $child
    if (![PorchlightSmokeWindow]::PostMessage($child,0x100,$vk,1) -or ![PorchlightSmokeWindow]::PostMessage($child,0x101,$vk,0xc0000001L)) { throw 'Addressed key failed.' }
}
function FrameState($count) {
    # Called ONLY after the window has confirmed both the explicitly selected core and our new terminal.
    $text = $pane.GetCurrentPattern([Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(-1)
    if ($text.Contains("smoke $nonce") -and $text -match "fixture (\d+) grid (\d+x\d+) accepted $count\b") {
        return @{pid=$Matches[1];grid=$Matches[2]}
    }
}
function CloseViewer {
    if ($viewer -and !$viewer.HasExited) {
        CheckOwner $viewer.MainWindowHandle
        if (!$viewer.CloseMainWindow() -or !$viewer.WaitForExit(6000)) { throw 'Viewer did not detach and close.' }
    }
    if ($viewer.ExitCode -ne 0) { throw "Unexpected viewer exit $($viewer.ExitCode)" }
}
try {
    $wire.Connect(2000); $reader = [IO.StreamReader]::new($wire,[Text.UTF8Encoding]::new($false),$false,4096,$true)
    $hello = Call hello @{generation=1}
    if ($hello.generation -ne 1 -or !$hello.core.id -or 'events.subscribe' -notin $hello.methods -or 'terminal.attach' -notin $hello.methods) { throw 'Incompatible core.' }
    $existing = @((Call terminal.list @{}).terminals)
    if ($name -in $existing.name) { throw 'Name collision.' }
    "Core $($hello.core.name) $($hello.version) [$($hello.core.id)] at $Pipe; listed metadata only."
    $record = Call terminal.run @{name=$name;argv=@($python,'-B',$program,$nonce);labels=@{'porchlight-smoke'=$nonce};no_parent=$true;no_notify=$true;wait=$false;keep=$false}
    if ($record.address -cnotmatch '\At[1-9][0-9bcdfghjkmnpqrstvwxyz]*\z' -or $record.address -in $existing.address -or $record.name -cne $name -or $record.labels.'porchlight-smoke' -cne $nonce) { throw 'Unexpected new-terminal identity.' }
    $id=$record.address; $created=$record.created_ms; "Created $id ($name)."
    $aid = [Windows.Automation.AutomationElement]::AutomationIdProperty
    foreach ($pass in 1,2) {
        $r=Owned; if ($r.state -ne 'live') { throw 'Fixture is not live.' }
        $psi=[Diagnostics.ProcessStartInfo]::new($exe); $psi.UseShellExecute=$false
        $psi.ArgumentList.Add('--browse') # Verify core and fixture ownership in the window before any attach.
        $psi.ArgumentList.Add("--pipe=$Pipe"); $psi.ArgumentList.Add("--select=$id")
        if ($WindowsTerminalSettings) { $psi.ArgumentList.Add("--wt-font=$WindowsTerminalSettings") }
        $viewer=[Diagnostics.Process]::Start($psi)
        $hw = Await 'Porchlight window' { $viewer.Refresh(); if ($viewer.MainWindowHandle -ne [IntPtr]::Zero) { $viewer.MainWindowHandle } }
        CheckOwner $hw; $root=[Windows.Automation.AutomationElement]::FromHandle($hw)
        $sidebar=(Await 'sidebar toggle' { Find $aid SidebarToggle }).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
        if ($sidebar.Current.ToggleState -ne 'On') { $sidebar.Toggle() } # The core identity must be visible.
        $label="$id · $name"
        $item=Await 'exact core and unselected fixture tab' {
            $c=Find $aid CoreLabel; $tabs=Find $aid TerminalTabs
            if ($c -and $c.Current.Name.EndsWith("core $($hello.core.id)") -and $tabs.Current.IsEnabled) {
                $tab=Find-TerminalItem $tabs $viewer.Id $label
                if ($tab -and !$tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) { $tab }
            }
        }
        if ($item.Current.ProcessId -ne $viewer.Id -or (Owned).state -ne 'live' -or
            !(Find $aid CoreLabel).Current.Name.EndsWith("core $($hello.core.id)")) { throw 'Fixture or window identity changed; refusing selection.' }
        $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
        $pane=Await 'selected tab and its attached native pane' {
            $tab=Find-TerminalItem (Find $aid TerminalTabs) $viewer.Id $label
            $paneHost=Find $aid PaneHost
            if ($paneHost.Current.Name -ceq $label -and $tab -and $tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected) {
                $paneHost.FindFirst('Descendants', [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ClassNameProperty, 'TermControl'))
            }
        }
        $initial=Await 'program challenge in the native buffer' { FrameState ($pass-1) }
        if ($pass -eq 1) {
            $fixturePid=$initial.pid; $pane.SetFocus(); SendFixtureKey 75
            $before=Await 'accepted key and settled program grid' {
                $f=FrameState 1; $meta=Owned
                if ($f -and $f.grid -eq "$($meta.cols)x$($meta.rows)") { $f }
            }
            CheckOwner $hw
            if (![PorchlightSmokeWindow]::SetWindowPos($hw,0,0,0,1060,800,0x16)) { throw 'Resize failed.' }
            $resized=Await 'changed program grid' {
                $f=FrameState 1; $meta=Owned
                if ($f -and $f.grid -ne $before.grid -and $f.grid -eq "$($meta.cols)x$($meta.rows)") { $f }
            }
            CloseViewer
            if ((Owned).state -ne 'live') { throw 'Closing the window ended the program.' }
            "First window: program PID $fixturePid accepted key, grid $($before.grid) -> $($resized.grid); closed; still live."
        } else {
            if ($initial.pid -ne $fixturePid) { throw 'New process on reopen.' }
            $pane.SetFocus(); SendFixtureKey 81
            [void](Await 'explicit program exit' { (Find $aid Status).Current.Name -like 'Program exited (code 0).*' }); CloseViewer
            "Second window: same PID and accepted count repainted; explicit exit code 0."
        }
    }
} finally {
    if ($viewer -and !$viewer.HasExited) { try { CloseViewer } catch { Write-Warning 'Close the test viewer window manually.' } }
    try {
        if ($id) {
            if ((Owned).state -eq 'live') { [void](Call terminal.kill @{terminal=$id}) }
            if ((Owned).state -ne 'finished') { throw 'Fixture not finished; refusing removal.' }
            [void](Call terminal.rm @{terminal=$id}); "Removed only $id ($name)."
        }
    } finally { if ($reader) { $reader.Dispose() }; $wire.Dispose() }
}
