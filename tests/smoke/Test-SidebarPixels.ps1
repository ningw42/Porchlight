# MachineSpaces calls this with its synthetic terminal wide and fully painted. Return it wide.
param($Hwnd, $Frame, $Toggle, $State, [switch]$Instant, $Baseline)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing.Common,System.Windows.Forms
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class FixturePixels {
    [DllImport("user32")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("gdi32", CharSet=CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string device, IntPtr output, IntPtr data);
    [DllImport("gdi32")] static extern int GetDeviceCaps(IntPtr dc, int index);
    [DllImport("gdi32")] static extern bool DeleteDC(IntPtr dc);
    public static int RefreshHz(string display) {
        var dc = CreateDC("DISPLAY", display, IntPtr.Zero, IntPtr.Zero);
        if (dc == IntPtr.Zero) return 0;
        try { return GetDeviceCaps(dc, 116); } finally { DeleteDC(dc); } // VREFRESH
    }
}
'@
$oldDpi = [FixturePixels]::SetThreadDpiAwarenessContext([intptr](-4)) # UIA returns physical screen coordinates.
try {
    if (![FixturePixels]::SetWindowPos($Hwnd, [intptr](-1), 0,0,0,0, 0x13)) { throw 'Cannot show fixture for pixel sampling' }
    $screen = [Windows.Forms.Screen]::FromHandle($Hwnd); $hz = [FixturePixels]::RefreshHz($screen.DeviceName)
    if ($hz -le 1) { throw 'Cannot measure a one-frame budget without the fixture display refresh rate' }
    $frameMs = 1000.0 / $hz
    $r = $Frame.Current.BoundingRectangle; $x = [int]$r.Left; $y = [int]($r.Top + $r.Height / 2)
    $rect = [Drawing.Rectangle]::new(0, 0, [int]$r.Width, 1)
    if (!$screen.Bounds.Contains([Drawing.Rectangle]::new($x,$y,$rect.Width,1))) { throw 'Keep the fixture scanline on one monitor' }
    $line = [Drawing.Bitmap]::new($rect.Width, 1, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($line); $pixels = [int[]]::new($rect.Width)
    function Sample {
        # One scanline of the topmost fixture only; never save an image or inspect a user's terminal.
        $g.CopyFromScreen($x, $y, 0, 0, $rect.Size)
        $bits = $line.LockBits($rect, [Drawing.Imaging.ImageLockMode]::ReadOnly, $line.PixelFormat)
        try { [Runtime.InteropServices.Marshal]::Copy($bits.Scan0, $pixels, 0, $pixels.Length) }
        finally { $line.UnlockBits($bits) }
        [pscustomobject]@{
            Left = [array]::IndexOf($pixels, [Drawing.Color]::Cyan.ToArgb()); Body = [array]::IndexOf($pixels, [Drawing.Color]::Magenta.ToArgb())
            Last = [Math]::Max([array]::LastIndexOf($pixels, [Drawing.Color]::Magenta.ToArgb()),
                [Math]::Max([array]::LastIndexOf($pixels, [Drawing.Color]::Yellow.ToArgb()), [array]::LastIndexOf($pixels, [Drawing.Color]::Lime.ToArgb())))
            Wide = [array]::IndexOf($pixels, [Drawing.Color]::Yellow.ToArgb()); Narrow = [array]::IndexOf($pixels, [Drawing.Color]::Lime.ToArgb())
            Edge = [array]::LastIndexOf($pixels, $edgeColor)
        }
    }
    # Calibrate the painted edge in the right padding, independently of the right-aligned VT marker.
    $edgeColor = 0; $clock = [Diagnostics.Stopwatch]::StartNew()
    do { $wide = Sample } while (($wide.Left -lt 0 -or $wide.Wide -lt 0) -and $clock.ElapsedMilliseconds -lt 1000)
    if ($wide.Left -lt 0 -or $wide.Body -le $wide.Left -or $wide.Wide -le $wide.Body -or $wide.Last -le $wide.Wide) {
        throw 'Fixture colors are not visible before the sidebar pixel check'
    }
    $edge = [int](FindId PaneHost).Current.BoundingRectangle.Right - $x - 1
    if ($edge -le $wide.Last -or $edge -ge $pixels.Length) { throw 'Pane edge is outside the sampled right padding' }
    $edgeColor = $pixels[$edge]
    if ([array]::LastIndexOf($pixels,$edgeColor) -ne $edge) { throw 'Cannot distinguish the pane edge from its frame' }
    $wideCols = $State.columns; $cellWidth = ($wide.Body - $wide.Left) / 2
    if ([Math]::Abs(($wide.Last - $wide.Wide + 1) - 3 * $cellWidth) -gt 1) { throw 'Wide repaint marker is not at the trailing columns' }

    if (!$Instant) {
        # Keep the original expansion check: no remote repaint on shrink can hide a swap-chain flash.
        $Toggle.Toggle(); $clock.Restart(); $maxDrift = 0; $missing = 0
        do {
            $s = Sample
            if ($s.Left -lt 0 -or $s.Body - $s.Left -ne 2 * $cellWidth) { $missing++ }
            $maxDrift = [Math]::Max($maxDrift, [Math]::Abs($s.Last - $wide.Last))
        } while ($clock.ElapsedMilliseconds -lt 500)
        if ($missing -or $maxDrift -gt $cellWidth -or $s.Left -le $wide.Left) {
            throw "Expansion lost the leading cells in $missing samples; last painted column drifted up to $maxDrift px (cell: $cellWidth px)"
        }
        ToggleSidebarWithViewer # Back to wide, settled and repainted, as in the instant arm.
    }

    foreach ($delay in 0,40,80,160) {
        $State.repaintDelayMs = $delay; $sawSlide = $false
        $times = for ($i = 0; $i -le 10; $i++) {
            # Start every measurement with a visibly fresh narrow frame, not last trial's yellow marker.
            $resizes = $State.resizeCount; $Toggle.Toggle(); $clock.Restart()
            do {
                $s = Sample
                $narrowReady = $s.Narrow -ge 0 -and [Math]::Abs($s.Narrow - $wide.Wide) -le $cellWidth -and $s.Wide -lt 0
            } while (!$narrowReady -and $clock.ElapsedMilliseconds -lt 2000)
            $narrowCols = $State.columns; $left = $Frame.Current.BoundingRectangle.Left
            if (!$narrowReady -or $left -le $r.Left -or $narrowCols -ge $wideCols -or $State.resizeCount -ne $resizes + 1) {
                throw "Expected one narrow repaint before collapse ($delay ms, trial $i); marker $($s.Narrow), cols $narrowCols, resizes $($State.resizeCount - $resizes)"
            }
            $settled = $null; $clock.Restart(); $Toggle.Toggle() # Include dispatch and delivery of the resize in both arms.
            do {
                $s = Sample; $ms = $clock.Elapsed.TotalMilliseconds
                if ([Math]::Abs($s.Edge - $edge) -gt 1) { throw "Collapse moved the painted pane edge from $edge to $($s.Edge) ($delay ms, trial $i)" }
                $bounds = $Frame.Current.BoundingRectangle
                if ([Math]::Abs($bounds.Right - $r.Right) -gt 1) { throw 'Collapse moved the workspace right edge' }
                if ($bounds.Left -gt $r.Left + 1 -and $bounds.Left -lt $left - 1) { $sawSlide = $true }
                if ($s.Wide -eq $wide.Wide -and $s.Last -eq $wide.Last) {
                    if ($null -eq $settled) { $settled = $ms }
                } else { $settled = $null } # A stale/blank right side before repaint is allowed. A later flash resets the clock.
            } while ($ms -lt 2000 -and ($ms -lt 350 -or $null -eq $settled -or $ms - $settled -lt 2 * $frameMs))
            if ($null -eq $settled -or $ms - $settled -lt 2 * $frameMs -or $bounds.Left -ne $r.Left -or
                $State.columns -ne $wideCols -or $State.resizeCount -ne $resizes + 2) {
                throw "Collapse did not settle at the original wide grid with one resize ($delay ms, trial $i)"
            }
            if ($settled -lt $delay) { throw "Counted a stale frame as repainted at $settled ms, before the $delay ms delay" }
            if ($i -gt 0) { $settled } # Discard a warm-up pair per delay; compare medians, not an isolated scheduling hiccup.
        }
        if ($sawSlide -eq $Instant.IsPresent) { throw "Expected distinct instant/animated arms (instant=$Instant, saw slide=$sawSlide). Enable Windows animation effects for the A/B." }
        $sorted = @($times | Sort-Object); $median = ($sorted[4] + $sorted[5]) / 2
        if ($Instant) {
            [pscustomobject]@{ Delay=$delay; Median=$median; Hz=$hz; Display=$screen.DeviceName; Cell=$cellWidth; Narrow=$narrowCols; Wide=$wideCols }
        } else {
            $ref = @($Baseline | Where-Object Delay -eq $delay)
            if ($ref.Count -ne 1 -or $ref[0].Hz -ne $hz -or $ref[0].Display -ne $screen.DeviceName -or $ref[0].Cell -ne $cellWidth -or
                $ref[0].Wide -ne $wideCols -or $ref[0].Narrow -ne $narrowCols) { throw "Incomparable instant reference for $delay ms repaint delay" }
            $delta = $median - $ref[0].Median
            $report = 'Collapse +{0} ms repaint: instant {1:n1} ms, animated {2:n1} ms (10-trial medians); delta {3:n1} ms, one frame {4:n1} ms' -f $delay,$ref[0].Median,$median,$delta,$frameMs
            if ($delta -gt $frameMs) { throw $report }; Write-Host $report
        }
    }
} finally {
    $State.repaintDelayMs = $null
    if ($g) { $g.Dispose() }; if ($line) { $line.Dispose() }
    [void][FixturePixels]::SetWindowPos($Hwnd, [intptr](-2), 0,0,0,0, 0x13)
    [void][FixturePixels]::SetThreadDpiAwarenessContext($oldDpi)
}
