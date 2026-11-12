using PKForge.Domain;
using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>What a Pokémon holds or plays with, drawn in art pixels beside its feet.</summary>
public static class ParkProps
{
    private static readonly SKColor Outline = new(0x2b, 0x2d, 0x42);

    /// <summary>The play ball, <paramref name="height"/> art pixels above its shadow on the ground.</summary>
    /// <summary>Bidoof's dam: a mound of sticks at the lake's edge, one more for every trip
    /// (up to twelve), bottom row first.</summary>
    public static void DrawDam(SKCanvas canvas, SKPaint paint, float x, float y, int sticks)
    {
        var bark = new SKColor(0x8a, 0x5a, 0x2e);
        var shade = new SKColor(0x4a, 0x30, 0x18);
        var light = new SKColor(0xb0, 0x7c, 0x48);
        // Rows of five, four and three sticks, each a little shorter and higher, staggered.
        var placed = 0;
        foreach (var (count, row) in new[] { (5, 0), (4, 1), (3, 2) })
            for (var i = 0; i < count && placed < sticks; i++, placed++)
            {
                float sx = MathF.Round(x - count * 3 + i * 6 + (row % 2) * 2), sy = MathF.Round(y - row * 3);
                paint.Color = shade; canvas.DrawRect(sx, sy + 1, 6, 1, paint);
                paint.Color = bark; canvas.DrawRect(sx, sy, 6, 1, paint);
                paint.Color = light; canvas.DrawRect(sx + 1, sy, 2, 1, paint);
            }
        paint.Color = SKColors.White;
    }

    public static void DrawBall(SKCanvas canvas, SKPaint paint, float x, float ground, float height)
    {
        x = MathF.Round(x); ground = MathF.Round(ground);
        paint.Color = new SKColor(0, 0, 0, (byte)Math.Max(20, 60 - height * 2));
        canvas.DrawRect(x - 2, ground, 5, 1, paint);
        var top = ground - 6 - MathF.Round(height);
        paint.Color = Outline;
        canvas.DrawRect(x - 2, top, 5, 6, paint);
        canvas.DrawRect(x - 3, top + 1, 7, 4, paint);
        paint.Color = new SKColor(0xe8, 0x5a, 0x4f);
        canvas.DrawRect(x - 2, top + 1, 5, 2, paint);
        paint.Color = new SKColor(0xfa, 0xf6, 0xea);
        canvas.DrawRect(x - 2, top + 3, 5, 2, paint);
        paint.Color = Outline;
        canvas.DrawRect(x, top + 2, 1, 2, paint);
    }

    /// <param name="facing">PMD order (0 south … 6 west): the prop goes in front, or beside when facing away.</param>
    /// <param name="age">ms since the prop appeared.</param>
    /// <summary>Berry colors by flavor (Cheri, Aspear, Pecha, Chesto, Rawst), then the Oran Berry.</summary>
    public static readonly SKColor[] BerryColors =
        [new(0xe8, 0x4a, 0x4a), new(0xf0, 0xd0, 0x40), new(0xf4, 0x9a, 0xb8), new(0x8a, 0x5c, 0xc8), new(0x4c, 0xb8, 0x8c), new(0x3f, 0x7c, 0xd8)];

    /// <param name="flavor">The berry's flavor (0–4), or −1 for an Oran Berry.</param>
    public static void Draw(SKCanvas canvas, SKPaint paint, ParkProp prop, float x, float y, int facing, long age, int flavor = -1)
    {
        // Held in a hand, to the side it faces; a boulder goes straight up over the head.
        var side = prop == ParkProp.Boulder ? 0 : facing is 1 or 2 or 3 ? 1 : facing is 5 or 6 or 7 ? -1 : (facing == 4 ? 1 : 0);
        var front = facing is 0 or 1 or 7 ? 2 : 0;
        x = MathF.Round(x + side * 8); y = MathF.Round(y + front);
        switch (prop)
        {
            case ParkProp.Berry:
            {
                // A round berry with a leaf; a bite goes missing as the meal goes on.
                float left = x - 2, top = y - 5;
                paint.Color = Outline;
                canvas.DrawRect(left, top - 1, 4, 6, paint);
                canvas.DrawRect(left - 1, top, 6, 4, paint);
                var color = BerryColors[flavor is >= 0 and <= 4 ? flavor : 5];
                paint.Color = color;
                canvas.DrawRect(left, top, 4, 4, paint);
                paint.Color = PksmPaint.Mix(color, SKColors.White, .55f);
                canvas.DrawRect(left, top, 1, 1, paint);
                if (age > 3000)
                {
                    paint.Color = new SKColor(0xf4, 0xe9, 0xc8);
                    canvas.DrawRect(left + 3, top, 1, 2, paint);
                }
                paint.Color = new SKColor(0x4e, 0x9a, 0x3c);
                canvas.DrawRect(left + 2, top - 2, 2, 1, paint);
                return;
            }
            case ParkProp.Ball:
                DrawBall(canvas, paint, x, y, MathF.Abs(MathF.Sin(age / 260f)) * 9);
                return;
            case ParkProp.Boulder:
            {
                // A round grey boulder held up over the head: outlined, lit from the top left.
                string[] rock =
                [
                    "...kkkk...",
                    ".kkhhllkk.",
                    "khhlllmmmk",
                    "khllmmmmdk",
                    "klmmmmmmdk",
                    "kmmmmmmddk",
                    ".kmmmdddk.",
                    "..kkkkkk..",
                ];
                var top = y - 30;
                foreach (var (row, j) in rock.Select((r, j) => (r, j)))
                    for (var i = 0; i < row.Length; i++)
                    {
                        if (row[i] == '.') continue;
                        paint.Color = row[i] switch
                        {
                            'k' => new SKColor(0x3a, 0x36, 0x40), 'h' => new SKColor(0xd4, 0xd0, 0xd8), 'l' => new SKColor(0xa8, 0xa4, 0xae),
                            'm' => new SKColor(0x86, 0x82, 0x8c), _ => new SKColor(0x62, 0x5e, 0x68)
                        };
                        canvas.DrawRect(x - 5 + i, top + j, 1, 1, paint);
                    }
                return;
            }
            case ParkProp.Egg:
            {
                // The artist's egg, one art pixel each, rocking now and then.
                var sway = MathF.Sin(age / 300f);
                var frame = sway > .85f ? "wobble-right" : sway < -.85f ? "wobble-left" : EggSprite.Still;
                paint.Color = SKColors.White; // the scene's light stays in the paint's filter
                EggSprite.Draw(canvas, SKRect.Create(x - EggSprite.Width / 2, y - 3 - EggSprite.Height, EggSprite.Width, EggSprite.Height),
                    frame, maxScale: 1, paint);
                return;
            }
            case ParkProp.Notes:
            {
                // Notes drift up from the Pokémon, one after another.
                for (var i = 0; i < 2; i++)
                {
                    var t = (age / 1800f + i * .5f) % 1;
                    var nx = x - side * 8 + (i == 0 ? -6 : 6) + MathF.Round(MathF.Sin(t * 6) * 1.5f);
                    var ny = y - 26 - MathF.Round(t * 12);
                    paint.Color = new SKColor(0x3d, 0x6f, 0xc9, (byte)(255 * (1 - t)));
                    canvas.DrawRect(nx, ny, 1, 4, paint);
                    canvas.DrawRect(nx + 1, ny, 2, 1, paint);
                    canvas.DrawRect(nx - 2, ny + 3, 2, 2, paint);
                }
                return;
            }
        }
    }
}
