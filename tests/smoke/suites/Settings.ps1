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
            foreach ($id in 'TerminalPageHeading','TerminalPreviewSample','FontSettingsCard','TerminalFontPicker','ResetFontsButton') {
                $control = FindId $id $settings
                $r = $control.Current.BoundingRectangle
                if (!$control -or $control.Current.IsOffscreen -or $r.IsEmpty -or $r.Left -lt $row.Right -or $r.Right -gt $window.Right) { return }
                if ($id -eq 'TerminalPageHeading' -and ($control.Current.ControlType -ne [ControlType]::Text -or $control.Current.Name -cne 'Terminal' -or
                    $control.GetCurrentPropertyValue([AutomationElement]::HeadingLevelProperty) -ne 'Level1')) { return }
                # Wait for the wide page to lay out, not just for the outer HWND to change size.
                if ($width -gt $narrow -and $id -eq 'ResetFontsButton' -and $r.Right -le $window.Left + $narrow * $scale) { return }
            }
            $sample = (FindId TerminalPreviewSample $settings).Current.BoundingRectangle
            if ($sample.Top -lt (FindId TerminalPageHeading $settings).Current.BoundingRectangle.Bottom -or
                $sample.Bottom -gt (FindId FontSettingsCard $settings).Current.BoundingRectangle.Top) { return }
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

Invoke-SmokeFixture -BeforeConnect {
    [void](WaitFor 'Settings available before the gateway hello' { SettingsButton })
    CheckNoGatewayControls $root
} -Test {
    CheckNoGatewayControls $root
    SelectSpace core-local Personal $personal t75
    $settings = OpenSettings
    CheckScope core-local Personal $personal t75
    CheckSettingsLayout $settings
    CheckFontPicker $settings
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
