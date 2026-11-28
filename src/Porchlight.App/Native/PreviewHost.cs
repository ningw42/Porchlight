using Microsoft.Terminal.Control;
using Microsoft.Terminal.TerminalConnection;
using Microsoft.UI.Xaml.Controls;
using System.Runtime.InteropServices;
using WinRT;

namespace Porchlight.App;

internal static class PreviewHost
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [DllImport("Porchlight.NativeFeatures.dll", ExactSpelling = true)]
    static extern int ShowTerminalPreview(nint host, ulong hwnd, nint settings, nint connection);

    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [DllImport("Porchlight.NativeFeatures.dll", ExactSpelling = true)]
    static extern int FitTerminalPreview(nint host, out float cellWidth, out float cellHeight);

    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [DllImport("Porchlight.NativeFeatures.dll", ExactSpelling = true)]
    static extern int CloseTerminalPreview(nint host);

    internal static void Show(Border host, ulong hwnd, IControlSettings settings, ITerminalConnection connection)
    {
        using var parent = MarshalInspectable<Border>.CreateMarshaler(host);
        using var appearance = MarshalInterface<IControlSettings>.CreateMarshaler(settings);
        using var output = MarshalInterface<ITerminalConnection>.CreateMarshaler(connection);
        Marshal.ThrowExceptionForHR(ShowTerminalPreview(parent.ThisPtr, hwnd, appearance.ThisPtr, output.ThisPtr));
    }

    internal static Windows.Foundation.Size Fit(Border host)
    {
        using var parent = MarshalInspectable<Border>.CreateMarshaler(host);
        Marshal.ThrowExceptionForHR(FitTerminalPreview(parent.ThisPtr, out var width, out var height));
        return new(width, height);
    }

    internal static void Close(Border host)
    {
        using var parent = MarshalInspectable<Border>.CreateMarshaler(host);
        Marshal.ThrowExceptionForHR(CloseTerminalPreview(parent.ThisPtr));
    }
}
