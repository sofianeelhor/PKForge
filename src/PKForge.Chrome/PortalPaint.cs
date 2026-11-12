using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// One Pokémon's steps through the portal, made once from its real box icon (40×30 art
/// pixels): the icon fading through the portal's palette in four flat steps, and its white
/// flash. Built when a transfer lands, never per frame.
/// </summary>
public sealed class PortalMon : IDisposable
{
    public const int Width = 40;
    public const int Height = 30;

    internal readonly SKImage[] Fade = new SKImage[5];
    internal readonly SKImage Flash;

    public PortalMon(SKBitmap icon)
    {
        for (var level = 0; level < Fade.Length; level++)
        {
            var amount = level / 4f;
            Fade[level] = Recolour(icon, c => level == 0 ? c : PksmPaint.Mix(c, PortalPaint.Shade(c), amount));
        }
        // The dark outline stays a pale rim so the flash keeps the Pokémon's shape.
        Flash = Recolour(icon, c => Luma(c) < 0.16f ? PortalPaint.Pale : SKColors.White);
    }

    private static float Luma(SKColor c) => (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue) / 255f;

    private static SKImage Recolour(SKBitmap icon, Func<SKColor, SKColor> map)
    {
        using var copy = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        for (var y = 0; y < Height; y++)
        for (var x = 0; x < Width; x++)
        {
            var c = x < icon.Width && y < icon.Height ? icon.GetPixel(x, y) : SKColors.Transparent;
            // Art pixels are either there or not: half-covered edges of a scaled sprite snap.
            copy.SetPixel(x, y, c.Alpha < 128 ? SKColors.Transparent : map(c.WithAlpha(255)));
        }
        return SKImage.FromBitmap(copy);
    }

    public void Dispose()
    {
        foreach (var image in Fade) image.Dispose();
        Flash.Dispose();
    }
}

/// <summary>A Pokémon taking part: its steps, and its icon's top-left in art pixels.</summary>
public readonly record struct PortalMember(PortalMon Mon, SKPoint From);

/// <summary>
/// The transfer portal, a warp pad on the floor: it opens under the Pokémon, which hops, flashes
/// white and sinks through it in the pad's colours, rows flickering like a scanline, while
/// pixels rise off the rim; the pad shuts into a pixel star. A group hops onto one pad one after
/// another. Drawn in art pixels (the caller scales the canvas by the box icons' own scale, with
/// nearest sampling); the pad and star are hand-made frames from <c>Resources/UI/portal/</c>,
/// stepped at 30 frames a second like a game's effect, never tweened.
/// </summary>
public static class PortalPaint
{
    public const int FramesPerSecond = 30;
    /// <summary>One pad frame; the sheet has the pad on row 0 and its front lip (drawn over the Pokémon) on row 1.</summary>
    public const int PadWidth = 60;
    public const int PadHeight = 24;
    public const int PadFrames = OpenFrames + LoopFrames + CloseFrames;
    public const int StarSize = 15;
    public const int StarFrames = 3;
    /// <summary>The most Pokémon one pad shows; a bigger group still goes, the rest unseen.</summary>
    public const int MaxMembers = 8;
    /// <summary>Where a Pokémon's feet stand in its 40×30 icon: the pad's middle goes there.</summary>
    public const int Feet = 28;

    // The pad's middle in its frame.
    private const int PadMidX = PadWidth / 2;
    private const int PadMidY = 11;
    private const int OpenFrames = 4;
    private const int LoopFrames = 8;
    private const int CloseFrames = 4;
    // A Pokémon's trip: the hop onto the pad, the flash, the sink.
    private const int Hop = 6;
    private const int Flash = 2;
    private static readonly int[] Depth = [2, 4, 7, 10, 14, 18, 23, 28, 33, 38, 44];
    private static readonly int Travel = Hop + Flash + Depth.Length;

