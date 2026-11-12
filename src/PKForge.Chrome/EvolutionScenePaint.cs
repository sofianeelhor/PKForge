using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// One Pokémon's looks for the evolution scene, made once from its real front sprite: trimmed
/// to its pixels with hard edges, then in colour, fading through the stage's palette in four flat
/// steps, as a dithered silhouette, and as a white flash. Built when an evolution lands, never per frame.
/// </summary>
public sealed class EvolutionMon : IDisposable
{
    public int Width { get; }
    public int Height { get; }

    internal readonly SKImage Colour;
    internal readonly SKImage Silhouette;
    internal readonly SKImage Flash;
    internal readonly SKImage[] Fade = new SKImage[5];

    private readonly SKColor[] _pixels;

    public EvolutionMon(SKBitmap sprite)
    {
        int minX = sprite.Width, minY = sprite.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < sprite.Height; y++)
        for (var x = 0; x < sprite.Width; x++)
        {
            if (sprite.GetPixel(x, y).Alpha < 128) continue;
            minX = Math.Min(minX, x);
            minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x);
            maxY = Math.Max(maxY, y);
        }
        if (maxX < 0) minX = minY = maxX = maxY = 0;
        Width = maxX - minX + 1;
        Height = maxY - minY + 1;
        _pixels = new SKColor[Width * Height];
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            // Art pixels are either there or not: half-covered edges of a scaled sprite snap.
            var c = sprite.GetPixel(minX + x, minY + y);
            _pixels[y * Width + x] = c.Alpha < 128 ? SKColors.Transparent : c.WithAlpha(255);
        }
        var stage = EvolutionScenePaint.Palette.Current();
        Colour = Recolour((_, _, c) => c);
        for (var level = 0; level < Fade.Length; level++)
        {
            var amount = level / 4f;
            Fade[level] = Recolour((_, _, c) => level == 0 ? c : PksmPaint.Mix(c, stage.Shade(c), amount));
        }
        // Light where the sprite is light, cyan where it is dark, checkered between, a blue rim:
        // the shape and its volume read, the colours are gone.
        Silhouette = Recolour((x, y, c) =>
        {
            if (Edge(x, y)) return stage.Blue;
            var lum = Luma(c);
            return lum > 0.55f ? stage.Pale : lum > 0.3f ? ((x + y) & 1) == 0 ? stage.Pale : stage.Cyan : stage.Cyan;
        });
        Flash = Recolour((x, y, _) => Edge(x, y) ? stage.Pale : SKColors.White);
    }

    internal static float Luma(SKColor c) => (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue) / 255f;

    private bool Opaque(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && _pixels[y * Width + x].Alpha != 0;

    private bool Edge(int x, int y) => !Opaque(x - 1, y) || !Opaque(x + 1, y) || !Opaque(x, y - 1) || !Opaque(x, y + 1);

    private SKImage Recolour(Func<int, int, SKColor, SKColor> map)
    {
        using var copy = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var c = _pixels[y * Width + x];
            copy.SetPixel(x, y, c.Alpha == 0 ? SKColors.Transparent : map(x, y, c));
        }
        return SKImage.FromBitmap(copy);
    }

    public void Dispose()
    {
        Colour.Dispose();
        Silhouette.Dispose();
        Flash.Dispose();
        foreach (var image in Fade) image.Dispose();
    }
}

/// <summary>Where the scene's line goes (art pixels) and how many of its letters show; drawn by <see cref="EvolutionScenePaint.Caption"/>.</summary>
public readonly record struct EvolutionCaption(SKRectI Plate, int Shown);

/// <summary>
/// The evolution scene, played once an evolution has been written: a stage band opens over the
/// dimmed screen, the Pokémon stands on its base, its colours drain in flat steps while pixels are
/// drawn in, then it flickers between its own silhouette and the new one, faster and faster, as
/// light rises off the base; the stage flashes through its palette in steps while the new shape
/// blinks white, then the new Pokémon stands in colour, hops, sparkles, and the line says what happened.
/// Drawn in art pixels (the caller scales the canvas by a whole step with nearest sampling),
/// stepped at 30 frames a second like a game's effect, never tweened. The colours are the theme's.
/// </summary>
public static class EvolutionScenePaint
{
    public const int FramesPerSecond = 30;
    /// <summary>The whole scene: 3 s, the last second on the new Pokémon and its line.</summary>
    public const int Length = 90;
    /// <summary>The art canvas's height on a landscape screen (×5 on a 1080 p screen).</summary>
    public const int ArtHeight = 216;

