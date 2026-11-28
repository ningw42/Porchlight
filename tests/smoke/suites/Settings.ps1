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
function CheckFontPicker($settings) {
    $picker = WaitFor 'terminal font picker' {
        $p = FindId TerminalFontPicker $settings
        if ($p -and $p.Current.IsEnabled -and !$p.Current.IsOffscreen) { $p }
    }
    $reset = WaitFor 'reset fonts button' { FindId ResetFontsButton $settings }
    $sample = WaitFor 'terminal preview sample' { FindId TerminalPreviewSample $settings }
    $text = $sample.GetCurrentPattern([TextPattern]::Pattern)
    $launchFont = $text.DocumentRange.GetAttributeValue([TextPattern]::FontNameAttribute)
    $value = $picker.GetCurrentPattern([ValuePattern]::Pattern)
    if ($value.Current.Value -or $reset.Current.IsEnabled) { throw 'Expected an empty picker inheriting the launch fonts' }

    # The comma commits the name through the picker's own delimiter handling. No keyboard focus or injected keys.
    $value.SetValue('consolas,')
    $token = [PropertyCondition]::new([AutomationElement]::NameProperty, 'Consolas')
    [void](WaitFor 'Consolas token and preview' {
        $reset.Current.IsEnabled -and $picker.FindFirst('Descendants', $token) -and
            $text.DocumentRange.GetAttributeValue([TextPattern]::FontNameAttribute) -ceq 'Consolas'
    })
    $reset.GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    [void](WaitFor 'reset to inherited fonts' {
        !$reset.Current.IsEnabled -and !$value.Current.Value -and !$picker.FindFirst('Descendants', $token) -and
            $text.DocumentRange.GetAttributeValue([TextPattern]::FontNameAttribute) -ceq $launchFont
    })
}

function CheckFontSize($settings) {
    $size = WaitFor 'font size editor' { FindId TerminalFontSize $settings }
    $range = $size.GetCurrentPattern([RangeValuePattern]::Pattern)
    $sample = (FindId TerminalPreviewSample $settings).GetCurrentPattern([TextPattern]::Pattern)
    $up = FindId UpSpinButton $size
    if (!$up -or $up.Current.IsOffscreen -or $range.Current.Value -ne 14) { throw 'Expected an inline font size editor showing the launch size' }
    $launchSize = $sample.DocumentRange.GetAttributeValue([TextPattern]::FontSizeAttribute)

    $edit = FindId InputBox $size
    $inputValue = $edit.GetCurrentPattern([ValuePattern]::Pattern)
    function CommitSizeText([string]$text) {
        $edit.SetFocus()
        $inputValue.SetValue($text)
        if ($CheckFocus) { SendSettingsKey $settings 0x0d 0x1c } # Enter
        else { (FindId TerminalCategory $settings).SetFocus() }
    }
    CommitSizeText (12.5).ToString([cultureinfo]::CurrentCulture)
    [void](WaitFor 'fractional size applied to the preview' {
        $range.Current.Value -eq 12.5 -and $sample.DocumentRange.GetAttributeValue([TextPattern]::FontSizeAttribute) -lt $launchSize
    })
    $fractionalSize = $sample.DocumentRange.GetAttributeValue([TextPattern]::FontSizeAttribute)
    $up.GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    [void](WaitFor 'one-point spin step preserves the fraction' {
        $range.Current.Value -eq 13.5 -and $sample.DocumentRange.GetAttributeValue([TextPattern]::FontSizeAttribute) -gt $fractionalSize
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

    function ChipsAre([string[]]$expected) {
        # Count token containers, not matching TextBlocks (the popup can contain the same tag).
        if ($value.Current.Value) { return $false } # Also wait for the delimiter to be consumed.
        $names = foreach ($item in $picker.FindAll('Children', $listItem)) {
            if (FindId PART_RemoveButton $item) { $item.Current.Name }
        }
        (($names | Sort-Object) -join ',') -ceq (($expected | Sort-Object) -join ',')
    }
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

    [void](WaitFor 'launch ligatures on, no individual chips' { (ChipsAre @()) -and $switch.Current.ToggleState -eq 'On' -and !$reset.Current.IsEnabled })
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
    $reset.GetCurrentPattern([InvokePattern]::Pattern).Invoke()
    [void](WaitFor 'reset restores launch switch and individual tags' { (ChipsAre @()) -and $switch.Current.ToggleState -eq 'On' -and !$reset.Current.IsEnabled })

}

Invoke-SmokeFixture -FontArguments @('--face="Unavailable, Fixture", Cascadia Code, Consolas') -BeforeConnect {
    [void](WaitFor 'Settings available before the gateway hello' { SettingsButton })
    CheckNoGatewayControls $root
} -Test {
    CheckNoGatewayControls $root
    SelectSpace core-local Personal $personal t75
    $settings = OpenSettings
    if ($CheckFocus) { CheckCategoryFocus $settings }
    CheckScope core-local Personal $personal t75
    CheckSettingsLayout $settings
    CheckFontPicker $settings
    CheckFontSize $settings
    CheckOpenTypePicker $settings
    if ($CheckFocus) {
        [void](WaitFor 'main window active with Settings open' {
            [PorchlightChrome]::GetForegroundWindow() -eq $hwnd -or [PorchlightChrome]::SetForegroundWindow($hwnd)
        })
    }
    $again = OpenSettings
    if ($again.Current.NativeWindowHandle -ne $settings.Current.NativeWindowHandle) { throw 'Settings opened a different window' }
    CloseSettings $settings
    CheckScope core-local Personal $personal t75
}