    internal static readonly SKColor Pale = new(0xBC, 0xF6, 0xFF);
    private static readonly SKColor Navy = new(0x22, 0x31, 0x7E);
    private static readonly SKColor Blue = new(0x2E, 0x5C, 0xD2);
    private static readonly SKColor Sky = new(0x3E, 0xA2, 0xF2);
    private static readonly SKColor Cyan = new(0x62, 0xE2, 0xF8);

    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>A colour's place in the pad's palette, by brightness.</summary>
    internal static SKColor Shade(SKColor c)
    {
        var lum = (0.299f * c.Red + 0.587f * c.Green + 0.114f * c.Blue) / 255f;
        return lum < 0.16f ? Navy : lum < 0.42f ? Blue : lum < 0.68f ? Sky : lum < 0.86f ? Cyan : Pale;
    }

    private static int Stagger(int count) => count <= 1 ? 0 : Math.Clamp(8 / (count - 1), 1, 2);

    private static int Start(int index, int count) => OpenFrames + index * Stagger(count);

    // A group lands side by side across the pad, three lanes 14 art pixels apart, so no two
    // Pokémon flash on the same spot.
    private static float Lane(int index, int count)
    {
        var lanes = Math.Min(count, 3);
        return (index % lanes - (lanes - 1) / 2f) * 14f;
    }

    private static int CloseAt(int count) => Start(Math.Min(count, MaxMembers) - 1, count) + Travel;

    /// <summary>How many frames the whole effect lasts for <paramref name="count"/> Pokémon (30 for one: 1 s; 37 at most).</summary>
    public static int Length(int count) => CloseAt(Math.Max(1, count)) + CloseFrames + StarFrames;

    /// <summary>
    /// Draws frame <paramref name="tick"/>. <paramref name="centre"/> is the pad's middle, where
    /// feet go in; <paramref name="pad"/> is the pad sheet, <paramref name="star"/> the closing
    /// star's frames left to right.
    /// </summary>
    public static void Draw(SKCanvas c, SKImage pad, SKImage star, ReadOnlySpan<PortalMember> members, SKPoint centre,
        int tick, SKPaint dot)
    {
        var count = Math.Min(members.Length, MaxMembers);
        if (count == 0 || tick < 0) return;
        var closeAt = CloseAt(count);
        var frame = tick < OpenFrames ? tick
            : tick < closeAt ? OpenFrames + (tick - OpenFrames) % LoopFrames
            : tick < closeAt + CloseFrames ? OpenFrames + LoopFrames + (tick - closeAt)
            : -1;
        var cx = MathF.Round(centre.X);
        var cy = MathF.Round(centre.Y);
        var at = SKRect.Create(cx - PadMidX, cy - PadMidY, PadWidth, PadHeight);
        if (frame >= 0) c.DrawImage(pad, SKRect.Create(frame * PadWidth, 0, PadWidth, PadHeight), at, Nearest, null);

        // Those going down sit between the pad and its front lip; the others stand in front.
        for (var i = count - 1; i >= 0; i--)
            if (tick - Start(i, count) >= Hop + Flash) DrawMember(c, members[i], tick - Start(i, count), tick, cx + Lane(i, count), cy);
        if (frame >= 0) c.DrawImage(pad, SKRect.Create(frame * PadWidth, PadHeight, PadWidth, PadHeight), at, Nearest, null);
        for (var i = count - 1; i >= 0; i--)
            if (tick - Start(i, count) < Hop + Flash) DrawMember(c, members[i], tick - Start(i, count), tick, cx + Lane(i, count), cy);

        // Pixels rise off the rim while Pokémon go through.
        for (var i = 0; i < 16; i++)
        {
            var age = tick - (OpenFrames + 4 + i % 9);
            if (age < 0 || age > 9 || tick >= closeAt + 2) continue;
            if (age > 7 && tick % 2 == 0) continue; // the old ones blink out
            var angle = Hash(i * 7 + 1) % 360 * (MathF.PI / 180f);
            dot.Color = age < 3 ? SKColors.White : age < 6 ? Pale : Cyan;
            var size = i % 5 == 0 && age < 4 ? 2 : 1;
            c.DrawRect(MathF.Round(cx + MathF.Cos(angle) * 24f), MathF.Round(cy + MathF.Sin(angle) * 8f - age * (1 + i % 2)), size, size, dot);
        }

        var spark = tick - closeAt - CloseFrames;
        if (spark is >= 0 and < StarFrames)
            c.DrawImage(star, SKRect.Create(spark * StarSize, 0, StarSize, StarSize),
                SKRect.Create(cx - StarSize / 2, cy - 10, StarSize, StarSize), Nearest, null);
    }