    private const int BandHeight = 136;
    private const int ChargeAt = 4;
    private const int FlickerAt = 16;
    private const int FlashAt = 52;
    private const int RevealAt = 58;
    private const int BaseRadius = 46;
    private const int LettersPerFrame = 3;
    // How long each look holds while the two silhouettes trade places: slow, then fast.
    private static readonly int[] Flicker = [5, 4, 4, 4, 3, 3, 3, 2, 2, 2, 1, 1, 1, 1];
    // The band's palette steps during the flash (indexes into the ramp).
    private static readonly int[] FlashSteps = [2, 3, 4, 4, 3, 1];

    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>The stage's colours, read from the theme when a scene starts.</summary>
    internal sealed record Palette(SKColor Void, SKColor Deep, SKColor Deck, SKColor Grid, SKColor Blue, SKColor Cyan, SKColor Pale)
    {
        public static Palette Current()
        {
            var t = ColorTheme.Current;
            return new Palette(t.Void, t.Deep, t.Deck, t.Grid, t.Blue, t.Cyan, PksmPaint.Mix(t.Cyan, SKColors.White, 0.6f));
        }

        /// <summary>A colour's place in the stage's palette, by brightness.</summary>
        public SKColor Shade(SKColor c)
        {
            var lum = EvolutionMon.Luma(c);
            return lum < 0.16f ? Deep : lum < 0.42f ? Blue : lum < 0.68f ? Cyan : lum < 0.86f ? Pale : SKColors.White;
        }

        public SKColor Ramp(int step) => step switch { 0 => Deep, 1 => Grid, 2 => Blue, 3 => Cyan, _ => Pale };
    }

    /// <summary>
    /// The whole step art pixels are drawn at on a canvas this size: about 216 art pixels high,
    /// and never fewer than 240 across (a phone held upright).
    /// </summary>
    public static int ScaleFor(int pixelWidth, int pixelHeight) =>
        Math.Max(1, Math.Min((int)MathF.Round(pixelHeight / (float)ArtHeight), pixelWidth / 240));

