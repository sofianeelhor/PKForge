using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// Text for the Skia-drawn summary: the NDS12 pixel face, with per-glyph fallback to the
/// bundled rounded face (Japanese nicknames) and then the system (✓ ✦ ♥ …) - what MAUI
/// labels did implicitly. Fonts and fallback faces are cached; UI thread only.
/// </summary>
internal static class SummaryInk
{
    private static readonly Dictionary<(IntPtr Face, float Size, bool Bold), SKFont> Fonts = new();
    private static readonly Dictionary<int, SKTypeface> Fallbacks = new();
    private static readonly SKPaint Fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly List<(string Text, SKTypeface Face)> Scratch = new();

    public static bool FaceReady => !ReferenceEquals(PixelFont.Face, SKTypeface.Default);

    private static SKFont Font(SKTypeface face, float size, bool bold)
    {
        var key = (face.Handle, size, bold);
        if (!Fonts.TryGetValue(key, out var font))
        {
            font = new SKFont(face, size) { Edging = SKFontEdging.Antialias, Subpixel = true, Embolden = bold };
            Fonts[key] = font;
        }
        return font;
    }

    private static SKTypeface FaceFor(int rune)
    {
        var pixel = PixelFont.Face;
        if (rune == ' ' || pixel.GetGlyph(rune) != 0) return pixel;
        if (Fallbacks.TryGetValue(rune, out var cached)) return cached;
        var rounded = PixelFont.FallbackFace;
        var face = rounded.GetGlyph(rune) != 0 ? rounded : SKFontManager.Default.MatchCharacter(rune) ?? SKTypeface.Default;
        // Only remember real answers: the bundled faces load asynchronously.
        if (!ReferenceEquals(rounded, SKTypeface.Default)) Fallbacks[rune] = face;
        return face;
    }

    /// <summary>Splits text into runs that one face can draw.</summary>
    private static List<(string Text, SKTypeface Face)> Runs(string text)
    {
        Scratch.Clear();
        if (text.Length == 0) return Scratch;
        SKTypeface? current = null;
        var start = 0;
        var i = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var face = FaceFor(rune.Value);
            if (current is null) current = face;
            else if (!ReferenceEquals(face, current))
            {
                Scratch.Add((text[start..i], current));
                start = i;
                current = face;
            }
            i += rune.Utf16SequenceLength;
        }
        Scratch.Add((text[start..], current!));
        return Scratch;
    }

    public static float Width(string text, float size, bool bold = false)
    {
        var width = 0f;
        foreach (var (run, face) in Runs(text)) width += Font(face, size, bold).MeasureText(run);
        return width;
    }

    /// <summary>Draws one line. <paramref name="baseline"/> is the text baseline.</summary>
    public static float Draw(SKCanvas c, string text, float x, float baseline, float size, SKColor color,
        bool bold = false, SKTextAlign align = SKTextAlign.Left)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var width = Width(text, size, bold);
        var left = align switch
        {
            SKTextAlign.Center => x - width / 2,
            SKTextAlign.Right => x - width,
            _ => x,
        };
        Fill.Color = color;
        foreach (var (run, face) in Runs(text).ToArray())
        {
            var font = Font(face, size, bold);
            c.DrawText(run, left, baseline, SKTextAlign.Left, font, Fill);
            left += font.MeasureText(run);
        }
        return width;
    }

    /// <summary>The baseline that centres a line of <paramref name="size"/> on <paramref name="centerY"/>.</summary>
    public static float Center(float centerY, float size) => centerY + size * 0.36f;

    /// <summary>Line pitch for wrapped text.</summary>
    public static float Leading(float size) => size * 1.32f;

    /// <summary>Greedy word wrap; words longer than the width break by character.</summary>
    public static List<string> Wrap(string text, float size, float width, bool bold = false)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r", "").Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (Width(candidate, size, bold) <= width || line.Length == 0 && word.Length <= 1)
                {
                    line = candidate;
                    continue;
                }
                if (line.Length > 0) lines.Add(line);
                line = word;
                while (line.Length > 1 && Width(line, size, bold) > width)
                {
                    var cut = line.Length - 1;
                    while (cut > 1 && Width(line[..cut], size, bold) > width) cut--;
                    lines.Add(line[..cut]);
                    line = line[cut..];
                }
            }
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>One line, ellipsized to <paramref name="width"/>.</summary>
    public static string Fit(string text, float size, float width, bool bold = false)
    {
        if (Width(text, size, bold) <= width) return text;
        var cut = text.Length;
        while (cut > 0 && Width(text[..cut] + "…", size, bold) > width) cut--;
        return text[..cut] + "…";
    }
}

