using System.Text;
using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// Per-glyph font fallback for the Skia painters. The NDS12 pixel face has Latin only: a
/// Japanese box name (<c>ボックス１</c>), a full-width nickname (<c>Ｂｒｕｈ</c>) or a <c>✓</c>
/// drew as blank boxes (QA 2026-11-12). Text the face covers is drawn exactly as before, in one
/// call (the pixel look of Latin text is untouched); otherwise it is split into runs, and each run
/// the face lacks goes to <see cref="Preferred"/> (the app's bundled M PLUS Rounded, full Japanese
/// coverage) or to whatever face the system has for that character. Answers are cached per
/// character and fonts per face and size.
/// </summary>
public static class TextFallback
{
    private static readonly object Gate = new();
    private static readonly Dictionary<int, SKTypeface> Faces = [];
    private static readonly Dictionary<(IntPtr Face, float Size, bool Bold, SKFontEdging Edging), SKFont> Fonts = [];
    private static SKTypeface? _preferred;

    /// <summary>The first fallback face (set by the app once its bundled face is loaded).</summary>
    public static SKTypeface? Preferred
    {
        get => Volatile.Read(ref _preferred);
        set
        {
            lock (Gate)
            {
                Volatile.Write(ref _preferred, value);
                Faces.Clear(); // answers given without it may now differ
            }
        }
    }

    /// <summary>True when <paramref name="font"/> can draw every visible character of the text.</summary>
    public static bool Covers(SKFont font, string text) => text.Length == 0 || font.ContainsGlyphs(text) || AllMissingAreBlank(font, text);

    private static bool AllMissingAreBlank(SKFont font, string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (!Rune.IsWhiteSpace(rune) && !Rune.IsControl(rune) && font.Typeface.GetGlyph(rune.Value) == 0)
                return false;
        return true;
    }

    /// <summary><see cref="SKCanvas.DrawText(string, float, float, SKTextAlign, SKFont, SKPaint)"/> with fallback.</summary>
    public static void DrawTextFallback(this SKCanvas canvas, string text, float x, float y, SKTextAlign align, SKFont font, SKPaint paint)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Covers(font, text))
        {
            canvas.DrawText(text, x, y, align, font, paint);
            return;
        }
        var runs = Runs(font, text);
        var width = 0f;
        foreach (var (run, runFont) in runs) width += runFont.MeasureText(run, paint);
        var left = align switch
        {
            SKTextAlign.Center => x - width / 2,
            SKTextAlign.Right => x - width,
            _ => x,
        };
        foreach (var (run, runFont) in runs)
        {
            canvas.DrawText(run, left, y, SKTextAlign.Left, runFont, paint);
            left += runFont.MeasureText(run, paint);
        }
    }

    /// <summary><see cref="SKFont.MeasureText(string, SKPaint)"/> with fallback.</summary>
    public static float MeasureFallback(this SKFont font, string text, SKPaint? paint = null)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (Covers(font, text)) return font.MeasureText(text, paint);
        var width = 0f;
        foreach (var (run, runFont) in Runs(font, text)) width += runFont.MeasureText(run, paint);
        return width;
    }

    /// <summary>The text cut into runs that one font can draw.</summary>
    public static List<(string Text, SKFont Font)> Runs(SKFont font, string text)
    {
        var runs = new List<(string, SKFont)>();
        SKFont? current = null;
        var start = 0;
        var i = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var runFont = Rune.IsWhiteSpace(rune) && current is not null ? current : FontFor(font, rune);
            if (current is null) current = runFont;
            else if (!ReferenceEquals(runFont, current))
            {
                runs.Add((text[start..i], current));
                start = i;
                current = runFont;
            }
            i += rune.Utf16SequenceLength;
        }
        if (current is not null) runs.Add((text[start..], current));
        return runs;
    }

    private static SKFont FontFor(SKFont font, Rune rune)
    {
        if (Rune.IsWhiteSpace(rune) || font.Typeface.GetGlyph(rune.Value) != 0) return font;
        SKTypeface face;
        lock (Gate)
        {
            if (!Faces.TryGetValue(rune.Value, out face!))
            {
                var preferred = Preferred;
                face = preferred is not null && preferred.GetGlyph(rune.Value) != 0
                    ? preferred
                    : SKFontManager.Default.MatchCharacter(rune.Value) ?? font.Typeface;
                Faces[rune.Value] = face;
            }
            if (ReferenceEquals(face, font.Typeface)) return font;
            var key = (face.Handle, font.Size, font.Embolden, font.Edging);
            if (!Fonts.TryGetValue(key, out var made))
            {
                made = new SKFont(face, font.Size) { Edging = font.Edging, Embolden = font.Embolden, Subpixel = font.Subpixel };
                Fonts[key] = made;
            }
            return made;
        }
    }
}
