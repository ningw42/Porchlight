using namespace System.Windows.Automation

function SidebarStatus {
    $status = FindId Status
    if ($status -and !$status.Current.IsOffscreen -and !$status.Current.BoundingRectangle.IsEmpty -and
        $status.Current.BoundingRectangle.Right -le (FindId WorkspaceFrame).Current.BoundingRectangle.Left) { $status.Current.Name }
}

Invoke-SmokeFixture -Test {
    SelectSpace core-studio Work $remoteWork core-studio/t74
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
    SelectSpace core-local Work $localWork ($startupTerminal ?? 't74')
    if ($startupTerminal -ceq 't76') { CommitTerminal $localWork t74 }
}
