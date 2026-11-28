using Microsoft.Terminal.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Porchlight.Core.Configuration;
using System.Runtime.InteropServices;
using Windows.Foundation;

namespace Porchlight.App;

public sealed partial class TerminalAppearancePreview : UserControl
{
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

    internal void Update(ulong hwnd, TerminalScheme? scheme, TerminalFont font, CursorStyle cursor, bool blink)
    {
        ToolTipService.SetToolTip(TerminalPreviewSample, font.Face);
        AutomationProperties.SetHelpText(TerminalPreviewSample, blink ? $"Blinking {cursor} cursor" : $"{cursor} cursor");
        try
        {
            var settings = TerminalSettings.Create(font, scheme, cursor);
            var connection = output ?? new PreviewConnection(DispatcherQueue, FitSample);
            connection.CursorBlink = blink;
            PreviewHost.Show(TerminalPreviewSample, hwnd, settings, connection);
            output = connection;
            connection.Refresh();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or COMException)
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
