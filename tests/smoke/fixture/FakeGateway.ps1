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
    $script:eventSeq++
    $e.seq = $script:eventSeq
    $event = @{ method='event'; params=$e } | ConvertTo-Json -Depth 8 -Compress
    WriteFrame $eventChannel 0 ($utf8.GetBytes("$event`n"))
}
$script:eventSeq = 79; $eventChannel = 9; $nextChannel = 41; $studioSpaceName = 'Work'
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
    # A test command can arrive on an idle pipe. Keep the same outstanding read and a single writer.
    while ([Threading.Tasks.Task]::WhenAny($state.catalogEdit.Task, $read).GetAwaiter().GetResult() -ne $read) {
        $edit = $state.catalogEdit.Task.Result
        $state.catalogEdit = [Threading.Tasks.TaskCompletionSource[string]]::new()
        switch ($edit) {
            'cursor-probe' {
                if (!$active -or $probe -or $state.cursorProbe.address -cne $state.activeAddress) { throw 'No matching attachment for cursor probe' }
                $probe = $state.cursorProbe; $remainingReply = $probe.expected
                $bytes = $utf8.GetBytes($probe.query); $probeCredit = $bytes.Length
                WriteFrame $active 0 $bytes
            }
            'retitle' {
                $targets.t75.title = 'π - Porchlight'
                WriteEvent @{ type='title'; terminal='t75'; data=@{title=$targets.t75.title} }
            }
            'insert' {
                if ($state.activeAddress -cne 't74') { throw 'Expected the local twin during catalog insertion' }
                # Insert before the selected tab; preserve its address, not its index.
                $targets.t73 = @{ address='t73'; name='new sibling'; state='live'; labels=@{'herdr.space'='sp1'} }
                WriteEvent @{ type='created'; terminal='t73'; data=$targets.t73 }
                $targets.t74.foreground = @{argv=@('C:\tools\lazygit.exe'); cwd='C:\fixture\Work\src'}
                WriteEvent @{ type='foreground'; terminal='t74'; data=$targets.t74.foreground }
            }
            'finish-hidden' {
                if ($state.activeAddress -cne 'core-studio/t76') { throw 'Expected the remote t76 while local t76 finishes' }
                $targets.Remove('t76')
                WriteEvent @{ type='finished'; terminal='t76' }
                $targets.t74.foreground = @{argv=@('pwsh.exe'); cwd='C:\fixture\Work\src'}
                WriteEvent @{ type='foreground'; terminal='t74'; data=$targets.t74.foreground }
                $studioSpaceName = 'Renamed Work' # Same core and space ID in the next doc.get.
                WriteEvent @{ type='doc'; machine='studio'; data=@{name='herdr-app'} }
            }
            default { throw "Unexpected fixture command: $edit" }
        }
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
            0 {
                # No automatic-input capability advertised. Only the exact pending reply is legal DATA.
                $part = $utf8.GetString($body)
                if (!$n -or !$remainingReply -or !$remainingReply.StartsWith($part, [StringComparison]::Ordinal)) {
                    throw "Unexpected terminal DATA on channel $active (hex: $([Convert]::ToHexString($body)))"
                }
                $remainingReply = $remainingReply.Substring($part.Length)
            }
            1 {
                if ($n -ne 4) { throw 'Invalid terminal credit' }
                if ($probe -and [BitConverter]::ToUInt32($body, 0) -eq $probeCredit) { $probeCredit = 0 }
            }
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
        # Credit follows synchronous render, even for an unfinished CSI with no reply of its own.
        if ($probe -and !$probeCredit -and !$remainingReply) { $probe.done.SetResult($true); $probe = $null }
        continue
    }
    if ($kind -ne 0) { throw "Unexpected RPC frame kind $kind" }
    $request = $utf8.GetString($body) | ConvertFrom-Json
    if ($state.switchesOnly -and $request.method -cne 'terminal.attach') { throw "Unexpected benchmark RPC: $($request.method)" }
    switch -CaseSensitive ($request.method) {
        'machine.list' {
            if ($listed -or $subscribed) { throw 'Expected machine.list only once at startup' }; $listed = $true
            $result = @{ machines=@(@{ id='core-studio'; name='studio'; state='connected' },
                                    @{ id='core-extra'; name='extra'; state='needs_login' }) }
        }
        'events.subscribe' {
            if ($subscribed) { throw 'Viewer subscribed a second time' }; $subscribed = $true
            $result = @{ channel=$eventChannel; seq=$script:eventSeq; terminals=@($targets.Values) }
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
            WriteEvent @{ type='finished'; terminal='studio/t74'; data=@{reason='killed'} }
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
