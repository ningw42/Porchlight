using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Graphics.Canvas.Text;
using Porchlight.Core.Configuration;
using Windows.UI.Text;
using WinRT;
using WinRT.Interop;

namespace Porchlight.App;

internal sealed record FontFeatureCandidate(string Tag, string? Sample, bool Italic = false);
internal sealed record FontFeatureProbeResult(IReadOnlyList<FontFeatureCandidate> Features, string? LigatureSample);

internal static class FontFeatureProbe
{
    internal const float SampleFontSize = 18;
    internal const string SampleLocale = "en-US";
    const int MaxComparisonsPerStyle = 1024;

    // Inputs to try, not a feature catalog. A string is never returned on the strength of this list.
    static readonly string[] Specimens =
    [
        "=> !=", "0O", "office", "AV To", "1/2", "1Il", "cursive", "0123456789", "1st 2nd 3rd",
        "-> <- <=>", "== === !==", "~=", "// /* */", "<= >=", ":: := |> <|", "&& || !!", "... ---", "␀␉␊",
        "fi fl ffi", "st ct Th", "aegfr", "(H) [H]", "@#$%&", "i + j", "QJRW", "2x + 3y",
        "abcdefghi", "jklmnopqr", "stuvwxyz", "ABCDEFGHI", "JKLMNOPQR", "STUVWXYZ",
        "q\u0323\u0301", "Αβθφ", "бгджт", "שָׁלוֹם", "سلام", "لَا سَلَام", "क्षि", "বাংলা", "ภาษาไทย",
        "亜悪圧囲", "国学体", "あアカナ", "ｱｲｳ", "ＡＢ１２", "。、（）", "한글",
    ];

    static readonly string[] Operators =
    [
        "=>", "!=", "->", "==", "<=", ">=", "<-", "&&", "||", "::", ":=", "|>", "<|", "~=", "++",
        "--", "**", "//", "/*", "*/", "<<", ">>", "??", "?.", "===", "!==", "<=>", "...", ">>>",
    ];

    internal static FontFeatureProbeResult Discover(string family, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(family)) return new([], null);

