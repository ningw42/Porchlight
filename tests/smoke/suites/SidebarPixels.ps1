. "$PSScriptRoot/../fixture/ToggleSidebarWithViewer.ps1"
$sidebarPixelSampler = Join-Path $PSScriptRoot '../Test-SidebarPixels.ps1'

function MeasureSidebarPixels([switch]$Instant, $Baseline) {
    # Both arms start on the same painted terminal with the sidebar closed (wide grid).
    SelectSpace core-local Personal $personal t75
    ToggleSidebarWithViewer
    & $sidebarPixelSampler -Hwnd $hwnd -Frame (FindId WorkspaceFrame) -Toggle $toggle `
        -State $state -Instant:$Instant -Baseline $Baseline
}

$instantTimings = Invoke-SmokeFixture -Instant -PaintCells -Test { MeasureSidebarPixels -Instant }
Invoke-SmokeFixture -PaintCells -Test { MeasureSidebarPixels -Baseline $instantTimings }
