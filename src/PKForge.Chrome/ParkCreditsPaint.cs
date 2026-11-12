using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The PKForge statue's credits, painted as the games end their stories: the park dimmed behind,
/// little stars twinkling, the PKForge logo, then each section's heading in gold between two
/// diamonds, each name in white with what they did underneath, and the Pokémon living in the park
/// walking beside the names of those who drew them; the roll fades in at the bottom and out at
/// the top.
/// </summary>
public static class ParkCreditsPaint
{
    /// <summary>A name and what they did; a Pokémon's sprite to show beside it, if any.</summary>
    public sealed record Entry(string Name, string? Role = null, (int Species, int Form, bool Shiny)? Sprite = null);
    public sealed record Section(string Heading, IReadOnlyList<Entry> Entries);
    /// <summary>A sprite frame: the sheet and the frame's rectangle in it.</summary>
    public sealed record Frame(SKBitmap Bitmap, SKRect Source);

    private static readonly SKColor Gold = new(0xf4, 0xc8, 0x4a);
    private static readonly SKColor Role = new(0x9c, 0xb4, 0xd8);
    private static readonly SKColor Shade = new(6, 10, 20);

    /// <param name="scroll">How far the roll has come up (screen pixels).</param>
    /// <returns>The roll's full height, to know where it ends.</returns>
    public static float Paint(SKCanvas c, int w, int h, IReadOnlyList<Section> sections, float scroll, long now, SKBitmap? logo,
        SKTypeface face, Func<(int Species, int Form, bool Shiny), long, Frame?> sprite)
    {
        var unit = w / 1920f;
        c.DrawColor(Shade.WithAlpha(185));
        Stars(c, w, h, unit, now);

        using var heading = new SKFont(face, 30 * unit);
        using var name = new SKFont(face, 44 * unit);
        using var role = new SKFont(face, 26 * unit);
        using var small = new SKFont(face, 22 * unit);
        using var big = new SKFont(face, 64 * unit);
        using var paint = new SKPaint { IsAntialias = false };
        var mid = w / 2f;
        // The roll starts below the screen's bottom edge, as in the games.
        var y = h * .62f - scroll;

        if (logo is { } shownLogo)
        {
            var size = 220 * unit;
            using var crisp = new SKPaint { IsAntialias = false };
            c.DrawBitmap(shownLogo, SKRect.Create(mid - size / 2, y, size, size), crisp);
            y += size + 30 * unit;
        }
        paint.Color = SKColors.White;
        c.DrawTextFallback("PKForge", mid, y + 52 * unit, SKTextAlign.Center, big, paint);
        paint.Color = Role;
        c.DrawTextFallback("and its Poképark", mid, y + 100 * unit, SKTextAlign.Center, role, paint);
        y += 220 * unit;

        foreach (var section in sections)
        {
            Heading(c, section.Heading, mid, y, heading, unit);
            y += 90 * unit;
            foreach (var entry in section.Entries)
            {
                var hasSprite = entry.Sprite is not null;
                var textX = mid;
                if (entry.Sprite is { } look)
                {
                    // The Pokémon walks on the spot, facing the reader, beside the names.
                    var frame = sprite(look, now);
                    // Just left of the name and the artists, whichever is wider.
                    var block = Math.Max(heading.MeasureFallback(entry.Name), entry.Role is { } r ? Math.Min(small.MeasureFallback(r), w * .42f) : 0);
                    var spriteX = mid + 60 * unit - block / 2 - 70 * unit;
                    if (frame is not null && y > -200 * unit && y < h + 200 * unit)
                    {
                        var scale = 3 * unit;
                        var dest = SKRect.Create(spriteX - frame.Source.Width * scale / 2, y - frame.Source.Height * scale / 2 - 10 * unit,
                            frame.Source.Width * scale, frame.Source.Height * scale);
                        using var crisp = new SKPaint { IsAntialias = false };
                        c.DrawBitmap(frame.Bitmap, frame.Source, dest, crisp);
                    }
                    textX = mid + 60 * unit;
                }
                paint.Color = SKColors.White;
                c.DrawTextFallback(entry.Name, textX, y, SKTextAlign.Center, hasSprite ? heading : name, paint);
                y += (hasSprite ? 40 : 50) * unit;
                if (entry.Role is { } what)
                {
                    paint.Color = Role;
                    foreach (var line in Wrap(what, hasSprite ? small : role, w * (hasSprite ? .42f : .56f)))
                    {
                        c.DrawTextFallback(line, textX, y, SKTextAlign.Center, hasSprite ? small : role, paint);
                        y += (hasSprite ? 30 : 36) * unit;
                    }
                }
                y += (hasSprite ? 54 : 46) * unit;
            }
            y += 70 * unit;
        }
        paint.Color = Gold;
        c.DrawTextFallback("Thank you for playing!", mid, y + 120 * unit, SKTextAlign.Center, name, paint);
        var contentHeight = y + 120 * unit + scroll - h * .62f;

        // The roll fades in at the bottom and out at the top.
        Fade(c, w, h, unit);
        paint.Color = Role.WithAlpha(200);
        c.DrawTextFallback("Up and Down to scroll  ·  B to close", mid, h - 24 * unit, SKTextAlign.Center, small, paint);
        return contentHeight;
    }

