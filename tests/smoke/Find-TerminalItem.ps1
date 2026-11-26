using namespace System.Windows.Automation

# Only match terminal tabs inside the strip from the verified viewer window.
function Find-TerminalItem([AutomationElement]$Tabs, [int]$OwnerPid, [string]$Label) {
    if ($Tabs -and $Tabs.Current.ProcessId -eq $OwnerPid) {
        $Tabs.FindFirst('Descendants', [AndCondition]::new(
            [PropertyCondition]::new([AutomationElement]::ProcessIdProperty, $OwnerPid),
            [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::TabItem),
            [PropertyCondition]::new([AutomationElement]::NameProperty, $Label)))
    }
}
