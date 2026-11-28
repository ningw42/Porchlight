#include <unknwn.h>
#undef GetCurrentTime // WinBase macro conflicts with the WinUI animation projection.
#include <cmath>
#include <winrt/Windows.Foundation.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.Terminal.Control.h>
#include <winrt/Microsoft.Terminal.TerminalConnection.h>

namespace xaml = winrt::Microsoft::UI::Xaml;
namespace control = winrt::Microsoft::Terminal::Control;
using winrt::Microsoft::Terminal::TerminalConnection::ITerminalConnection;

// The alpha TermControl destroys XAML timers in its destructor, even after Close().
// Keep it in the XAML tree, not a C#/WinRT wrapper finalized on the GC thread.
static xaml::Controls::Border previewBorder(void* hostAbi)
{
    winrt::Windows::Foundation::IInspectable parent{ nullptr };
    winrt::copy_from_abi(parent, hostAbi);
    return parent.as<xaml::Controls::Border>();
}

extern "C" __declspec(dllexport) HRESULT __stdcall ShowTerminalPreview(void* hostAbi, uint64_t hwnd, void* settingsAbi, void* connectionAbi) noexcept
{
    try
    {
        auto host = previewBorder(hostAbi);
        control::IControlSettings settings{ nullptr };
        winrt::copy_from_abi(settings, settingsAbi);
        auto terminal = host.Child().try_as<control::TermControl>();
        if (terminal)
        {
            terminal.UpdateControlSettings(settings, settings);
        }
        else
        {
            ITerminalConnection connection{ nullptr };
            winrt::copy_from_abi(connection, connectionAbi);
            terminal = control::TermControl(settings, settings, connection);
            terminal.OwningHwnd(hwnd);
            terminal.IsEnabled(false);
            terminal.IsTabStop(false);
            terminal.AllowFocusWhenDisabled(false);
            terminal.IsHitTestVisible(false);
            terminal.SetReadOnly(true);
            terminal.CursorVisibility(control::CursorDisplayState::Shown);
            host.Child(terminal);
        }
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

// Called after replaying the sample, whose cursor is just after its last character.
// Include any rows that wrapped into history while the viewport was still short.
extern "C" __declspec(dllexport) HRESULT __stdcall FitTerminalPreview(void* hostAbi, float* cellWidth, float* cellHeight) noexcept
{
    try
    {
        auto terminal = previewBorder(hostAbi).Child().as<control::TermControl>();
        const auto cell = terminal.CharacterDimensions();
        *cellWidth = cell.Width;
        *cellHeight = cell.Height;
        const auto history = terminal.BufferHeight() - terminal.ViewHeight();
        const auto lastRowTop = history * cell.Height + terminal.CursorPositionInDips().Y;
        // MinimumSize reserves one whole cell plus padding, including the bottom inset.
        terminal.Height(std::ceil(lastRowTop + terminal.MinimumSize().Height));
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}

extern "C" __declspec(dllexport) HRESULT __stdcall CloseTerminalPreview(void* hostAbi) noexcept
{
    try
    {
        auto host = previewBorder(hostAbi);
        if (auto terminal = host.Child().try_as<control::TermControl>())
        {
            terminal.Close();
            host.Child(nullptr);
        }
        return S_OK;
    }
    catch (...) { return winrt::to_hresult(); }
}
