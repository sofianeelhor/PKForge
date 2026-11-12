using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The Pokémon editor panel in the designer's art direction: the name tab with level, shiny
/// star and gender, the cyan species tab with the EXP line, the slanted section chips between
/// fading rails, and the faint Pokémon watermark. Sizes are design pixels (the 1920×1080
/// mockup, where the panel is 740 wide) scaled by a unit.
/// </summary>
public static class EditorPaint
{
    private static ColorTheme T => ColorTheme.Current;

    public static SKColor Label => T.Label;
    public static SKColor Value => T.Value;
    public static SKColor Cyan => T.Rim;
    public static SKColor CyanFill => T.RimFill;
    public static SKColor ExpInk => T.SubInk;
    public static SKColor ChipInk => T.ChipInk;
    public static SKColor ChipTop => T.ChipTop;
    public static SKColor ChipBottom => T.ChipBottom;
    public static SKColor Band => T.Band;

    /// <summary>The panel width in the mockup: a panel's unit is its width over this.</summary>
    public const float DesignWidth = 740f;

    /// <summary>The header's height in the mockup: the name frame (72), a gap (4), the species tab (56).</summary>
    public const float DesignHeaderHeight = 132f;

    private static SKPaint Fill(SKColor c) => new() { Color = c, IsAntialias = true };
    private static SKPaint Stroke(SKColor c, float w) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w };

    /// <summary>The two alternating row bands: dark and light, over the well and its watermark.</summary>
    public static SKColor RowBand(bool dark) => Band.WithAlpha(dark ? (byte)150 : (byte)60);

    /// <summary>What the header shows for one Pokémon; <paramref name="Accent"/> tints the tabs (the Bank uses the first type).</summary>
    public sealed record Header(string Name, int Level, bool Shiny, int Gender, string Species, string? ExpLine, SKColor? Accent = null);

    /// <summary>
    /// The editor header over the top of the panel: name frame, name tab, level, star and
    /// gender glyphs, then the species tab and EXP line.
    /// </summary>
    public static void PaintHeader(SKCanvas c, SKRect r, Header header, SKTypeface typeface, float unit)
    {
        var edge = 3f * unit;
        var frame = new SKRect(r.Left, r.Top, r.Right, r.Top + 72f * unit);
        using (var fill = Fill(StoragePaint.Frame)) c.DrawRoundRect(frame, 18f * unit, 18f * unit, fill);
        using (var stroke = Stroke(StoragePaint.FrameEdge, edge))
            c.DrawRoundRect(SKRect.Inflate(frame, -edge / 2, -edge / 2), 18f * unit, 18f * unit, stroke);

        // The name tab: rounded on the left, its right edge slanting out toward the bottom.
        var name = new SKRect(r.Left, frame.Top, r.Left + 324f * unit, frame.Bottom);
        using (var path = Tab(name, 18f * unit, 26f * unit))
        {
            using (var gradient = new SKPaint
            {
                IsAntialias = true,
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, name.Top), new SKPoint(0, name.Bottom),
                    header.Accent is { } tint
                        ? [PksmPaint.Mix(tint, SKColors.White, 0.22f), PksmPaint.Mix(tint, SKColors.Black, 0.35f)]
                        : [StoragePaint.BannerTop, StoragePaint.BannerBottom], SKShaderTileMode.Clamp),
            })
                c.DrawPath(path, gradient);
            using var outline = Stroke(Pksm.Ink, edge);
            c.DrawPath(path, outline);
        }

        using var big = new SKFont(typeface, 34f * unit);
        Text(c, header.Name, name.Left + 24f * unit, frame.MidY, big, Pksm.Ink, name.Width - 60f * unit);
        Text(c, $"Lv. {header.Level}", r.Left + 348f * unit, frame.MidY, big, Pksm.Ink);
        var glyph = header.Gender switch { 0 => "♂", 1 => "♀", _ => null };
        if (glyph is not null) Centered(c, glyph, r.Right - 38f * unit, frame.MidY, big, Pksm.Ink);
        if (header.Shiny) Centered(c, "★", r.Right - 82f * unit, frame.MidY, big, Pksm.ShinyGold);

        // The species tab under it, cyan-rimmed, and the EXP line beside it.
        var species = new SKRect(r.Left, frame.Bottom + 4f * unit, r.Left + 262f * unit, frame.Bottom + 60f * unit);
        using (var path = Tab(species, 14f * unit, 22f * unit))
        {
            using (var fill = Fill(header.Accent is { } tint ? PksmPaint.Mix(tint, Band, 0.7f) : CyanFill)) c.DrawPath(path, fill);
            using var outline = Stroke(header.Accent is { } rim ? PksmPaint.Mix(rim, SKColors.White, 0.25f) : Cyan, edge);
            c.DrawPath(path, outline);
        }
        using var mid = new SKFont(typeface, 32f * unit);
        Text(c, header.Species, species.Left + 20f * unit, species.MidY, mid,
            header.Accent is { } ink ? PksmPaint.Mix(ink, SKColors.White, 0.5f) : Cyan, species.Width - 50f * unit);
        if (header.ExpLine is { } exp)
        {
            using var small = new SKFont(typeface, 22f * unit);
            Text(c, exp, r.Left + 284f * unit, species.MidY, small, ExpInk, r.Right - (r.Left + 284f * unit) - 20f * unit);
        }
    }

    /// <summary>
    /// A section divider: the title in a slanted chip, with a rail fading out to a diamond on
    /// each side.
    /// </summary>
    public static void PaintSection(SKCanvas c, SKRect r, string title, SKTypeface typeface, float unit)
    {
        using var font = new SKFont(typeface, 30f * unit);
        var chipWidth = font.MeasureFallback(title) + 64f * unit;
        var left = r.Left + 16f * unit;
        var right = r.Right - 16f * unit;
        var cx = (left + right) / 2;
        var cy = r.MidY;
        var chipLeft = cx - chipWidth / 2;
        var chipRight = cx + chipWidth / 2;

        // Rails: brightest at the chip, fading toward the diamonds at the ends.
        Rail(c, left + 10f * unit, chipLeft - 8f * unit, cy, unit, towardEnd: true);
        Rail(c, chipRight + 8f * unit, right - 10f * unit, cy, unit, towardEnd: false);
        using (var diamond = Fill(Cyan))
        {
            foreach (var x in new[] { left + 10f * unit, right - 10f * unit })
            {
                using var path = new SKPath();
                var s = 7f * unit;
                path.MoveTo(x, cy - s);
                path.LineTo(x + s, cy);
                path.LineTo(x, cy + s);
                path.LineTo(x - s, cy);
                path.Close();
                c.DrawPath(path, diamond);
            }
        }

        var slant = 15f * unit;
        var chip = new SKRect(chipLeft, cy - 25f * unit, chipRight, cy + 25f * unit);
        using var shape = new SKPath();
        shape.MoveTo(chip.Left + slant, chip.Top);
        shape.LineTo(chip.Right, chip.Top);
        shape.LineTo(chip.Right - slant, chip.Bottom);
        shape.LineTo(chip.Left, chip.Bottom);
        shape.Close();
        using (var gradient = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, chip.Top), new SKPoint(0, chip.Bottom),
                [ChipTop, ChipBottom], SKShaderTileMode.Clamp),
        })
            c.DrawPath(shape, gradient);
        using (var outline = Stroke(Cyan, 3f * unit)) c.DrawPath(shape, outline);
        Centered(c, title, cx, cy, font, ChipInk);
    }

    /// <summary>The Pokémon as a faint oversized watermark in the panel's lower right.</summary>
    public static void PaintWatermark(SKCanvas c, SKRect panel, SKImage sprite, float unit, SKSamplingOptions sampling)
    {
        var scale = 10f * unit * 56f / Math.Max(sprite.Width, sprite.Height) * 1.15f;
        var w = sprite.Width * scale;
        var h = sprite.Height * scale;
        var dest = new SKRect(panel.Right - w + 40f * unit, panel.Bottom - h + 10f * unit, panel.Right + 40f * unit, panel.Bottom + 10f * unit);
        using var paint = new SKPaint { Color = ColorTheme.Current.Bright.WithAlpha(24) };
        c.Save();
        c.ClipRoundRect(new SKRoundRect(panel, 24f * unit), antialias: true);
        c.DrawImage(sprite, dest, sampling, paint);
        c.Restore();
    }

    private static void Rail(SKCanvas c, float from, float to, float cy, float unit, bool towardEnd)
    {
        if (to <= from) return;
        // Bright at the chip end, faint at the diamond end.
        SKColor[] strong = towardEnd ? [Cyan.WithAlpha(20), Cyan.WithAlpha(220)] : [Cyan.WithAlpha(220), Cyan.WithAlpha(20)];
        SKColor[] soft = towardEnd ? [Cyan.WithAlpha(10), Cyan.WithAlpha(130)] : [Cyan.WithAlpha(130), Cyan.WithAlpha(10)];
        using var top = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(from, 0), new SKPoint(to, 0), strong, SKShaderTileMode.Clamp) };
        using var bottom = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(from, 0), new SKPoint(to, 0), soft, SKShaderTileMode.Clamp) };
        c.DrawRect(new SKRect(from, cy - 1f * unit, to, cy), top);
        c.DrawRect(new SKRect(from, cy, to, cy + 1f * unit), bottom);
    }

    // A tab shape: rounded left corners, right edge slanting out toward the bottom.
    private static SKPath Tab(SKRect r, float radius, float slant)
    {
        var path = new SKPath();
        path.MoveTo(r.Left + radius, r.Top);
        path.LineTo(r.Right - slant, r.Top);
        path.LineTo(r.Right, r.Bottom);
        path.LineTo(r.Left + radius, r.Bottom);
        path.ArcTo(new SKRect(r.Left, r.Bottom - 2 * radius, r.Left + 2 * radius, r.Bottom), 90, 90, false);
        path.LineTo(r.Left, r.Top + radius);
        path.ArcTo(new SKRect(r.Left, r.Top, r.Left + 2 * radius, r.Top + 2 * radius), 180, 90, false);
        path.Close();
        return path;
    }

    private static void Text(SKCanvas c, string text, float x, float cy, SKFont font, SKColor color, float maxWidth = float.MaxValue)
    {
        while (text.Length > 1 && font.MeasureFallback(text) > maxWidth) text = text[..^1];
        using var paint = Fill(color);
        var m = font.Metrics;
        c.DrawTextFallback(text, x, cy - (m.Ascent + m.Descent) / 2, SKTextAlign.Left, font, paint);
    }

    private static void Centered(SKCanvas c, string text, float cx, float cy, SKFont font, SKColor color)
    {
        using var paint = Fill(color);
        var m = font.Metrics;
        c.DrawTextFallback(text, cx, cy - (m.Ascent + m.Descent) / 2, SKTextAlign.Center, font, paint);
    }
}
