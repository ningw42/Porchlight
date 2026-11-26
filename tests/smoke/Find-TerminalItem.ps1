using namespace System.Windows.Automation

# WinUI can put a ComboBox flyout in a separate top-level HWND. Never search or
# interact with arbitrary desktop descendants when committing a terminal selection.
if (!('PorchlightPopupOwner' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PorchlightPopupOwner {
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
'@
}
function Find-TerminalItem([AutomationElement]$Picker, [AutomationElement]$Window, [IntPtr]$Hwnd, [int]$OwnerPid, [string]$Label) {
    $mine = [PropertyCondition]::new([AutomationElement]::ProcessIdProperty, $OwnerPid)
    $match = [AndCondition]::new($mine,
        [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::ListItem),
        [PropertyCondition]::new([AutomationElement]::NameProperty, $Label))
    $item = $Picker.FindFirst('Descendants', $match)
    if (!$item) { $item = $Window.FindFirst('Descendants', $match) }
    if ($item) { return $item }
    foreach ($candidate in [AutomationElement]::RootElement.FindAll('Children', $mine)) {
        $popup = [IntPtr]$candidate.Current.NativeWindowHandle
        if ($popup -ne [IntPtr]::Zero -and $popup -ne $Hwnd -and [PorchlightPopupOwner]::GetAncestor($popup, 3) -eq $Hwnd) {
            $item = $candidate.FindFirst('Descendants', $match); if ($item) { return $item }
        }
    }
}
