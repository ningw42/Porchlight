using namespace System.Windows.Automation

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