        // Exact WWS family (any locale), not a layout resolved with system fallback. Don't close
        // shared Win2D system-set/face wrappers: another probe or a layout can be using them.
        var systemFonts = CanvasFontSet.GetSystemFontSet();
        using var familyFonts = systemFonts.GetMatchingFonts([new(CanvasFontPropertyIdentifier.FamilyName, family, "")]);
        var tags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var face in familyFonts.Fonts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenTypeFeatureTags.Collect(face, tags, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (tags.Count == 0) return new([], null);

        bool probeLigatures = CodingLigatures.Tags.Any(tags.Contains);
        tags.RemoveWhere(CodingLigatures.IsExcludedFromPicker);
        var candidates = tags.ToDictionary(FeatureName, t => new FontFeatureCandidate(t, null));
        string? ligatureSample;
        using (var normal = Format(family, FontStyle.Normal))
        {
            var probe = new StyleProbe(familyFonts, normal, cancellationToken);
            ligatureSample = probeLigatures ? probe.FindLigatureSample() : null;
            probe.FindSamples(candidates);
        }
        if (candidates.Values.Any(c => c.Sample == null))
        {
            using var italic = Format(family, FontStyle.Italic);
            new StyleProbe(familyFonts, italic, cancellationToken).FindSamples(candidates);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(candidates.Values.ToArray(), ligatureSample);
    }

    // DWRITE_MAKE_OPENTYPE_TAG: include raw values not named by the Win2D enum.
    static CanvasTypographyFeatureName FeatureName(string t) =>
        (CanvasTypographyFeatureName)(t[0] | t[1] << 8 | t[2] << 16 | t[3] << 24);

    static CanvasTextFormat Format(string family, FontStyle style) => new()
    {
        FontFamily = family, FontSize = SampleFontSize, LocaleName = SampleLocale,
        FontWeight = new FontWeight { Weight = 400 }, FontStretch = FontStretch.Normal, FontStyle = style,
    };

    static CanvasTypography Typography(uint value, params ReadOnlySpan<CanvasTypographyFeatureName> tags)
    {
        var settings = new CanvasTypography();
        foreach (var tag in tags) settings.AddFeature(tag, value);
        return settings;
    }

    sealed record Run(CanvasTextAnalyzer Analyzer, CanvasCharacterRange Range, CanvasFontFace Face, int FaceIndex,
        CanvasAnalyzedScript Script, bool Rtl)
    {
        CanvasGlyph[] Shape(CanvasTypography settings) => Analyzer.GetGlyphs(Range, Face, SampleFontSize,
            false, Rtl, Script, SampleLocale, null, [new(Range, settings)], out _, out _, out _);

        public bool Changes(CanvasTypography off, CanvasTypography on) => Different(Shape(off), Shape(on));
    }

    sealed class StyleProbe(CanvasFontSet familyFonts, CanvasTextFormat format, CancellationToken token)
    {
        // Per family/style/locale and probe invocation. Cache only analysis of this exact isolated text,
        // including failures; never reuse a containing word's shaping runs or glyph cluster indices.
        readonly Dictionary<string, Run?> runs = new(StringComparer.Ordinal);

        Run? Analyze(string text)
        {
            token.ThrowIfCancellationRequested();
            if (!runs.TryGetValue(text, out var run)) runs[text] = run = AnalyzeIsolated(text);
            return run;
        }

        Run? AnalyzeIsolated(string text)
        {
            // Win2D 1.4 projects IDisposable here, but the native analyzer does not implement IClosable.
            var analyzer = new CanvasTextAnalyzer(text, CanvasTextDirection.LeftToRightThenTopToBottom);
            var range = new CanvasCharacterRange(0, text.Length);
            var fonts = analyzer.GetFonts(format);
            if (fonts.Count != 1 || fonts[0].Key != range) return null;
            var mapped = fonts[0].Value;
            var face = mapped.FontFace;
            if (mapped.ScaleFactor != 1 || !familyFonts.TryFindFontFace(face, out int index)) return null;
            if (face.GetGlyphIndices(text.EnumerateRunes().Select(r => (uint)r.Value).ToArray()).Contains(0)) return null;
            var scripts = analyzer.GetScript(SampleLocale);
            var levels = analyzer.GetBidi(SampleLocale);
            if (scripts.Count != 1 || scripts[0].Key != range || levels.Count != 1 || levels[0].Key != range) return null;
            return new(analyzer, range, face, index, scripts[0].Value, (levels[0].Value.ResolvedLevel & 1) != 0);
        }

        public string? FindLigatureSample()
        {
            var group = CodingLigatures.Tags.Select(FeatureName).ToArray();
            using var off = Typography(0, group);
            using var on = Typography(1, group);
            foreach (var op in Operators)
                if (Analyze(op) is { } run && run.Face.Style == FontStyle.Normal && run.Changes(off, on)) return op;
            return null;
        }

        public void FindSamples(Dictionary<CanvasTypographyFeatureName, FontFeatureCandidate> candidates)
        {
            int remaining = candidates.Values.Count(c => c.Sample == null), comparisons = 0;
            bool italic = format.FontStyle == FontStyle.Italic;
            foreach (var sample in Specimens)
            {
                if (remaining == 0 || comparisons >= MaxComparisonsPerStyle) return;
                if (Analyze(sample) is not { } run) continue;
                // The script/language list only narrows the sample search, not the family-wide picker.
                foreach (var tag in run.Face.GetSupportedTypographicFeatureNames(run.Script, SampleLocale))
                {
                    token.ThrowIfCancellationRequested();
                    if (comparisons >= MaxComparisonsPerStyle) return;
                    if (!candidates.TryGetValue(tag, out var candidate) || candidate.Sample != null) continue;
                    using var off = Typography(0, tag);
                    using var on = Typography(1, tag);
                    comparisons++;
                    if (!run.Changes(off, on)) continue;

                    // Finish minimizing a match even at the budget boundary. At worst this adds
                    // n(n+1)/2 - 1 comparisons for one of the fixed, short n-grapheme specimens.
                    var shortest = FontSampleMinimizer.ShortestVerified(sample, slice =>
                    {
                        if (Analyze(slice) is not { } part || part.FaceIndex != run.FaceIndex) return false;
                        comparisons++;
                        return part.Changes(off, on);
                    }, token);
                    candidates[tag] = candidate with { Sample = shortest, Italic = italic };
                    remaining--;
                }
            }
        }
    }

    static bool Different(CanvasGlyph[] off, CanvasGlyph[] on)
    {
        if (off.Length == 0 || on.Length == 0 || off.Any(g => g.Index == 0) || on.Any(g => g.Index == 0)) return false;
        if (off.Length != on.Length) return true;
        float offPen = 0, onPen = 0;
        for (int i = 0; i < off.Length; i++)
        {
            var a = off[i];
            var b = on[i];
            if (a.Index != b.Index || MathF.Abs(a.AscenderOffset - b.AscenderOffset) > 0.001f ||
                MathF.Abs((offPen + a.AdvanceOffset) - (onPen + b.AdvanceOffset)) > 0.001f) return true;
            offPen += a.Advance;
            onPen += b.Advance;
        }
        return false; // A change only to trailing advance doesn't move any glyph.
    }
}

// Win2D exposes feature enumeration only for a given script/language. The GSUB and GPOS
// FeatureLists contain all raw tags, including private ones and ones with no horizontal sample.
file static unsafe class OpenTypeFeatureTags
{
    internal static void Collect(CanvasFontFace face, HashSet<string> tags, CancellationToken token)
    {
        // Microsoft.Graphics.Canvas.native.h: the public ICanvasResourceWrapperNative interop
        // on a CanvasFontFace wraps an IDWriteFontFaceReference (not a font file or face index).
        Guid wrapperId = new("5F10688D-EA55-4D55-A3B0-4DDB55C0C20A");
        Guid referenceId = new("5E7FA7CA-DDE3-424C-89F0-9FCD6FED58CD");
        using var wrapper = ((IWinRTObject)face).NativeObject.As<IUnknownVftbl>(wrapperId);
        nint w = wrapper.ThisPtr, reference = 0, nativeFace = 0;
        var getNativeResource = (delegate* unmanaged[Stdcall]<nint, nint, float, Guid*, nint*, int>)(*(nint**)w)[3];
        Marshal.ThrowExceptionForHR(getNativeResource(w, 0, 0, &referenceId, &reference));
        try
        {
            // IDWriteFontFaceReference::CreateFontFace. A font no longer local is not a candidate.
            var createFace = (delegate* unmanaged[Stdcall]<nint, nint*, int>)(*(nint**)reference)[3];
            if (createFace(reference, &nativeFace) < 0) return;
            try
            {
                ReadTable(nativeFace, 0x42555347, tags, token); // DWRITE_MAKE_OPENTYPE_TAG('G','S','U','B')
                ReadTable(nativeFace, 0x534F5047, tags, token); // GPOS
            }
            finally { Marshal.Release(nativeFace); }
        }
        finally { Marshal.Release(reference); }
    }

    static void ReadTable(nint face, uint tableTag, HashSet<string> tags, CancellationToken token)
    {
        // IDWriteFontFace::TryGetFontTable, ReleaseFontTable (dwrite.h).
        var vtable = *(nint**)face;
        var getTable = (delegate* unmanaged[Stdcall]<nint, uint, byte**, uint*, nint*, int*, int>)vtable[12];
        var releaseTable = (delegate* unmanaged[Stdcall]<nint, nint, void>)vtable[13];
        byte* data = null;
        uint size = 0;
        nint context = 0;
        int exists = 0;
        if (getTable(face, tableTag, &data, &size, &context, &exists) < 0 || exists == 0) return;
        try
        {
            if (size < 10 || size > int.MaxValue) return;
            var table = new ReadOnlySpan<byte>(data, (int)size);
            if (BinaryPrimitives.ReadUInt16BigEndian(table) != 1) return;
            int list = BinaryPrimitives.ReadUInt16BigEndian(table[6..]);
            if (list < 10 || list > table.Length - 2) return;
            int count = BinaryPrimitives.ReadUInt16BigEndian(table[list..]);
            if (count > (table.Length - list - 2) / 6) return;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                // FeatureRecord: Tag[4], Offset16.
                var bytes = table.Slice(list + 2 + i * 6, 4);
                if (bytes.ContainsAnyExceptInRange((byte)0x20, (byte)0x7e)) continue;
                tags.Add(Encoding.ASCII.GetString(bytes));
            }
        }
        finally { releaseTable(face, context); }
    }
}
