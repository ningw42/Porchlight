using namespace System.Windows.Automation

Invoke-SmokeFixture -Test {
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
    [pscustomobject]@{ TabsPerSpace=$MeasureSwitchTabs; Switches=$times.Count;
        MedianMs=[math]::Round(($sorted[5]+$sorted[6])/2,1); MaxMs=[math]::Round($sorted[-1],1) }
}
