using namespace System.Windows.Automation

function ExpandGroup($machine, $expanded) {
    $id = "MachineGroup-$machine"; $pattern = [ExpandCollapsePattern]::Pattern
    $target = if ($expanded) { 'Expanded' } else { 'Collapsed' }
    $p = Pattern $id $pattern
    if ($p.Current.ExpandCollapseState -ne $target) {
        if ($expanded) { $p.Expand() } else { $p.Collapse() }
    }
    [void](WaitFor "$id $target" { (FindId $id).GetCurrentPattern($pattern).Current.ExpandCollapseState -eq $target })
}
function IsExpanded($machine) {
    (Pattern "MachineGroup-$machine" ([ExpandCollapsePattern]::Pattern)).Current.ExpandCollapseState -eq 'Expanded'
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

Invoke-SmokeFixture -Test {
    CheckNoAttachButtons; CheckLocalOrder
    if (!(IsExpanded core-local) -or !(IsExpanded core-studio) -or !(IsExpanded core-extra)) { throw 'Machines must start expanded' }
    if ((FindId MachineGroup-core-local).Current.Name -cne 'Local, connected') { throw 'Missing accessible machine name or state' }
    foreach ($id in 'MachineGroup-core-local','MachineGroup-core-studio') {
        if (FindId StateLabel (FindId $id)) { throw 'Connected machines should not have a state caption' }
    }
    $extra = FindId MachineGroup-core-extra
    if ($extra.Current.Name -cne 'extra, needs login' -or (FindId StateLabel $extra).Current.Name -cne 'needs login') {
        throw 'Missing unavailable machine or specific reason'
    }
    ExpandGroup core-extra $false
    foreach ($expanded in $false,$true) {
        ExpandGroup core-studio $expanded; CheckScope core-local Work $localWork $startupTerminal
    }

    SelectSpace core-studio Work $remoteWork core-studio/t74
    CommitTerminal $remoteWork core-studio/t76
    # The local startup choice is independent of studio's t76. Browse left no remembered tab.
    SelectSpace core-local Work $localWork ($startupTerminal ?? 't74')
    if ($startupTerminal -ceq 't76') { CommitTerminal $localWork t74 }
    $localWork.t74 = 't74 · local twin · Git Changes'; $localWork.t73 = 't73 · new sibling'
    $state.catalogEdit.SetResult('insert')
    CheckScope core-local Work $localWork t74 # Inserted t73 and renamed t74; no new viewer.
    if (IsExpanded core-extra) { throw 'Catalog update reset machine expansion' }
    CheckLocalOrder
    CommitTerminal $localWork t76
    CommitTerminal $localWork t74
    CheckNoAttachButtons

    # Remember non-first tabs independently, including the null space ID and two cores with identical sp1/t74/t76 IDs.
    SelectSpace core-local Unassigned $unassigned t77
    CommitTerminal $unassigned t78
    SelectSpace core-local Work $localWork t74 -focusSettings:$CheckFocus
    SelectSpace core-local Unassigned $unassigned t78
    SelectSpace core-local Archive @{} # The active viewer must detach, without attaching a neighbor.
    SelectSpace core-studio Work $remoteWork core-studio/t76
    SelectSpace core-local Work $localWork t74
    CommitTerminal $localWork t76
    SelectSpace core-studio Work $remoteWork core-studio/t76 # Same terminal ID, different viewer/route.

    $state.catalogEdit.SetResult('finish-hidden')
    CheckScope core-studio 'Renamed Work' $remoteWork core-studio/t76 # Same viewer through the sidebar rename.
    # Even a reused strip must drop finished local t76 and show t74's new title. Fall back to first live t73.
    $localWork.Remove('t76'); $localWork.t74 = 't74 · local twin · pwsh'
    SelectSpace core-local Work $localWork t73
    SelectSpace core-studio 'Renamed Work' $remoteWork core-studio/t76
    CommitTerminal $remoteWork core-studio/t74
}
