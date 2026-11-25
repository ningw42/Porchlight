#include <unknwn.h>
#include <winrt/Windows.Foundation.Collections.h>

// On success the caller owns one reference to IMap<hstring, float> (not IMapView).
extern "C" __declspec(dllexport) HRESULT __stdcall CreateNativeFontMap(void** result) noexcept
{
    if (!result) return E_POINTER;
    *result = nullptr;
    try
    {
        auto map = winrt::multi_threaded_map<winrt::hstring, float>();
        *result = winrt::detach_abi(map);
        return S_OK;
    }
    catch (...)
    {
        return winrt::to_hresult();
    }
}