/// <summary>
/// The summary's chrome, drawn with the app's own PKSM vocabulary so it reads as the same
/// app as the box browser and the menus: navy device panels with the cobalt bezel, cobalt
/// header strips with a soft top light, the navy/cobalt button rows for tabs, cyan only
/// for focus. Each page keeps one accent - toned to the logo palette - on its header
/// strips and its active tab. Small radii (3-6 dp) everywhere, like the rest of the app.
/// </summary>
internal static class SummaryChrome
{
    private static readonly SKPaint Fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint Line = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };

    public const float PanelRadius = 6;
    public const float StripRadius = 4;

    public static SKColor Accent(SummaryPage page) => page switch
    {
        SummaryPage.Info => Pksm.BandInfo,
        SummaryPage.Stats => Pksm.BandStats,
        SummaryPage.Moves => Pksm.BandMoves,
        SummaryPage.Origin => Pksm.BandOrigin,
        _ => Pksm.BandLegal,
    };

    public static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t),
        (byte)(a.Alpha + (b.Alpha - a.Alpha) * t));

    public static SKColor Lighter(SKColor c, float t) => Mix(c, SKColors.White, t);
    public static SKColor Darker(SKColor c, float t) => Mix(c, SKColors.Black, t);

    private static void Gradient(SKCanvas c, SKRect r, float radius, SKColor top, SKColor bottom)
    {
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), [top, bottom], SKShaderTileMode.Clamp);
        Fill.Shader = shader;
        Fill.Color = SKColors.White;
        c.DrawRoundRect(r, radius, radius, Fill);
        Fill.Shader = null;
    }

    private static void Stroke(SKCanvas c, SKRect r, float radius, SKColor color, float width)
    {
        Line.Color = color;
        Line.StrokeWidth = width;
        var inset = width / 2;
        c.DrawRoundRect(SKRect.Inflate(r, -inset, -inset), radius, radius, Line);
    }

    /// <summary>
    /// The device panel (Kit.DevicePanel / PksmPaint.Panel): navy body with a whisper of
    /// vertical gradient, the 2 dp cobalt bezel and the hard pixel drop shadow.
    /// </summary>
    public static void Panel(SKCanvas c, SKRect r, SKColor? fill = null, bool shadow = true)
    {
        var body = fill ?? Pksm.Paper;
        if (shadow)
        {
            Fill.Color = Pksm.LogoVoid.WithAlpha(0x88);
            c.DrawRoundRect(new SKRect(r.Left + 3, r.Top + 3, r.Right + 3, r.Bottom + 3), PanelRadius, PanelRadius, Fill);
        }
        // Solid body (cheap to raster on tall pages) with the soft light along its top edge.
        Fill.Color = body;
        c.DrawRoundRect(r, PanelRadius, PanelRadius, Fill);
        var light = new SKRect(r.Left + 2, r.Top + 2, r.Right - 2, r.Top + Math.Min(26, r.Height / 3));
        Gradient(c, light, PanelRadius - 2, Lighter(body, 0.05f), body);
        Stroke(c, r, PanelRadius, Pksm.PaperEdge, 2);
    }

    /// <summary>
    /// A header strip (Kit.HeaderBar): the accent body with a soft top light, the dark
    /// outline and a white caption. Cobalt by default, the page accent on section heads.
    /// </summary>
    public static void Strip(SKCanvas c, SKRect r, string title, SKColor? accent = null, float size = 13.5f, string? trailing = null, SKColor? trailingColor = null)
    {
        var body = accent ?? Pksm.HeaderBlue;
        Fill.Color = Pksm.ButtonBlueDeep;
        c.DrawRoundRect(r, StripRadius, StripRadius, Fill);
        var inner = SKRect.Inflate(r, -1.5f, -1.5f);
        Gradient(c, inner, StripRadius - 1, Lighter(body, 0.16f), Darker(body, 0.1f));
        // The soft 3DS highlight across the upper half.
        Fill.Color = SKColors.White.WithAlpha(0x16);
        c.DrawRoundRect(new SKRect(inner.Left + 1, inner.Top + 1, inner.Right - 1, inner.MidY), StripRadius - 2, StripRadius - 2, Fill);
        var baseline = SummaryInk.Center(r.MidY, size);
        SummaryInk.Draw(c, title, r.Left + 10 + 1, baseline + 1, size, Pksm.LogoVoid.WithAlpha(0x90), bold: true);
        SummaryInk.Draw(c, title, r.Left + 10, baseline, size, SKColors.White, bold: true);
        if (trailing is not null)
            SummaryInk.Draw(c, trailing, r.Right - 10, baseline, size - 1, trailingColor ?? Pksm.Ink, align: SKTextAlign.Right);
    }

    /// <summary>
    /// A tab, in the menu rows' language (DsFolderButton): resting = navy body, cobalt edge;
    /// active = the page accent with the pale focus rim the selected rows wear.
    /// </summary>
    public static void Tab(SKCanvas c, SKRect r, string title, bool active, SKColor accent, float size = 13f)
    {
        if (active)
        {
            Fill.Color = Pksm.ButtonBlueDeep;
            c.DrawRoundRect(r, StripRadius, StripRadius, Fill);
            var inner = SKRect.Inflate(r, -1.5f, -1.5f);
            Gradient(c, inner, StripRadius - 1, Lighter(accent, 0.18f), Darker(accent, 0.08f));
            Stroke(c, inner, StripRadius - 1, Pksm.Ink.WithAlpha(0x70), 1.2f);
            SummaryInk.Draw(c, title, r.MidX, SummaryInk.Center(r.MidY, size), size, SKColors.White, bold: true, align: SKTextAlign.Center);
        }
        else
        {
            Fill.Color = Pksm.ButtonBlueDeep;
            c.DrawRoundRect(r, StripRadius, StripRadius, Fill);
            var inner = SKRect.Inflate(r, -1, -1);
            Gradient(c, inner, StripRadius - 1, Lighter(Pksm.LogoDeck, 0.04f), Darker(Pksm.LogoDeck, 0.05f));
            Stroke(c, inner, StripRadius - 1, Pksm.LogoGrid, 1.5f);
            SummaryInk.Draw(c, title, r.MidX, SummaryInk.Center(r.MidY, size), size, Pksm.InkSoft, align: SKTextAlign.Center);
        }
    }

    /// <summary>A type plate: the type colour, a gentle top light, caps name (the games' own badge).</summary>
    public static void TypeBadge(SKCanvas c, SKRect r, int type)
    {
        if (!TypeFacts.IsValid(type) && type != TypeFacts.Stellar) return;
        var color = InfoKit.TypeColor(type);
        var body = new SKColor((byte)(color.Red * 255), (byte)(color.Green * 255), (byte)(color.Blue * 255));
        Gradient(c, r, 3, Lighter(body, 0.12f), Darker(body, 0.08f));
        Stroke(c, r, 3, Darker(body, 0.28f), 1);
        var lum = 0.299f * body.Red + 0.587f * body.Green + 0.114f * body.Blue;
        var ink = lum >= 150 ? Pksm.LogoVoid : SKColors.White;
        var size = Math.Min(11.5f, r.Height * 0.62f);
        SummaryInk.Draw(c, TypeFacts.Name(type).ToUpperInvariant(), r.MidX, SummaryInk.Center(r.MidY, size), size, ink, align: SKTextAlign.Center);
    }

    /// <summary>A recessed gauge (base stats): dark track, rounded fill with a lit top edge.</summary>
    public static void Gauge(SKCanvas c, SKRect track, float fraction, SKColor color)
    {
        Fill.Color = Pksm.LogoVoid;
        c.DrawRoundRect(track, track.Height / 2, track.Height / 2, Fill);
        var fill = new SKRect(track.Left + 1, track.Top + 1, track.Left + 1 + Math.Max(track.Height, (track.Width - 2) * Math.Clamp(fraction, 0, 1)), track.Bottom - 1);
        Gradient(c, fill, fill.Height / 2, Lighter(color, 0.22f), color);
    }

    public static void Divider(SKCanvas c, float left, float right, float y)
    {
        Fill.Color = Pksm.PaperEdge.WithAlpha(0x70);
        c.DrawRect(new SKRect(left, y, right, y + 1), Fill);
    }

    public static void Band(SKCanvas c, SKRect r, SKColor color)
    {
        Fill.Color = color;
        c.DrawRect(r, Fill);
    }
}
