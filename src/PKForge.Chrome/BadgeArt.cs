using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The gym badges, hand-drawn as 16x16 pixel art and drawn at whole-step scale: in colour once
/// earned, as a flat silhouette in the theme's colour while still to win, like the games' cases.
/// </summary>
internal static partial class BadgeArt
{
    private static readonly Dictionary<string, SKImage> Cache = [];

    /// <summary>"kanto/boulder" from the region and badge name the engine reports.</summary>
    public static string KeyOf(string region, string name) => $"{region.ToLowerInvariant()}/{name.ToLowerInvariant()}";

    public static bool Has(string key) => Grids.ContainsKey(key);

    private static SKImage? Image(string key)
    {
        if (Cache.TryGetValue(key, out var image)) return image;
        if (!Grids.TryGetValue(key, out var art)) return null;
        using var bitmap = new SKBitmap(new SKImageInfo(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul));
        var ink = new SKColor(0x20, 0x20, 0x28);
        for (var i = 0; i < 256; i++)
        {
            var ch = art.Grid[i];
            if (ch == '.') continue;
            var color = ch == 'k' ? ink : new SKColor(art.Palette.First(p => p.Key == ch).Argb);
            bitmap.SetPixel(i % 16, i / 16, color);
        }
        image = SKImage.FromBitmap(bitmap);
        Cache[key] = image;
        return image;
    }

    /// <summary>Draws the badge centred in <paramref name="room"/>, as large as whole steps allow.</summary>
    public static void Draw(SKCanvas c, string key, SKRect room, bool earned, SKColor silhouette)
    {
        // Whole steps of device pixels, so every art pixel is the same size on screen.
        var scale = MathF.Max(0.01f, c.TotalMatrix.ScaleX);
        var step = MathF.Max(1f, MathF.Floor(MathF.Min(room.Width, room.Height) * scale / 16f)) / scale;
        var left = MathF.Round((room.MidX - 8f * step) * scale) / scale;
        var top = MathF.Round((room.MidY - 8f * step) * scale) / scale;
        var dest = SKRect.Create(left, top, 16f * step, 16f * step);
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        if (Image(key) is not { } image)
        {
            using var plain = new SKPaint { Color = earned ? Pksm.RibbonGold : silhouette };
            c.DrawRect(SKRect.Inflate(dest, -2f * step, -2f * step), plain);
            return;
        }
        if (earned) { c.DrawImage(image, dest, sampling); return; }
        using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(silhouette, SKBlendMode.SrcIn) };
        c.DrawImage(image, dest, sampling, paint);
    }
}