    // A section's heading in gold, between two little diamonds and two thin lines.
    private static void Heading(SKCanvas c, string text, float mid, float y, SKFont font, float unit)
    {
        using var paint = new SKPaint { Color = Gold, IsAntialias = false };
        var half = font.MeasureFallback(text) / 2;
        c.DrawTextFallback(text, mid, y, SKTextAlign.Center, font, paint);
        foreach (var side in new[] { -1, 1 })
        {
            var x = mid + side * (half + 34 * unit);
            var py = y - 10 * unit;
            var d = 6 * unit;
            using var diamond = new SKPath();
            diamond.MoveTo(x, py - d); diamond.LineTo(x + d, py); diamond.LineTo(x, py + d); diamond.LineTo(x - d, py); diamond.Close();
            c.DrawPath(diamond, paint);
            paint.Color = Gold.WithAlpha(110);
            c.DrawRect(side < 0 ? x - 140 * unit : x + 14 * unit, py - unit, 126 * unit, 2 * unit, paint);
            paint.Color = Gold;
        }
    }

    // Small pixel stars twinkling behind the names.
    private static void Stars(SKCanvas c, int w, int h, float unit, long now)
    {
        using var paint = new SKPaint { IsAntialias = false };
        var random = new Random(7);
        for (var i = 0; i < 70; i++)
        {
            float x = random.Next(w), y = random.Next(h);
            var phase = random.NextDouble() * Math.PI * 2;
            var glow = (float)(.5 + .5 * Math.Sin(now / 700.0 + phase));
            var size = MathF.Round((random.Next(3) == 0 ? 4 : 2) * unit);
            paint.Color = new SKColor(0xdc, 0xe8, 0xff, (byte)(40 + 140 * glow));
            c.DrawRect(x, y, size, size, paint);
            if (size > 3 * unit && glow > .8f)
            {
                c.DrawRect(x - size, y, size, size, paint); c.DrawRect(x + size, y, size, size, paint);
                c.DrawRect(x, y - size, size, size, paint); c.DrawRect(x, y + size, size, size, paint);
            }
        }
    }

    private static void Fade(SKCanvas c, int w, int h, float unit)
    {
        var band = 180 * unit;
        using var top = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, band),
            [Shade.WithAlpha(255), Shade.WithAlpha(0)], SKShaderTileMode.Clamp) };
        c.DrawRect(0, 0, w, band, top);
        // The bottom is solid under the hint, so the names never run over it.
        var solid = 70 * unit;
        using var bottom = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, h - band - solid), new SKPoint(0, h - solid),
            [Shade.WithAlpha(0), Shade.WithAlpha(255)], SKShaderTileMode.Clamp) };
        c.DrawRect(0, h - band - solid, w, band, bottom);
        using var floor = new SKPaint { Color = Shade };
        c.DrawRect(0, h - solid, w, solid, floor);
    }

    private static IEnumerable<string> Wrap(string text, SKFont font, float width)
    {
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (font.MeasureFallback(next) <= width || line.Length == 0) { line = next; continue; }
            yield return line;
            line = word;
        }
        if (line.Length > 0) yield return line;
    }

}
