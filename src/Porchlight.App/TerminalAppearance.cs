using Microsoft.Terminal.Control;
using Microsoft.Terminal.Core;
using Porchlight.Core.Configuration;
using System.Runtime.InteropServices;

namespace Porchlight.App;

// A renderer request, not the state of the running program. Null blink leaves a newly
// attached program alone until the user actually chooses a blink mode in Settings.
internal sealed record TerminalAppearance(TerminalFont Font, TerminalScheme? Scheme = null,
    CursorStyle Cursor = TerminalSettings.DefaultCursorShape, bool? Blink = null)
{
    // Null inherits the launch face verbatim. An explicit family list is still a choice
    // to restore in the picker even if it happens to spell the same list as the launch font.
    internal string? FamilyOverride { get; init; }
    internal string FontFace => FamilyOverride ?? Font.Face;
    internal bool BlinkEnabled => Blink ?? true;

    // Picker intent (inherited vs explicitly identical families) and the one-shot VT mode
    // are not native settings changes. Avoid reloading the program's appearance for either.
    internal bool HasSameControlSettings(TerminalAppearance other) =>
        Font == other.Font && FontFace == other.FontFace && Scheme == other.Scheme && Cursor == other.Cursor;

    internal static bool IsNativeFailure(Exception ex) =>
        ex is COMException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException;

    internal TerminalSettings CreateSettings() => new(this);

    internal TerminalSettings UpdateControl(TermControl control, TerminalSettings installed, TerminalAppearance previous)
    {
        if (HasSameControlSettings(previous)) return installed;
        if (Cursor == previous.Cursor && Scheme == previous.Scheme && Font.CellHeight == previous.Font.CellHeight &&
            Font.CellWidth == previous.Font.CellWidth && Font.BuiltinGlyphs == previous.Font.BuiltinGlyphs &&
            Font.ColorGlyphs == previous.Font.ColorGlyphs)
        {
            // In 0.1.0-alpha both font operations reread the retained face, weight, features
            // and axes. Neither reapplies the configured cursor/palette to the running program.
            installed.SetFont(this);
            if (Font.Size != previous.Font.Size) control.ResetFontSize();
            else control.AdjustFontSize(0); // Keep the user's current zoom.
            return installed;
        }
        var settings = CreateSettings();
        control.UpdateControlSettings(settings, null); // No distinct unfocused appearance.
        return settings;
    }
}

// UI-thread contract. Apply in place; an unchanged blink choice must not override later program output.
internal interface ITerminalAppearanceTarget
{
    void ApplyAppearance(TerminalAppearance appearance);
}