    /// <summary>
    /// Draws frame <paramref name="tick"/> on an art canvas <paramref name="width"/> × <paramref name="height"/>
    /// art pixels. <paramref name="letters"/> is the line's length. Returns where the line goes once
    /// it shows; the caller writes it with <see cref="Caption"/> at screen resolution.
    /// </summary>
    public static EvolutionCaption? Draw(SKCanvas c, int width, int height, EvolutionMon from, EvolutionMon into, int letters,
        int tick, SKPaint dot)
    {
        if (tick < 0) return null;
        var p = Palette.Current();
        var top = Math.Max(2, (height - (BandHeight + 6 + 22)) / 2 + 4);
        var band = new SKRectI(0, top, width, top + BandHeight);
        var plateWidth = Math.Min(width - 16, 280);
        var plate = new SKRectI((width - plateWidth) / 2, band.Bottom + 6, (width + plateWidth) / 2, band.Bottom + 28);
        var fx = width / 2;
        var feet = top + 114;

        var flash = tick - FlashAt;
        SKColor? fill = flash >= 0 && flash < FlashSteps.Length ? p.Ramp(FlashSteps[flash]) : null;
        Band(c, width, height, band, p, tick, fill, dot);
        if (tick < 3) return null;
        // At the height of the flash the base lights up with the band.
        Base(c, fx, feet, BaseRadius, 9, p, dot, flash is >= 1 and <= 3 ? p.Pale : null);
        var foot = feet + 2;

        if (tick < FlickerAt)
        {
            // The colours drain in flat steps while pixels are drawn in toward it.
            var step = Math.Clamp((tick - ChargeAt) / 3, 0, 4);
            Mon(c, tick < ChargeAt ? from.Colour : from.Fade[step], fx, foot, from);
            for (var i = 0; i < 14; i++)
            {
                var age = tick - (ChargeAt + i % 6);
                if (age < 0 || age > 9) continue;
                var angle = Hash(i * 11 + 3) % 360 * (MathF.PI / 180f);
                var distance = 70f - age * 7f;
                dot.Color = age > 6 ? SKColors.White : age > 3 ? p.Pale : p.Cyan;
                var size = age < 5 ? 2 : 1;
                c.DrawRect(MathF.Round(fx + MathF.Cos(angle) * distance), MathF.Round(foot - from.Height / 2f + MathF.Sin(angle) * distance * 0.55f),
                    size, size, dot);
            }
            return null;
        }

        if (tick < RevealAt)
        {
            if (tick < FlashAt)
            {
                var at = FlickerAt;
                var turn = 0;
                while (turn < Flicker.Length && at + Flicker[turn] <= tick) at += Flicker[turn++];
                var mon = turn % 2 == 0 ? from : into;
                Mon(c, mon.Silhouette, fx, foot, mon);
            }
            else Mon(c, flash % 2 == 0 ? into.Flash : into.Silhouette, fx, foot, into);
            // Light rising off the base.
            for (var i = 0; i < 18; i++)
            {
                var age = (tick - FlickerAt + i * 3) % 12;
                var x = fx - 40 + (int)(Hash(i * 5 + 1) % 80);
                var y = foot - age * 2 - (int)(Hash(i + 9) % 6);
                if (Math.Abs(x - fx) < 10 && age < 4) continue;
                dot.Color = age < 4 ? SKColors.White : age < 8 ? p.Pale : p.Cyan;
                var size = age < 6 ? 2 : 1;
                c.DrawRect(x, y, size, size, dot);
            }
            return null;
        }

        // The new Pokémon: a hop, a squash on landing, shards out, a few sparkles, the line.
        var r = tick - RevealAt;
        var hop = r switch { 1 => 2, 2 => 4, 3 => 4, 4 => 2, _ => 0 };
        var squash = r == 5;
        Mon(c, into.Colour, fx, foot - hop, into, squash ? 1.06f : 1f, squash ? 0.94f : 1f);
        for (var i = 0; i < 10 && r <= 10; i++)
        {
            var angle = (i * 36 + 18) * (MathF.PI / 180f);
            var distance = 20f + r * 6f;
            dot.Color = r < 4 ? SKColors.White : r < 7 ? p.Pale : p.Cyan;
            var size = r < 5 ? 2 : 1;
            c.DrawRect(MathF.Round(fx + MathF.Cos(angle) * distance), MathF.Round(foot - into.Height / 2f + MathF.Sin(angle) * distance * 0.7f),
                size, size, dot);
        }
        for (var i = 0; i < 4; i++)
        {
            var x = fx - into.Width / 2 + (int)(Hash(i * 3 + 7) % (uint)Math.Max(1, into.Width));
            var y = foot - into.Height + (int)(Hash(i * 13 + 2) % (uint)Math.Max(1, into.Height));
            Sparkle(c, x, y, r - 4 - i * 3, p, dot);
        }
        Plate(c, plate, p, dot);
        return new EvolutionCaption(plate, Math.Clamp((r - 2) * LettersPerFrame, 0, letters));
    }

    /// <summary>
    /// Writes the line's first <see cref="EvolutionCaption.Shown"/> letters on its plate, on the
    /// screen canvas (device pixels) where art pixel (0, 0) is at <paramref name="origin"/>. The
    /// pixel font is drawn at a whole multiple of its own size, so it stays crisp.
    /// </summary>
    public static void Caption(SKCanvas c, EvolutionCaption caption, SKPoint origin, int scale, string line, SKTypeface face)
    {
        if (caption.Shown <= 0) return;
        var room = (caption.Plate.Width - 8) * scale;
        // A step under the art's own scale (×4 letters on ×5 art): the line reads as a caption, not a headline.
        var step = scale >= 4 ? scale - 1 : scale;
        using var font = new SKFont(face, 12 * step) { Edging = SKFontEdging.Alias };
        while (step > 1 && font.MeasureFallback(line) > room) font.Size = 12 * --step;
        using var ink = new SKPaint { Color = SKColors.White, IsAntialias = false };
        font.GetFontMetrics(out var m);
        var full = MathF.Min(room, font.MeasureFallback(line));
        var x = MathF.Round(origin.X + caption.Plate.MidX * scale - full / 2);
        var y = MathF.Round(origin.Y + caption.Plate.MidY * scale - (m.Ascent + m.Descent) / 2);
        c.DrawTextFallback(line[..Math.Min(line.Length, caption.Shown)], x, y, SKTextAlign.Left, font, ink);
    }

