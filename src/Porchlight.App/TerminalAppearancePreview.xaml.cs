using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Porchlight.App;

public sealed partial class TerminalAppearancePreview : UserControl, ITerminalAppearanceTarget
{
    internal ulong OwningHwnd { private get; set; }
    PreviewConnection? output;
    internal event Action<Size>? CellMeasured;

    public TerminalAppearancePreview()
    {
        InitializeComponent();
        Unloaded += (_, _) => Close();
    }

    internal void Close()
    {
        if (output == null) return;
        output.Close();
        output = null;
        PreviewHost.Close(TerminalPreviewSample);
    }

    void ITerminalAppearanceTarget.ApplyAppearance(TerminalAppearance appearance)
    {
        ToolTipService.SetToolTip(TerminalPreviewSample, appearance.FontFace);
        AutomationProperties.SetHelpText(TerminalPreviewSample, appearance.BlinkEnabled
            ? $"Blinking {appearance.Cursor} cursor" : $"{appearance.Cursor} cursor");
        try
        {
            var settings = appearance.CreateSettings();
            var connection = output ?? new PreviewConnection(DispatcherQueue, FitSample);
            connection.CursorBlink = appearance.BlinkEnabled;
            PreviewHost.Show(TerminalPreviewSample, OwningHwnd, settings, connection);
            output = connection;
            connection.Refresh();
        }
        catch (Exception ex) when (TerminalAppearance.IsNativeFailure(ex))
        {
            Close();
            TerminalPreviewSample.Child = new TextBlock { Text = "Terminal preview unavailable.", Margin = new(12) };
        }
    }

    void FitSample(PreviewConnection source)
    {
        if (source != output) return;
        var cell = PreviewHost.Fit(TerminalPreviewSample);
        CellMeasured?.Invoke(cell);
    }
}
