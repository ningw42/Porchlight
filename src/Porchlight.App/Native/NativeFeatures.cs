using System.Collections.Generic;
using System.Runtime.InteropServices;
using WinRT;

namespace Porchlight.App;

public static class NativeFeatures
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [DllImport("Porchlight.NativeFeatures.dll", ExactSpelling = true, CallingConvention = CallingConvention.StdCall)]
    private static extern int CreateNativeFontMap(out nint map);

    public static IDictionary<string, float> Create(IReadOnlyDictionary<string, float> settings)
    {
        Marshal.ThrowExceptionForHR(CreateNativeFontMap(out var map));
        try
        {
            // Populate the native IMap before handing it to the control. Its HSTRING keys
            // live with the map; these are not borrowed entries of a managed Dictionary.
            var native = MarshalInterface<IDictionary<string, float>>.FromAbi(map);
            foreach (var (tag, value) in settings) native.Add(tag, value);
            return native;
        }
        finally
        {
            // FromAbi borrows the input and manages its own reference. Drop the factory's +1.
            MarshalInterface<IDictionary<string, float>>.DisposeAbi(map);
        }
    }
}