    private static void Band(SKCanvas c, int width, int height, SKRectI band, Palette p, int tick, SKColor? fill, SKPaint dot)
    {
        // The screen dims, and the band opens from its middle in four steps.
        dot.Color = p.Void.WithAlpha(200);
        c.DrawRect(0, 0, width, height, dot);
        var half = (int)MathF.Round(band.Height * (Math.Min(4, tick + 1) / 4f) / 2f);
        var r = new SKRectI(0, band.MidY - half, width, band.MidY + half);
        dot.Color = fill ?? p.Deep;
        c.DrawRect(r, dot);
        if (fill is null)
        {
            dot.Color = p.Deck;
            for (var y = r.Top + 2; y < r.Bottom - 2; y += 4) c.DrawRect(0, y, width, 2, dot);
        }
        dot.Color = p.Cyan;
        c.DrawRect(0, r.Top, width, 1, dot);
        c.DrawRect(0, r.Bottom - 1, width, 1, dot);
        dot.Color = p.Void;
        c.DrawRect(0, r.Top - 1, width, 1, dot);
        c.DrawRect(0, r.Bottom, width, 1, dot);
    }

    /// <summary>The base the Pokémon stands on: a flat oval in two tones with a light top edge, row by row.</summary>
    private static void Base(SKCanvas c, int cx, int cy, int rx, int ry, Palette p, SKPaint dot, SKColor? lit = null)
    {
        for (var y = -ry; y <= ry; y++)
        {
            var half = (int)MathF.Round(rx * MathF.Sqrt(MathF.Max(0f, 1f - y * y / (float)(ry * ry))));
            dot.Color = p.Void;
            c.DrawRect(cx - half, cy + y, half * 2, 1, dot);
            var inner = Math.Max(0, half - 2);
            if (Math.Abs(y) >= ry || inner == 0) continue;
            dot.Color = lit ?? (y < 0 ? p.Grid : p.Deck);
            c.DrawRect(cx - inner, cy + y, inner * 2, 1, dot);
        }
        dot.Color = p.Blue;
        c.DrawRect(cx - rx + 4, cy - ry + 1, rx * 2 - 8, 1, dot);
    }

    private static void Plate(SKCanvas c, SKRectI b, Palette p, SKPaint dot)
    {
        dot.Color = p.Void;
        c.DrawRect(b.Left, b.Top, b.Width, b.Height, dot);
        dot.Color = p.Cyan;
        c.DrawRect(b.Left + 1, b.Top + 1, b.Width - 2, b.Height - 2, dot);
        dot.Color = p.Deep;
        c.DrawRect(b.Left + 2, b.Top + 2, b.Width - 4, b.Height - 4, dot);
    }

    private static void Mon(SKCanvas c, SKImage image, int x, int foot, EvolutionMon mon, float scaleX = 1f, float scaleY = 1f)
    {
        var w = Math.Max(1, (int)MathF.Round(mon.Width * scaleX));
        var h = Math.Max(1, (int)MathF.Round(mon.Height * scaleY));
        c.DrawImage(image, SKRect.Create(0, 0, mon.Width, mon.Height), SKRect.Create(x - w / 2, foot - h, w, h), Nearest, null);
    }

    /// <summary>A plus-shaped twinkle that shrinks to a dot.</summary>
    private static void Sparkle(SKCanvas c, int x, int y, int age, Palette p, SKPaint dot)
    {
        if (age is < 0 or > 8) return;
        dot.Color = age < 3 ? SKColors.White : age < 6 ? p.Pale : p.Cyan;
        var arm = age < 3 ? 3 : age < 6 ? 2 : 0;
        c.DrawRect(x, y, 1, 1, dot);
        if (arm == 0) return;
        c.DrawRect(x - arm, y, arm * 2 + 1, 1, dot);
        c.DrawRect(x, y - arm, 1, arm * 2 + 1, dot);
    }

    private static uint Hash(int x)
    {
        var h = (uint)x * 2654435761u;
        h ^= h >> 15;
        h *= 2246822519u;
        h ^= h >> 13;
        return h;
    }
}
