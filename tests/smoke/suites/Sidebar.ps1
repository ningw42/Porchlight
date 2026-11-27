using namespace System.Windows.Automation

. "$PSScriptRoot/../fixture/ToggleSidebarWithViewer.ps1"

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

Invoke-SmokeFixture -Test {
    [void](WaitFor 'full-height windowed caption buttons' { CaptionFits 40 })
    if ((FindId WindowLabel) -or (FindId ScopeLabel)) { throw 'Duplicate title or machine/space label in the title bar' }

    # The fixture starts in multi-tab Work, but --select and --browse do not select its first tab.
    if ($startupTerminal -cne 't74') { CommitTerminal $localWork t74 }
    [void](WaitFor 'selected first tab aligned with the workspace' { TabsInCaption $localWork.t74 })

    SelectSpace core-local Archive @{}
    [void](WaitFor 'draggable title bar above the open sidebar and the empty workspace' { TabsInCaption })
    # Check first-tab alignment with a strip that fits. WinUI adds scroll buttons before overflowing tabs.
    SelectSpace core-local Personal $personal t75
    # These headers round in opposite directions at 200% DPI. UIA includes the visible corner in the tab bounds.
    [void](WaitFor 'ScopeJobs tab corner at the workspace edge' { TabsInCaption $personal.t75 })

    $state.catalogEdit.SetResult('retitle')
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
    ToggleSidebarWithViewer
    [void](WaitFor 'visible sidebar with toggle unmoved' {
        $group = FindId MachineGroup-core-studio
        $toggle.Current.ToggleState -eq 'On' -and $group -and !$group.Current.IsOffscreen -and
        $toggleBounds.Equals((FindId SidebarToggle).Current.BoundingRectangle)
    })
    CheckScope core-local Personal $personal t75
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
}
