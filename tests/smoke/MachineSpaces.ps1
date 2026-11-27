#requires -Version 7.4
# Run from PowerShell on an interactive Windows desktop. Omit -Suite for all standard checks.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Exe,
    [ValidateRange(0,96)][int]$MeasureSwitchTabs,
    [ValidateSet('Default','Select','Browse')][string]$Startup = 'Default',
    [switch]$CheckFocus,
    [switch]$CheckSidebarPixels,
    [ValidateNotNullOrEmpty()][ValidateSet('Settings','Sidebar','Spaces','TabClose','SidebarPixels')]
    [string[]]$Suite = @('Settings','Sidebar','Spaces','TabClose')
)
$ErrorActionPreference = 'Stop'
if ($MeasureSwitchTabs -and ($CheckSidebarPixels -or $PSBoundParameters.ContainsKey('Suite'))) {
    throw 'Run -MeasureSwitchTabs separately from -Suite or -CheckSidebarPixels.'
}
$exePath = (Get-Item -LiteralPath $Exe).FullName
. "$PSScriptRoot/fixture/Viewer.ps1"

if ($MeasureSwitchTabs) {
    & "$PSScriptRoot/suites/SwitchBenchmark.ps1"
} else {
    if ($CheckSidebarPixels -and 'SidebarPixels' -notin $Suite) { $Suite += 'SidebarPixels' }
    foreach ($name in $Suite | Select-Object -Unique -CaseInsensitive) {
        Write-Host "Running smoke suite: $name"
        & "$PSScriptRoot/suites/$name.ps1"
        Write-Host "Passed: $name"
    }
}
