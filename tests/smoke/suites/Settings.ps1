using namespace System.Windows.Automation

function SettingsButton {
    $button = FindId SettingsButton
    if ($button -and $button.Current.Name -ceq 'Settings' -and $button.Current.IsEnabled -and !$button.Current.IsOffscreen) { $button }
}
function FindSettings {
    $windows = [AutomationElement]::RootElement.FindAll('Children', [AndCondition]::new(
        [PropertyCondition]::new([AutomationElement]::ProcessIdProperty, $viewer.Id),
        [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::Window),
        [PropertyCondition]::new([AutomationElement]::NameProperty, 'Porchlight Settings')))
    if ($windows.Count -gt 1) { throw 'More than one Settings window for this fixture' }
    if ($windows.Count) {
        $handle = [IntPtr]$windows[0].Current.NativeWindowHandle
        if ($handle -eq [IntPtr]::Zero -or $handle -eq $hwnd) { throw 'Settings is not a separate window' }
        $windows[0]
    }
}
function OpenSettings {
    (WaitFor 'enabled main window Settings button' { SettingsButton }).GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    $settings = WaitFor 'fixture Settings window' { FindSettings }
    if ($settings.GetCurrentPattern([WindowPattern]::Pattern).Current.IsModal -or !$root.Current.IsEnabled) { throw 'Settings blocked the main window' }
    if ($CheckFocus) {
        [void](WaitFor 'active Settings window' { [PorchlightChrome]::GetForegroundWindow() -eq [IntPtr]$settings.Current.NativeWindowHandle })
    }
    $settings
}
function VisibleSuggestions($settings) {
    $settings.FindFirst('Descendants', [AndCondition]::new(
        [PropertyCondition]::new([AutomationElement]::AutomationIdProperty, 'SuggestionsList'),
        [PropertyCondition]::new([AutomationElement]::IsOffscreenProperty, $false)))
}
function CheckCategoryFocus($settings) {
    [void](WaitFor 'Terminal category focused with both suggestion lists closed' {
        (FindId TerminalCategory $settings).Current.HasKeyboardFocus -and !(VisibleSuggestions $settings)
    })
}
function SendSettingsKey($settings, [int]$key, [int]$scan) {
    # Address only this fixture's Settings input site, never the desktop keyboard.
    $site = $settings.FindFirst('Descendants', [PropertyCondition]::new([AutomationElement]::ClassNameProperty, 'InputSiteWindowClass'))
    $child = [IntPtr]$site.Current.NativeWindowHandle
    if ($child -eq [IntPtr]::Zero -or $site.Current.ProcessId -ne $viewer.Id -or [PorchlightChrome]::GetForegroundWindow() -ne [IntPtr]$settings.Current.NativeWindowHandle) {
        throw 'No active fixture Settings input site'
    }
    $down = ($scan -shl 16) -bor 1
    if (![PorchlightChrome]::PostMessage($child, 0x100, $key, $down) -or ![PorchlightChrome]::PostMessage($child, 0x101, $key, (0xc0000000L -bor $down))) {
        throw "Could not send key $key to Settings"
    }
}
function CloseSettings($settings) {
    $settings.GetCurrentPattern([WindowPattern]::Pattern).Close()
    [void](WaitFor 'Settings window to close' { !(FindSettings) })
}
function CheckNoGatewayControls($within) {
    foreach ($id in 'CoreLabel','RefreshMachinesButton','PipePath') {
        if (FindId $id $within) { throw "Obsolete gateway control: $id" }
    }
    if ($within.FindFirst('Descendants', [PropertyCondition]::new([AutomationElement]::NameProperty, 'Gateway'))) { throw 'Obsolete Gateway section' }
    $buttons = $within.FindAll('Descendants', [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::Button))
    foreach ($button in $buttons) {
        if ($button.Current.Name -match '^(Refresh|Retry)\b') { throw "Obsolete action: $($button.Current.Name)" }
    }
}
$previewLines = @('C:\work> git diff', '└─ != => ffi 0123456789 日本語')
function WaitForTerminalText($hostId, $within, [string[]]$expected) {
    WaitFor "$hostId native terminal buffer: $($expected -join ' / ')" {
        $frame = FindId $hostId $within
        if (!$frame) { return }
        $pane = $frame.FindFirst('Descendants', [PropertyCondition]::new([AutomationElement]::ClassNameProperty, 'TermControl'))
        $text = $null
        if (!$pane -or !$pane.TryGetCurrentPattern([TextPattern]::Pattern, [ref]$text)) { return }
        $screen = $text.DocumentRange.GetText(-1)
        foreach ($line in $expected) { if (!$screen.Contains($line)) { return } }
        $text
    }
}
function TerminalRowHeight($text, $word) {
    # TermControl exposes cell geometry through text ranges, not the FontSize attribute.
    $match = $text.DocumentRange.FindText($word, $false, $false)
    if ($match) {
        $rects = $match.GetBoundingRectangles()
        if ($rects.Count -eq 1) { $rects[0].Height }
    }
}
function NativeFace($text) {
    $text.DocumentRange.GetAttributeValue([TextPattern]::FontNameAttribute)
}
function TokensAre($picker, [string[]]$expected, [switch]$ordered) {
    if (!$picker -or $picker.GetCurrentPattern([ValuePattern]::Pattern).Current.Value) { return $false }
    # Count committed token containers, not TextBlocks in a suggestion popup.
    $listItem = [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::ListItem)
    $names = foreach ($item in $picker.FindAll('Children', $listItem)) {
        if (FindId PART_RemoveButton $item) { $item.Current.Name }
    }
    if (!$ordered) { $names = $names | Sort-Object; $expected = $expected | Sort-Object }
    ($names -join ',') -ceq ($expected -join ',')
}
function CursorProbe($address, $query, $expected, $description) {
    $done = [Threading.Tasks.TaskCompletionSource[bool]]::new()
    $state.cursorProbe = @{ address=$address; query=$query; expected=$expected; done=$done }
    $state.catalogEdit.SetResult('cursor-probe')
    [void](WaitFor "$address $description" { $done.Task.IsCompletedSuccessfully })
}
function CheckLiveCursor($address, [ValidateSet('Blinking','Steady')]$blink, [ValidateSet('Bar','Underscore')]$shape, [switch]$programBlinks) {
    $mode = if ($blink -eq 'Blinking') { 1 } else { 2 }
    $style = @{ Bar=5; Underscore=3 }[$shape] + ($mode - 1)
    # Query the program's actual DEC blink mode (1=set, 2=reset) and DECSCUSR style.
    $query = "`e[?12`$p`eP`$q q`e\"
    if ($programBlinks) { $query = "`e[?12h" + $query }
    CursorProbe $address $query "`e[?12;$mode`$y`eP1`$r$style q`e\" "to report a $blink $shape cursor"
}
function CheckProgramColor($address) {
    CursorProbe $address "`e]4;1;?`a" "`e]4;1;rgb:1212/3434/5656`e\" 'to retain the program-defined ANSI red'
}
function CheckPreviewPointer($settings, $text) {
    $pane = $text.DocumentRange.GetEnclosingElement()
    if ($pane.Current.ClassName -cne 'TermControl' -or $pane.Current.IsEnabled) { throw 'Expected a disabled native preview' }
    if (!$CheckFocus) { return } # Like Tab, screen hit-testing requires the foreground fixture.
    Add-Type -Namespace PorchlightSmoke -Name PreviewPointer -MemberDefinition @'
[DllImport("user32")] public static extern IntPtr WindowFromPhysicalPoint(System.Drawing.Point point);
[DllImport("user32")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
'@
    $window = $settings.Current.NativeWindowHandle
    $ancestor = [TreeWalker]::new([OrCondition]::new(
        [PropertyCondition]::new([AutomationElement]::ClassNameProperty, 'TermControl'),
        [PropertyCondition]::new([AutomationElement]::NativeWindowHandleProperty, $window)))
    [void](WaitFor 'Settings owns the preview point and hits outside the disabled terminal' {
        $r = $pane.Current.BoundingRectangle
        if ($r.IsEmpty -or $r.Width -le 0 -or $r.Height -le 0) { return }
        $p = [Drawing.Point]::new($r.X + $r.Width / 2, $r.Y + $r.Height / 2)
        $under = [PorchlightSmoke.PreviewPointer]::WindowFromPhysicalPoint($p)
        if ([PorchlightSmoke.PreviewPointer]::GetAncestor($under, 2) -ne [IntPtr]$window) { return }
        $hit = [AutomationElement]::FromPoint([Windows.Point]::new($p.X, $p.Y))
        $hit -and $hit.Current.ProcessId -eq $viewer.Id -and $settings.Equals($ancestor.Normalize($hit))
    })
}
function CheckPreviewViewport($settings, $text, $launchHeight) {
    $window = [IntPtr]$settings.Current.NativeWindowHandle
    $scale = [PorchlightChrome]::GetDpiForWindow($window) / 96.0
    $original = $settings.Current.BoundingRectangle
    $frame = FindId TerminalPreviewSample $settings
    $scrollable = [TreeWalker]::new([PropertyCondition]::new([AutomationElement]::IsScrollPatternAvailableProperty, $true))
    $page = $scrollable.GetParent($frame) # Only scroll the enclosing Settings page, never the terminal.
    $scroll = $page.GetCurrentPattern([ScrollPattern]::Pattern)
    $size = (FindId TerminalFontSize $settings).GetCurrentPattern([RangeValuePattern]::Pattern)
    $specimen = ($previewLines -join '') -creplace '\s', ''
    # Return to a smaller effective size between 100 and 128: a stale 100-pt frame must not pass.
    foreach ($step in (920,14),(920,32),(920,14),(550,100),(550,14),(550,128),(920,14)) {
        $width, $pt = $step
        if ($scroll.Current.VerticallyScrollable) { $scroll.SetScrollPercent(-1, 0) }
        [PorchlightChrome]::Resize($window, $width * $scale, $original.Height)
        $size.SetValue($pt)
        $cell = WaitFor "visible specimen and end cursor at $width DIPs / $pt pt" {
            $bounds = $frame.Current.BoundingRectangle
            if ($scroll.Current.VerticalScrollPercent -gt 0) { $scroll.SetScrollPercent(-1, 0); return }
            if ($size.Current.Value -ne $pt -or $bounds.IsEmpty -or
                [Math]::Abs($settings.Current.BoundingRectangle.Width / $scale - $width) -gt 16 -or
                ($bounds.Width / $scale -gt 550) -ne ($width -eq 920)) { return }
            # GetVisibleRanges excludes scrollback. Tiny grids can insert line breaks/padding between glyphs.
            $visible = ($text.GetVisibleRanges() | ForEach-Object { $_.GetText(-1) }) -join ''
            if (($visible -creplace '\s', '') -cne $specimen) { return }
            $doc = $text.DocumentRange
            $first = $doc.FindText('C', $false, $false).GetBoundingRectangles()
            if ($first.Count -ne 1 -or $first[0].Height -le 0) { return }
            $h = $first[0].Height # One ASCII cell even when words wrap across many rows.
            if (($pt -eq 14 -and $h -ne $launchHeight) -or ($pt -eq 32 -and $h -le $launchHeight) -or
                ($pt -eq 100 -and $h -le $mediumHeight) -or ($pt -eq 128 -and $h -ne $capHeight)) { return }
            $last = $doc.FindText('語', $false, $false)
            $last.ExpandToEnclosingUnit('Character') # Both cells of the final wide glyph.
            $caret = $text.GetSelection()[0] # TermControl returns a degenerate range at the native cursor.
            if (!$caret -or $caret.GetText(-1)) { return }
            # The sample reserves a blank cursor cell after the last wide character.
            if ($caret.CompareEndpoints('Start', $last, 'Start') -le 0) { return }
            $caret.ExpandToEnclosingUnit('Character')
            $end = $caret.GetBoundingRectangles()
            if ($end.Count -eq 1 -and $last.GetBoundingRectangles().Count) {
                [pscustomobject]@{ Height = $h; Span = $end[0].Bottom - $first[0].Top }
            }
        }
        if ($pt -eq 32) { $mediumHeight = $cell.Height }
        if ($pt -eq 100) { $capHeight = $cell.Height }
        # Native UIA caches a clipped origin. Use only its row span; anchor it to the XAML host at
        # page offset zero. A tall specimen is fine if ordinary outer scrolling can reveal its end.
        $start = $frame.Current.BoundingRectangle.Top
        [void](WaitFor "last preview row reachable on the Settings page at $width DIPs / $pt pt" {
            $view = $page.Current.BoundingRectangle
            $travel = $view.Height * (100 / $scroll.Current.VerticalViewSize - 1)
            $y = $start + $cell.Span - $cell.Height / 2 - $travel * $scroll.Current.VerticalScrollPercent / 100
            $shown = [Windows.Rect]::Intersect($view, $frame.Current.BoundingRectangle)
            if ($y -ge $shown.Top -and $y -lt $shown.Bottom) { return $true }
            if ($travel -gt 0) { $scroll.SetScrollPercent(-1, [Math]::Min(100, 100 * ($cell.Span - $cell.Height) / $travel)) }
        })
    }
    [PorchlightChrome]::Resize($window, $original.Width, $original.Height)
}
function CheckSettingsLayout($settings) {
    $settingsHwnd = [IntPtr]$settings.Current.NativeWindowHandle
    $original = $settings.Current.BoundingRectangle
    $scale = [PorchlightChrome]::GetDpiForWindow($settingsHwnd) / 96.0
    $expander = [PropertyCondition]::new([AutomationElement]::ClassNameProperty, 'Microsoft.UI.Xaml.Controls.Expander')
    $fonts = [PropertyCondition]::new([AutomationElement]::NameProperty, 'Terminal fonts')
    # Exercise a narrow window and the old capped layout. UIA Resize can drop the WinUI input-site subtree.
    $narrow = 550
    $paneWidths = foreach ($width in $narrow,1150) {
        [PorchlightChrome]::Resize($settingsHwnd, $width * $scale, $original.Height)
        WaitFor "expanded Terminal navigation and font page at $width DIPs" {
            $window = $settings.Current.BoundingRectangle
            if ([Math]::Abs($window.Width / $scale - $width) -gt 16) { return }
            $nav = FindId Categories $settings
            if (!$nav -or $nav.Current.Name -cne 'Settings categories') { return }
            $category = FindId TerminalCategory $nav
            if (!$category -or $category.Current.IsOffscreen -or $category.Current.Name -cnotlike '*Terminal*' -or
                !$category.GetCurrentPattern([SelectionItemPattern]::Pattern).Current.IsSelected) { return }
            $row = $category.Current.BoundingRectangle
            if ($row.IsEmpty -or $row.Width -lt 120 * $scale -or $row.Left -lt $window.Left -or $row.Left -gt $window.Left + 40 * $scale) { return }
            foreach ($id in 'TerminalPageHeading','TerminalPreviewSample','TypographyHeading','FontSettingsCard','TerminalFontPicker','ResetFontsButton') {
                $control = FindId $id $settings
                $r = $control.Current.BoundingRectangle
                if (!$control -or $control.Current.IsOffscreen -or $r.IsEmpty -or $r.Left -lt $row.Right -or $r.Right -gt $window.Right) { return }
                if ($id -eq 'TerminalPageHeading' -and ($control.Current.ControlType -ne [ControlType]::Text -or $control.Current.Name -cne 'Terminal' -or
                    $control.GetCurrentPropertyValue([AutomationElement]::HeadingLevelProperty) -ne 'Level1')) { return }
                # Wait for the wide page to lay out, not just for the outer HWND to change size.
                if ($width -gt $narrow -and $id -eq 'ResetFontsButton' -and $r.Right -le $window.Left + $narrow * $scale) { return }
            }
            $sample = (FindId TerminalPreviewSample $settings).Current.BoundingRectangle
            $heading = FindId TypographyHeading $settings
            if ($heading.Current.Name -cne 'Typography' -or $heading.GetCurrentPropertyValue([AutomationElement]::HeadingLevelProperty) -ne 'Level2') { return }
            if ($sample.Top -lt (FindId TerminalPageHeading $settings).Current.BoundingRectangle.Bottom -or
                $sample.Bottom -gt $heading.Current.BoundingRectangle.Top -or
                $heading.Current.BoundingRectangle.Bottom -gt (FindId FontSettingsCard $settings).Current.BoundingRectangle.Top) { return }
            $togglePane = FindId TogglePaneButton $nav
            if ($togglePane -and !$togglePane.Current.IsOffscreen) { throw 'Settings navigation can be collapsed' }
            $row.Width
        }
        CheckNoGatewayControls $settings
        if ($settings.FindFirst('Descendants', [OrCondition]::new($expander, $fonts))) { throw 'Terminal fonts are behind an expander' }
    }
    if ([Math]::Abs($paneWidths[0] - $paneWidths[1]) -gt 2 * $scale) { throw 'Settings navigation width changed on resize' }
    [PorchlightChrome]::Resize($settingsHwnd, $original.Width, $original.Height)
}
function CursorOptions($settings) {
    $picker = FindId CursorShapePicker $settings
    if ($picker) {
        $picker.FindAll('Descendants', [PropertyCondition]::new([AutomationElement]::IsSelectionItemPatternAvailableProperty, $true))
    }
}
function CheckCursorSettings($settings) {
    $options = @(CursorOptions $settings)
    if (($options.Current.Name -join ',') -cne 'Vintage,Bar,Underscore,FilledBox,EmptyBox') { throw 'Unexpected cursor options or order' }
    foreach ($item in $options) {
        if ($item.FindFirst('Descendants', [PropertyCondition]::new([AutomationElement]::IsTextPatternAvailableProperty, $true))) {
            throw "$($item.Current.Name) uses a text glyph instead of cursor geometry"
        }
    }
    $sample = FindId TerminalPreviewSample $settings
    $selected = [SelectionItemPattern]::Pattern
    if (!$options[1].GetCurrentPattern($selected).Current.IsSelected -or $sample.Current.HelpText -cne 'Bar cursor') { throw 'Lost the pre-connect Bar cursor' }
    $toggle = FindId CursorBlinkSwitch $settings
    $blink = $toggle.GetCurrentPattern([TogglePattern]::Pattern)
    if ($toggle.Current.Name -cne 'Cursor blink' -or $blink.Current.ToggleState -ne 'Off') { throw 'Lost the pre-connect blink preference' }
    CheckLiveCursor t75 Steady Bar
    # Home, then park the program's parser inside CUP. Wait for credit, not a sleep or the pipe write.
    CursorProbe t75 "`e[H`e[4;" '' 'to consume the unfinished cursor-position command'
    $blink.Toggle()
    [void](WaitFor 'blinking Bar without changing shape' { $sample.Current.HelpText -ceq 'Blinking Bar cursor' })
    CursorProbe t75 "3H`e[6n" "`e[4;3R" 'to finish the split CUP at row 4, column 3'
    CheckLiveCursor t75 Blinking Bar # Only now query the deferred blink edit; the suffix frame has finished.
    # REP uses the last printed graphic. A complete CSI inserted after '@' would clear that register.
    CursorProbe t75 "`e[4;3H@" '' 'to print a repeatable graphic'
    $blink.Toggle()
    [void](WaitFor 'steady Bar requested after printable text' { $sample.Current.HelpText -ceq 'Bar cursor' })
    CursorProbe t75 "`e[3b`e[6n" "`e[4;7R" 'to repeat all three graphics before the deferred blink change'
    CheckLiveCursor t75 Steady Bar
    $blink.Toggle()
    [void](WaitFor 'blinking Bar restored' { $sample.Current.HelpText -ceq 'Blinking Bar cursor' })
    # The older user Off must not land after a newer DECSET, even if the command spans DATA frames.
    CursorProbe t75 'repeatable!' '' 'to retain a repeatable graphic'
    $blink.Toggle()
    [void](WaitFor 'Off requested at a printable prompt' { $sample.Current.HelpText -ceq 'Bar cursor' })
    CursorProbe t75 "`e[?12" '' 'to consume the prefix of DECSET 12'
    CursorProbe t75 'h' '' 'to complete the newer program blink request'
    CheckLiveCursor t75 Blinking Bar
    $blink.Toggle()
    [void](WaitFor 'blinking choice restored' { $sample.Current.HelpText -ceq 'Blinking Bar cursor' })
    foreach ($item in $options[0,2,3,4]) {
        $item.GetCurrentPattern($selected).Select()
        [void](WaitFor "preview $($item.Current.Name) cursor" {
            $item.GetCurrentPattern($selected).Current.IsSelected -and $sample.Current.HelpText -ceq "Blinking $($item.Current.Name) cursor"
        })
    }
    $blink.Toggle()
    [void](WaitFor 'steady EmptyBox without changing shape' { $sample.Current.HelpText -ceq 'EmptyBox cursor' })
    $options[2].GetCurrentPattern($selected).Select()
    [void](WaitFor 'steady Underscore cursor' { $sample.Current.HelpText -ceq 'Underscore cursor' })
    CheckLiveCursor t75 Steady Underscore
}
function CheckFontPicker($settings, $text, $live) {
    $picker = WaitFor 'terminal font picker' {
        $p = FindId TerminalFontPicker $settings
        if ($p -and $p.Current.IsEnabled -and !$p.Current.IsOffscreen) { $p }
    }
    $reset = WaitFor 'reset fonts button' { FindId ResetFontsButton $settings }
    $launchFont = NativeFace $live
    if ($launchFont -isnot [string] -or !$launchFont -or $launchFont -ceq 'Consolas') { throw "Unexpected native launch font: '$launchFont'" }
    $value = $picker.GetCurrentPattern([ValuePattern]::Pattern)
    if ($value.Current.Value -or $reset.Current.IsEnabled) { throw 'Expected an empty picker inheriting the launch fonts' }

    # The comma commits the name through the picker's own delimiter handling. No keyboard focus or injected keys.
    $value.SetValue('consolas,')
    $token = [PropertyCondition]::new([AutomationElement]::NameProperty, 'Consolas')
    [void](WaitFor 'Consolas token, preview and original live pane' {
        $reset.Current.IsEnabled -and $picker.FindFirst('Descendants', $token) -and
            (NativeFace $text) -ceq 'Consolas' -and (NativeFace $live) -ceq 'Consolas'
    })
    $reset.GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    [void](WaitFor 'reset to inherited fonts' {
        !$reset.Current.IsEnabled -and !$value.Current.Value -and !$picker.FindFirst('Descendants', $token) -and
            (NativeFace $text) -ceq $launchFont -and (NativeFace $live) -ceq $launchFont
    })
}

function CheckFontSize($settings, $text, $live) {
    $size = WaitFor 'font size editor' { FindId TerminalFontSize $settings }
    $range = $size.GetCurrentPattern([RangeValuePattern]::Pattern)
    $up = FindId UpSpinButton $size
    if (!$up -or $up.Current.IsOffscreen -or $range.Current.Value -ne 14) { throw 'Expected an inline font size editor showing the launch size' }
    $launchHeight = WaitFor 'native preview cell height at 14 pt' { TerminalRowHeight $text 'git' }

    $edit = FindId InputBox $size
    $inputValue = $edit.GetCurrentPattern([ValuePattern]::Pattern)
    function CommitSizeText([string]$text) {
        $edit.SetFocus()
        $inputValue.SetValue($text)
        if ($CheckFocus) { SendSettingsKey $settings 0x0d 0x1c } # Enter
        else { (FindId TerminalCategory $settings).SetFocus() }
    }
    CommitSizeText (12.5).ToString([cultureinfo]::CurrentCulture)
    $fractionalHeight = WaitFor "12.5 pt in editor and native cells shorter than $launchHeight px" {
        $h = TerminalRowHeight $text 'git'
        if ($range.Current.Value -eq 12.5 -and $h -gt 0 -and $h -lt $launchHeight -and (TerminalRowHeight $live 'Fixture') -eq $h) { $h }
    }
    $up.GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    [void](WaitFor "one-point spin step to 13.5 pt and native cells taller than $fractionalHeight px" {
        $h = TerminalRowHeight $text 'git'
        $range.Current.Value -eq 13.5 -and $h -gt $fractionalHeight -and (TerminalRowHeight $live 'Fixture') -eq $h
    })
    CommitSizeText ''
    [void](WaitFor 'clearing restores the last valid size in the editor' { $range.Current.Value -eq 13.5 -and $inputValue.Current.Value })
}

function CheckOpenTypePicker($settings) {
    $picker = WaitFor 'visible OpenType feature picker' {
        $p = FindId FeaturePicker $settings; if ($p -and $p.Current.IsEnabled -and !$p.Current.IsOffscreen) { $p }
    }
    $value = $picker.GetCurrentPattern([ValuePattern]::Pattern)
    $ligatures = FindId CodingLigaturesSwitch $settings
    if ($ligatures.Current.Name -cne 'Ligatures') { throw 'Expected a switch named Ligatures' }
    $switch = $ligatures.GetCurrentPattern([TogglePattern]::Pattern)
    $reset = FindId ResetFeaturesButton $settings
    $listItem = [PropertyCondition]::new([AutomationElement]::ControlTypeProperty, [ControlType]::ListItem)
    $fixturePid = [PropertyCondition]::new([AutomationElement]::ProcessIdProperty, $viewer.Id)

    function ChipsAre([string[]]$expected) { TokensAre $picker $expected }
    function RemoveChip($tag) {
        $name = [PropertyCondition]::new([AutomationElement]::NameProperty, $tag)
        $chip = $picker.FindFirst('Children', [AndCondition]::new($listItem, $name))
        (FindId PART_RemoveButton $chip).GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    }
    function QueryFeatures($prefix) {
        $edit = FindId TextBox $picker
        $edit.SetFocus()
        $edit.GetCurrentPattern([ValuePattern]::Pattern).SetValue($prefix)
    }
    function Suggestion($tag) {
        # The popup is a descendant of this Settings window, not necessarily of its picker.
        $row = FindId "FeatureSuggestion-$tag" $settings
        if ($row -and !$row.Current.IsOffscreen) { $row }
    }

    [void](WaitFor 'launch ss01 override and ligatures on' { (ChipsAre ss01) -and $switch.Current.ToggleState -eq 'On' -and !$reset.Current.IsEnabled })
    RemoveChip ss01
    [void](WaitFor 'removed launch chip can be reset' { (ChipsAre @()) -and $reset.Current.IsEnabled })
    $switch.Toggle()
    [void](WaitFor 'disabling ligatures is independent of feature chips' { (ChipsAre @()) -and $switch.Current.ToggleState -eq 'Off' -and $reset.Current.IsEnabled })
    if ($CheckFocus) {
        $edit = FindId TextBox $picker
        $edit.SetFocus()
        [void](WaitFor 'feature suggestions open on deliberate focus' { $edit.Current.HasKeyboardFocus -and (VisibleSuggestions $settings) })
        SendSettingsKey $settings 0x1b 0x01 # Escape
        [void](WaitFor 'dismiss initial suggestions without committing a token' {
            $edit.Current.HasKeyboardFocus -and !(VisibleSuggestions $settings) -and (ChipsAre @())
        })
        SendSettingsKey $settings 0x43 0x2e # C
        [void](WaitFor 'typed c in feature editor' { $value.Current.Value -ceq 'c' -and (Suggestion case) })
        SendSettingsKey $settings 0x1b 0x01 # Escape
        [void](WaitFor 'one Escape closes suggestions without leaving the editor or changing its text' {
            $edit.Current.HasKeyboardFocus -and $value.Current.Value -ceq 'c' -and !(VisibleSuggestions $settings)
        })
        SendSettingsKey $settings 0x41 0x1e # A, without refocusing the editor
    } else { QueryFeatures 'ca' }
    [void](WaitFor 'ca offers case, not the grouped calt tag' { $value.Current.Value -ceq 'ca' -and (Suggestion case) -and !(Suggestion calt) })
    QueryFeatures 'm'
    [void](WaitFor 'previewed matches first; the other tags remain alphabetical and selectable' {
        $rows = @(foreach ($tag in 'mark','medi','mkmk') { Suggestion $tag })
        if ($rows.Count -ne 3) { return }
        $displayed = $rows | Sort-Object { $_.Current.BoundingRectangle.Top }
        $expected = $rows | Sort-Object { [int]($_.Current.HelpText -eq 'No matching sample found') }, { $_.Current.Name }
        ($displayed.Current.Name -join ',') -ceq ($expected.Current.Name -join ',')
    })
    QueryFeatures 'ss0'
    $row = WaitFor 'ss01 has a one-grapheme off/on example rather than a whole word' {
        $r = Suggestion ss01
        if ($r -and $r.Current.Name -ceq 'ss01' -and $r.Current.HelpText -cmatch '^Off and on: (.+)$' -and
            [Globalization.StringInfo]::ParseCombiningCharacters($Matches[1]).Length -eq 1) { $r }
    }
    $walker = [TreeWalker]::new([AndCondition]::new($fixturePid, $listItem))
    $item = $walker.GetParent($row)
    if (!$item -or $item.Current.ProcessId -ne $viewer.Id) { throw 'Missing fixture ListViewItem for ss01' }
    # Invoke commits the suggestion; SelectionItem.Select can merely highlight the row.
    $item.GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    [void](WaitFor 'invoked ss01 suggestion committed as a chip' { ChipsAre ss01 })
    $value.SetValue('zero,')
    [void](WaitFor 'typed individual tag added alongside ss01' { ChipsAre zero,ss01 })
    foreach ($invalid in 'ss01','zzzz','calt','liga','clig','dlig','hlig','rlig') {
        $value.SetValue("$invalid,")
        [void](WaitFor "reject '$invalid' without changing either control" { (ChipsAre zero,ss01) -and $switch.Current.ToggleState -eq 'Off' })
    }

    $fonts = FindId TerminalFontPicker $settings
    $family = $fonts.GetCurrentPattern([ValuePattern]::Pattern)
    $family.SetValue('Segoe MDL2 Assets,')
    [void](WaitFor 'icon font has no tags but keeps both chips editable' {
        $s = FindId FeatureStatus $settings
        $s -and $s.Current.Name -ceq 'No individual OpenType features found in Segoe MDL2 Assets.' -and
            $picker.Current.IsEnabled -and (ChipsAre zero,ss01) -and $switch.Current.ToggleState -eq 'Off'
    })
    $family.SetValue('Cascadia Code,')
    $added = [PropertyCondition]::new([AutomationElement]::NameProperty, 'Cascadia Code')
    [void](WaitFor 'fallback token committed' { $fonts.FindFirst('Children', [AndCondition]::new($listItem, $added)) })
    RemoveChip zero
    [void](WaitFor 'remove chip even with no candidates' { ChipsAre ss01 })
    $value.SetValue('zero,')
    [void](WaitFor 'fallback font cannot supply zero' { ChipsAre ss01 })
    $fontReset = FindId ResetFontsButton $settings
    $fontReset.GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    [void](WaitFor 'launch font list restored' { !$fontReset.Current.IsEnabled -and (ChipsAre ss01) })
    QueryFeatures 'ze'
    [void](WaitFor 'launch font offers zero again' { Suggestion zero })
    $value.SetValue('')
    [void](WaitFor 'edited chips survive restoring the launch font' { (ChipsAre ss01) -and $reset.Current.IsEnabled })
    # Close with both feature controls different from the explicit launch map. Reset them only after a reopen.
    RemoveChip ss01
    [void](WaitFor 'launch chip removed' { ChipsAre @() })
    $value.SetValue('zero,')
    [void](WaitFor 'edited features ready to retain' { (ChipsAre zero) -and $switch.Current.ToggleState -eq 'Off' -and $reset.Current.IsEnabled })
}

function CheckRetainedSettings($settings, $text, $pt, $height, $face) {
    [void](WaitFor "reopened Settings: ordered families, $pt pt, features, ligatures and steady Underscore" {
        $options = @(CursorOptions $settings)
        $options.Count -eq 5 -and $options[2].GetCurrentPattern([SelectionItemPattern]::Pattern).Current.IsSelected -and
            (FindId CursorBlinkSwitch $settings).GetCurrentPattern([TogglePattern]::Pattern).Current.ToggleState -eq 'Off' -and
            (FindId TerminalPreviewSample $settings).Current.HelpText -ceq 'Underscore cursor' -and
            (FindId TerminalFontSize $settings).GetCurrentPattern([RangeValuePattern]::Pattern).Current.Value -eq $pt -and
            (TokensAre (FindId TerminalFontPicker $settings) 'Consolas','Cascadia Code' -ordered) -and
            (TokensAre (FindId FeaturePicker $settings) zero) -and
            (FindId CodingLigaturesSwitch $settings).GetCurrentPattern([TogglePattern]::Pattern).Current.ToggleState -eq 'Off' -and
            (FindId ResetFontsButton $settings).Current.IsEnabled -and (FindId ResetFeaturesButton $settings).Current.IsEnabled -and
            (NativeFace $text) -ceq $face -and (TerminalRowHeight $text 'git') -eq $height
    })
}

Invoke-SmokeFixture -FontArguments @('--face="Unavailable, Fixture", Cascadia Code, Consolas', '--ss01=1') -BeforeConnect {
    CheckNoGatewayControls $root
    $settings = OpenSettings # The fake peer has not even read the gateway hello.
    [void](WaitForTerminalText TerminalPreviewSample $settings $previewLines)
    [void](WaitFor 'initial blinking Vintage cursor' {
        (CursorOptions $settings)[0].GetCurrentPattern([SelectionItemPattern]::Pattern).Current.IsSelected -and
            (FindId CursorBlinkSwitch $settings).GetCurrentPattern([TogglePattern]::Pattern).Current.ToggleState -eq 'On' -and
            (FindId TerminalPreviewSample $settings).Current.HelpText -ceq 'Blinking Vintage cursor'
    })
    # Edit with no pane or gateway session: the next attachment must use these choices.
    (CursorOptions $settings)[1].GetCurrentPattern([SelectionItemPattern]::Pattern).Select()
    (FindId CursorBlinkSwitch $settings).GetCurrentPattern([TogglePattern]::Pattern).Toggle()
    [void](WaitFor 'steady Bar cursor before close' { (FindId TerminalPreviewSample $settings).Current.HelpText -ceq 'Bar cursor' })
    CloseSettings $settings
} -Test {
    CheckNoGatewayControls $root
    SelectSpace core-local Personal $personal t75
    $live = WaitForTerminalText PaneHost $root 'Fixture t75'
    $originalPane = $live.DocumentRange.GetEnclosingElement()
    $launchFace = NativeFace $live
    $liveHeight = WaitFor 'initial live terminal cell height' { TerminalRowHeight $live 'Fixture' }
    CheckLiveCursor t75 Steady Bar # Edited before any connection existed.
    $settings = OpenSettings
    $text = WaitForTerminalText TerminalPreviewSample $settings $previewLines
    CheckPreviewPointer $settings $text
    if ($CheckFocus) {
        CheckCategoryFocus $settings
        # The preview sits between navigation and the first editor. Tab must skip it.
        SendSettingsKey $settings 0x09 0x0f
        [void](WaitFor 'Tab skips the preview and enters the font picker' {
            $picker = FindId TerminalFontPicker $settings
            $picker.Current.HasKeyboardFocus -or $picker.FindFirst('Descendants',
                [PropertyCondition]::new([AutomationElement]::HasKeyboardFocusProperty, $true))
        })
    }
    CheckScope core-local Personal $personal t75
    CheckSettingsLayout $settings
    CheckPreviewViewport $settings $text $liveHeight
    CheckFontPicker $settings $text $live
    CheckFontSize $settings $text $live
    CheckOpenTypePicker $settings
    CheckCursorSettings $settings
    # The program overrides the configured steady underline AND an ANSI palette slot.
    CursorProbe t75 "`e[5 q`e]4;1;rgb:12/34/56`a" '' 'to choose a blinking bar and a custom palette entry'
    CheckLiveCursor t75 Blinking Bar
    CheckProgramColor t75
    $ligatures = (FindId CodingLigaturesSwitch $settings).GetCurrentPattern([TogglePattern]::Pattern)
    foreach ($expected in 'On','Off') {
        $ligatures.Toggle()
        [void](WaitFor "ligatures $expected" { $ligatures.Current.ToggleState -eq $expected })
        CheckLiveCursor t75 Blinking Bar
        CheckProgramColor t75
    }
    $fonts = FindId TerminalFontPicker $settings
    $family = $fonts.GetCurrentPattern([ValuePattern]::Pattern)
    $family.SetValue('Consolas,')
    [void](WaitFor 'edited primary family on the original pane' { (TokensAre $fonts Consolas) -and (NativeFace $live) -ceq 'Consolas' })
    $family.SetValue('Cascadia Code,')
    $face = WaitFor 'ordered fallback committed; both native terminals use the edited face' {
        $name = NativeFace $live
        if ((TokensAre $fonts 'Consolas','Cascadia Code' -ordered) -and $name -and $name -cne $launchFace -and $name -ceq (NativeFace $text)) { $name }
    }
    $pt = 13.5
    $height = WaitFor 'edited native height on both terminals' {
        $h = TerminalRowHeight $live 'Fixture'; if ($h -gt 0 -and $h -eq (TerminalRowHeight $text 'git')) { $h }
    }
    CheckLiveCursor t75 Blinking Bar
    CheckProgramColor t75
    if ($CheckFocus) {
        [void](WaitFor 'main window active with Settings open' {
            [PorchlightChrome]::GetForegroundWindow() -eq $hwnd -or [PorchlightChrome]::SetForegroundWindow($hwnd)
        })
    }
    $again = OpenSettings
    if ($again.Current.NativeWindowHandle -ne $settings.Current.NativeWindowHandle) { throw 'Settings opened a different window' }
    CloseSettings $settings
    foreach ($cycle in 1..6) {
        $settings = OpenSettings
        $text = WaitForTerminalText TerminalPreviewSample $settings $previewLines
        CheckRetainedSettings $settings $text $pt $height $face
        if ($cycle -eq 1) { CheckLiveCursor t75 Blinking Bar; CheckProgramColor t75 } # Opening Settings is not an edit.
        $pt = if ($pt -eq 13.5) { 24 } else { 13.5 }
        (FindId TerminalFontSize $settings).GetCurrentPattern([RangeValuePattern]::Pattern).SetValue($pt)
        $height = WaitFor "fresh $pt pt edit in both native buffers before close $cycle" {
            $h = TerminalRowHeight $live 'Fixture'
            if ($h -gt 0 -and $h -ne $height -and $h -eq (TerminalRowHeight $text 'git')) { $h }
        }
        CloseSettings $settings
    }
    $state.catalogEdit.SetResult('retitle') # Require fresh traffic on the original peer after the repeated teardowns.
    $personal.t75 = 't75 · π - Porchlight'
    CheckScope core-local Personal $personal t75
    $mounted = WaitForTerminalText PaneHost $root 'Fixture t75'
    if (!$originalPane.Equals($mounted.DocumentRange.GetEnclosingElement()) -or $mounted.DocumentRange.GetText(-1).Contains($previewLines[0])) {
        throw 'Settings replaced the active terminal or replayed the specimen into its buffer'
    }
    CheckLiveCursor t75 Blinking Bar # No CLOSE/attach was authorized throughout these edits.
    CheckProgramColor t75

    SelectSpace core-local Unassigned $unassigned t77
    $next = WaitForTerminalText PaneHost $root 'Fixture t77'
    [void](WaitFor 'later mount uses the edited font face and cell height' { (NativeFace $next) -ceq $face -and (TerminalRowHeight $next 'Fixture') -eq $height })
    CheckLiveCursor t77 Steady Underscore # A fresh pane gets the saved steady underline, not t75's override.
    $settings = OpenSettings
    $text = WaitForTerminalText TerminalPreviewSample $settings $previewLines
    CheckRetainedSettings $settings $text $pt $height $face
    CheckLiveCursor t77 Blinking Underscore -programBlinks
    (FindId ResetFontsButton $settings).GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    [void](WaitFor 'reset to original launch family list, without resetting feature edits' {
        !(FindId ResetFontsButton $settings).Current.IsEnabled -and (TokensAre (FindId TerminalFontPicker $settings) @()) -and
            (NativeFace $text) -ceq $launchFace -and (NativeFace $next) -ceq $launchFace -and
            (TokensAre (FindId FeaturePicker $settings) zero) -and (FindId ResetFeaturesButton $settings).Current.IsEnabled
    })
    (FindId ResetFeaturesButton $settings).GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    [void](WaitFor 'reset to original launch ss01 and ligatures, without resetting size or cursor' {
        (TokensAre (FindId FeaturePicker $settings) ss01) -and !(FindId ResetFeaturesButton $settings).Current.IsEnabled -and
            (FindId CodingLigaturesSwitch $settings).GetCurrentPattern([TogglePattern]::Pattern).Current.ToggleState -eq 'On' -and
            (FindId TerminalFontSize $settings).GetCurrentPattern([RangeValuePattern]::Pattern).Current.Value -eq $pt -and
            (FindId TerminalPreviewSample $settings).Current.HelpText -ceq 'Underscore cursor'
    })
    CheckLiveCursor t77 Blinking Underscore # Neither resetting fonts nor restoring launch feature overrides resets the program's mode.
    CloseSettings $settings
}