    private static void DrawMember(SKCanvas c, PortalMember member, int k, int tick, float cx, float cy)
    {
        var mon = member.Mon;
        var fromX = member.From.X + PortalMon.Width / 2f;
        var fromFeet = member.From.Y + Feet;
        if (k < 0)
        {
            Rows(c, mon.Fade[0], fromX, fromFeet, 1f, 1f, false, tick);
            return;
        }
        if (k < Hop)
        {
            // A hop onto the pad: a little one in place, an arc from another slot. It lands squashed.
            if (k == Hop - 1)
            {
                Rows(c, mon.Fade[1], cx, cy, 1.1f, 0.9f, false, tick);
                return;
            }
            var e = (k + 1) / (float)(Hop - 1);
            var dx = cx - fromX;
            var dy = cy - fromFeet;
            var height = 5f + MathF.Min(10f, MathF.Sqrt(dx * dx + dy * dy) * 0.3f);
            Rows(c, mon.Fade[0], MathF.Round(fromX + dx * e), MathF.Round(fromFeet + dy * e - height * MathF.Sin(e * MathF.PI)),
                1f, 1f, false, tick);
            return;
        }
        if (k < Hop + Flash)
        {
            var first = k == Hop;
            Rows(c, mon.Flash, cx, cy, first ? 0.95f : 0.85f, first ? 1.08f : 1.15f, false, tick);
            return;
        }
        var j = k - Hop - Flash;
        if (j >= Depth.Length) return;
        // Down through the pad, thinner and taller, its rows flickering a pixel either way in pairs.
        Rows(c, mon.Fade[Math.Min(4, 2 + j / 2)], cx, cy + Depth[j], MathF.Max(0.3f, 0.8f - j * 0.05f), 1.15f + j * 0.03f,
            true, tick, clipBelow: cy + 1);
    }

    /// <summary>The icon row by row, its feet row on <paramref name="feet"/>; rows from <paramref name="clipBelow"/> down are inside the pad.</summary>
    private static void Rows(SKCanvas c, SKImage image, float x, float feet, float scaleX, float scaleY, bool scanlines, int tick,
        float clipBelow = float.MaxValue)
    {
        var w = Math.Max(1, (int)MathF.Round(PortalMon.Width * scaleX));
        var h = Math.Max(1, (int)MathF.Round(PortalMon.Height * scaleY));
        var left = MathF.Round(x - w / 2f);
        var top = MathF.Round(feet) - MathF.Round(Feet * scaleY);
        if (!scanlines && top + h <= clipBelow)
        {
            c.DrawImage(image, SKRect.Create(0, 0, PortalMon.Width, PortalMon.Height), SKRect.Create(left, top, w, h), Nearest, null);
            return;
        }
        for (var row = 0; row < h; row++)
        {
            var y = top + row;
            if (y >= clipBelow) break;
            var source = Math.Min(PortalMon.Height - 1, row * PortalMon.Height / h);
            var shift = scanlines ? ((row / 2 + tick) % 2 == 0 ? 1 : -1) : 0;
            c.DrawImage(image, SKRect.Create(0, source, PortalMon.Width, 1), SKRect.Create(left + shift, y, w, 1), Nearest, null);
        }
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
