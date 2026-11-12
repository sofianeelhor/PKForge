using System.Runtime.CompilerServices;
using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The storage box in the designer's art direction: a dark well on the blue lattice, a
/// slanted box banner between two chevrons, Pokémon standing on each row's ground line, and a
/// cursor made of a light pool under the Pokémon and a pixel pointer above it.
/// Sizes are given in design pixels (the 1920×1080 mockup) and scaled by a unit.
/// </summary>
public static class StoragePaint
{
    private static ColorTheme T => ColorTheme.Current;

    public static SKColor Well => T.Well;
    public static SKColor WellEdge => T.WellEdge;
    public static SKColor Frame => T.Frame;
    public static SKColor FrameEdge => T.FrameEdge;
    public static SKColor BannerTop => T.BannerTop;
    public static SKColor BannerBottom => T.BannerBottom;

    /// <summary>A mockup cell: 160×134 design pixels.</summary>
    public const float DesignCellWidth = 160f;
    public const float DesignCellHeight = 134f;

    /// <summary>Showdown icons are drawn 4.5× their 40×30 size at unit 1.</summary>
    public const float IconScale = 4.5f;

    /// <summary>The icon area sits this far inside the well (design pixels, at a 1072×702 well).</summary>
    public const float WellInsetX = 56f;
    public const float WellInsetY = 16f;

    private static SKPaint Fill(SKColor c) => new() { Color = c, IsAntialias = true };
    private static SKPaint Stroke(SKColor c, float w) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w };

    /// <summary>The box well: rounded dark fill with a thin blue edge.</summary>
    public static void WellPanel(SKCanvas c, SKRect r, float unit)
    {
        var radius = 28f * unit;
        using var fill = Fill(Well);
        c.DrawRoundRect(r, radius, radius, fill);
        var edge = 3f * unit;
        using var stroke = Stroke(WellEdge, edge);
        c.DrawRoundRect(SKRect.Inflate(r, -edge / 2, -edge / 2), radius, radius, stroke);
    }

    /// <summary>How a box's own wallpaper shows in the well (a player setting).</summary>
    // Blue is the default style's stored name, kept from before color schemes.
    public enum WallpaperStyle { Blue, Veiled, Duotone, Horizon }

    /// <summary>
    /// Draws a box's game wallpaper inside the well, covering it (aspect kept, overflow cropped)
    /// and toned down so the Pokémon stay the subject. <paramref name="average"/> is the art's
    /// average colour, which the duotone style maps the art onto.
    /// </summary>
    public static void Wallpaper(SKCanvas c, SKRect well, SKImage art, SKColor average, WallpaperStyle style, float unit)
    {
        // The small Gen 3-4 wallpapers carry a 4 px frame of their own: leave it out.
        var frame = art.Width < 200 ? 4 : 0;
        var source = SKRect.Create(frame, frame, art.Width - frame * 2, art.Height - frame * 2);
        var cover = Math.Max(well.Width / source.Width, well.Height / source.Height);
        var w = well.Width / cover;
        var h = well.Height / cover;
        source = SKRect.Create(source.MidX - w / 2, source.MidY - h / 2, w, h);
        // Pixel wallpapers stay crisp; the large painted ones are smoothed.
        var sampling = art.Width < 200
            ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        c.Save();
        var edge = 3f * unit;
        c.ClipRoundRect(new SKRoundRect(SKRect.Inflate(well, -edge, -edge), 25f * unit), antialias: true);
        switch (style)
        {
            case WallpaperStyle.Duotone:
            {
                // The art's light and shade, from the well's navy up to its own colour.
                var light = PksmPaint.Mix(average, Well, 0.35f);
                float Row(byte from, byte to, float weight) => weight * (to - from) / 255f;
                float[] matrix =
                [
                    Row(Well.Red, light.Red, 0.299f), Row(Well.Red, light.Red, 0.587f), Row(Well.Red, light.Red, 0.114f), 0, Well.Red / 255f,
                    Row(Well.Green, light.Green, 0.299f), Row(Well.Green, light.Green, 0.587f), Row(Well.Green, light.Green, 0.114f), 0, Well.Green / 255f,
                    Row(Well.Blue, light.Blue, 0.299f), Row(Well.Blue, light.Blue, 0.587f), Row(Well.Blue, light.Blue, 0.114f), 0, Well.Blue / 255f,
                    0, 0, 0, 1, 0,
                ];
                using var tint = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(matrix) };
                c.DrawImage(art, source, well, sampling, tint);
                break;
            }
            case WallpaperStyle.Horizon:
            {
                // The art rises from the bottom and fades into the navy toward the top.
                c.DrawImage(art, source, well, sampling, null);
                using var fade = new SKPaint
                {
                    Shader = SKShader.CreateLinearGradient(new SKPoint(0, well.Top), new SKPoint(0, well.Bottom),
                        [Well, Well.WithAlpha(235), Well.WithAlpha(120)], [0f, 0.55f, 1f], SKShaderTileMode.Clamp),
                };
                c.DrawRect(well, fade);
                break;
            }
            case WallpaperStyle.Blue:
            {
                // The art's light and shade painted in quiet shades of the color scheme.
                using var map = new SKPaint { ColorFilter = BlueMap(art) };
                c.DrawImage(art, source, well, sampling, map);
                break;
            }
            default:
            {
                // The art as it is, under a navy veil.
                c.DrawImage(art, source, well, sampling, null);
                using var veil = new SKPaint { Color = Well.WithAlpha(165) };
                c.DrawRect(well, veil);
                break;
            }
        }
        c.Restore();
        // The edge goes back on top of the art.
        using var stroke = Stroke(WellEdge, edge);
        c.DrawRoundRect(SKRect.Inflate(well, -edge / 2, -edge / 2), 28f * unit, 28f * unit, stroke);
    }

    private static SKColor[] BlueStops => [Well, ColorTheme.Current.WallpaperMid, ColorTheme.Current.WallpaperLight];

    /// <summary>A wallpaper's grey range (measured once) and its gradient map for one theme.</summary>
    private sealed class BlueMapEntry
    {
        public float Low, High;
        public int Version = -1;
        public SKColorFilter? Filter;
    }

    private static readonly ConditionalWeakTable<SKImage, BlueMapEntry> BlueMaps = new();

    /// <summary>
    /// A gradient map for one wallpaper: its grey levels, stretched over the art's own range
    /// (the wallpapers are pale and low in contrast), mapped onto <see cref="BlueStops"/>.
    /// The range is measured once per image; the map is rebuilt when the theme changes.
    /// </summary>
    private static SKColorFilter BlueMap(SKImage art)
    {
        var entry = BlueMaps.GetValue(art, Measure);
        if (entry.Version == ColorTheme.Version && entry.Filter is { } cached) return cached;

        var stops = BlueStops;
        var alpha = new byte[256];
        var red = new byte[256];
        var green = new byte[256];
        var blue = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var t = Math.Clamp((i - entry.Low) / (entry.High - entry.Low), 0, 1) * (stops.Length - 1);
            var k = Math.Min((int)t, stops.Length - 2);
            var color = PksmPaint.Mix(stops[k], stops[k + 1], t - k);
            alpha[i] = (byte)i;
            red[i] = color.Red;
            green[i] = color.Green;
            blue[i] = color.Blue;
        }
        float[] grey =
        [
            0.299f, 0.587f, 0.114f, 0, 0,
            0.299f, 0.587f, 0.114f, 0, 0,
            0.299f, 0.587f, 0.114f, 0, 0,
            0, 0, 0, 1, 0,
        ];
        entry.Filter?.Dispose();
        entry.Filter = SKColorFilter.CreateCompose(SKColorFilter.CreateTable(alpha, red, green, blue), SKColorFilter.CreateColorMatrix(grey));
        entry.Version = ColorTheme.Version;
        return entry.Filter;

        static BlueMapEntry Measure(SKImage image)
        {
            using var bitmap = SKBitmap.FromImage(image);
            var levels = new List<int>();
            for (var y = 0; y < bitmap.Height; y += 2)
                for (var x = 0; x < bitmap.Width; x += 2)
                {
                    var p = bitmap.GetPixel(x, y);
                    levels.Add((p.Red * 299 + p.Green * 587 + p.Blue * 114) / 1000);
                }
            levels.Sort();
            // The 5th and 95th percentiles, so a few stray pixels do not flatten the stretch.
            var low = levels.Count == 0 ? 0f : levels[levels.Count / 20];
            var high = levels.Count == 0 ? 255f : Math.Max(low + 1, levels[levels.Count * 19 / 20]);
            return new BlueMapEntry { Low = low, High = high };
        }
    }

    /// <summary>The average colour of an image, sampled on a coarse grid.</summary>
    public static SKColor AverageColor(SKBitmap bitmap)
    {
        long red = 0, green = 0, blue = 0, count = 0;
        var step = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 48);
        for (var y = 0; y < bitmap.Height; y += step)
            for (var x = 0; x < bitmap.Width; x += step)
            {
                var p = bitmap.GetPixel(x, y);
                red += p.Red; green += p.Green; blue += p.Blue; count++;
            }
        return count == 0 ? Well : new SKColor((byte)(red / count), (byte)(green / count), (byte)(blue / count));
    }

    /// <summary>
    /// The box header: a rounded frame holding the slanted banner with the box name, and a
    /// double chevron at each end, dimmed when there is no box that way.
    /// </summary>
    public static void BoxHeader(SKCanvas c, SKRect r, string label, SKFont font, bool canPrev, bool canNext, float unit, bool chevrons = true)
    {
        var edge = 3f * unit;
        var radius = 18f * unit;
        using (var frame = Fill(Frame)) c.DrawRoundRect(r, radius, radius, frame);
        using (var stroke = Stroke(FrameEdge, edge)) c.DrawRoundRect(SKRect.Inflate(r, -edge / 2, -edge / 2), radius, radius, stroke);

        var banner = BannerRect(r, unit);
        var slant = 26f * unit;
        using var path = new SKPath();
        path.MoveTo(banner.Left, banner.Top);
        path.LineTo(banner.Right - slant, banner.Top);
        path.LineTo(banner.Right, banner.Bottom);
        path.LineTo(banner.Left + slant, banner.Bottom);
        path.Close();
        using (var gradient = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(0, banner.Top), new SKPoint(0, banner.Bottom),
                [BannerTop, BannerBottom], SKShaderTileMode.Clamp),
        })
            c.DrawPath(path, gradient);
        using (var outline = Stroke(Pksm.Ink, edge)) c.DrawPath(path, outline);

        PksmPaint.CenterText(c, label, banner.MidX, banner.MidY, font, Pksm.Ink, Frame, SKTextAlign.Center);

        if (!chevrons) return;
        Chevron(c, r.Left + 68f * unit, r.MidY, -1, unit, canPrev);
        Chevron(c, r.Right - 68f * unit, r.MidY, 1, unit, canNext);
    }

    /// <summary>Where the banner sits in a header: 142 / 140 design pixels in from each end.</summary>
    public static SKRect BannerRect(SKRect header, float unit) =>
        new(header.Left + 142f * unit, header.Top, header.Right - 140f * unit, header.Bottom);

    /// <summary>A double chevron pointing left (-1) or right (1).</summary>
    public static void Chevron(SKCanvas c, float cx, float cy, int direction, float unit, bool enabled = true)
    {
        var thickness = 11f * unit;
        var arm = 18f * unit;
        using var path = new SKPath();
        foreach (var offset in new[] { -16f, 14f })
        {
            var ox = cx + offset * unit;
            var back = ox - direction * arm / 2;
            var tip = ox + direction * arm / 2;
            path.MoveTo(back, cy - arm);
            path.LineTo(back + direction * thickness, cy - arm);
            path.LineTo(tip + direction * thickness, cy);
            path.LineTo(back + direction * thickness, cy + arm);
            path.LineTo(back, cy + arm);
            path.LineTo(tip, cy);
            path.Close();
        }
        using var ink = Fill(enabled ? Pksm.Ink : Pksm.Ink.WithAlpha(0x40));
        c.DrawPath(path, ink);
    }

    // ---------- Multi-select: the sweep and the carried group ----------

    /// <summary>
    /// The area being swept with A held: one frame around all of it, its dashes walking
    /// (<paramref name="timeMs"/>), green to mark and red to unmark. Each cell used to get its
    /// own flat wash, which read as a stain rather than as a selection being drawn.
    /// </summary>
    public static void SweepFrame(SKCanvas c, SKRect area, float unit, bool mark, long timeMs)
    {
        var color = mark ? Pksm.CursorGreen : Pksm.CursorRed;
        var frame = SKRect.Inflate(area, -3f * unit, -3f * unit);
        var radius = 10f * unit;
        using var fill = Fill(color.WithAlpha(44));
        c.DrawRoundRect(frame, radius, radius, fill);
        using var edge = Stroke(color, 4f * unit);
        var dash = 16f * unit;
        var gap = 10f * unit;
        edge.StrokeCap = SKStrokeCap.Round;
        edge.PathEffect = SKPathEffect.CreateDash([dash, gap], -(timeMs % 900) / 900f * (dash + gap));
        c.DrawRoundRect(frame, radius, radius, edge);
    }

    /// <summary>How many Pokémon the hand carries, on a pill beside the pointer.</summary>
    public static void CarryCount(SKCanvas c, SKRect cell, float unit, int count, SKTypeface face)
    {
        using var font = new SKFont(face, 30f * unit);
        var text = $"x{count}";
        var width = Math.Max(64f * unit, font.MeasureFallback(text) + 30f * unit);
        var pill = SKRect.Create(cell.MidX + 30f * unit, cell.Top - 16f * unit, width, 44f * unit);
        using var shadow = Fill(SKColors.Black.WithAlpha(90));
        c.DrawRoundRect(SKRect.Create(pill.Left + 4f * unit, pill.Top + 6f * unit, pill.Width, pill.Height), 22f * unit, 22f * unit, shadow);
        using var fill = Fill(Pksm.CursorGreen);
        c.DrawRoundRect(pill, 22f * unit, 22f * unit, fill);
        using var rim = Stroke(Pksm.LogoVoid, 3f * unit);
        c.DrawRoundRect(pill, 22f * unit, 22f * unit, rim);
        PksmPaint.CenterText(c, text, pill.MidX, pill.MidY, font, Pksm.LogoVoid, SKColors.Transparent, SKTextAlign.Center);
    }

    /// <summary>How far the selected Pokémon is lifted off the ground.</summary>
    public const float CursorLift = 10f;

    // The pointer above the selected Pokémon, 11×8 art pixels:
    // 1 dark rim, 2 white, 3 accent, 4 pale accent.
    private static readonly string[] PointerArt =
    [
        "11111111111",
        "12222222221",
        "12444444421",
        "01344444310",
        "00134443100",
        "00013431000",
        "00001310000",
        "00000100000",
    ];

    private static SKColor PointerRim => T.PointerRim;
    private static SKColor PointerPale => T.PointerPale;
    private static readonly SKColor PointerPaleGreen = new(0xCC, 0xF5, 0xDC);

    /// <summary>
    /// The pixel pointer, centered over a cell and hanging just above it. Cyan for the move
    /// hand; green while marking many (the games' two hands).
    /// </summary>
    public static void Pointer(SKCanvas c, SKRect cell, float unit, bool marking = false)
    {
        var px = 5f * unit;
        var left = cell.MidX - 27f * unit;
        var top = cell.Top - 10f * unit;
        var accent = marking ? Pksm.CursorGreen : Pksm.LogoCyan;
        var pale = marking ? PointerPaleGreen : PointerPale;
        using var shadow = new SKPaint { Color = SKColors.Black.WithAlpha(90) };
        using var paint = new SKPaint();
        for (var pass = 0; pass < 2; pass++)
        {
            var dx = pass == 0 ? 4f * unit : 0f;
            var dy = pass == 0 ? 6f * unit : 0f;
            for (var y = 0; y < PointerArt.Length; y++)
                for (var x = 0; x < PointerArt[y].Length; x++)
                {
                    var code = PointerArt[y][x];
                    if (code == '0') continue;
                    paint.Color = code switch { '1' => PointerRim, '2' => Pksm.Ink, '3' => accent, _ => pale };
                    var cellRect = new SKRect(left + dx + x * px, top + dy + y * px, left + dx + (x + 1) * px, top + dy + (y + 1) * px);
                    c.DrawRect(cellRect, pass == 0 ? shadow : paint);
                }
        }
    }

    /// <summary>The shiny star in a cell's top-right corner.</summary>
    public static void ShinyStar(SKCanvas c, SKRect cell, SKFont font, float unit)
    {
        using var paint = Fill(Pksm.ShinyGold);
        var x = cell.Right - 22f * unit;
        var y = cell.Top + 18f * unit;
        var metrics = font.Metrics;
        c.DrawTextFallback("★", x, y - (metrics.Ascent + metrics.Descent) / 2, SKTextAlign.Center, font, paint);
    }

    /// <summary>
    /// A Showdown box icon standing on the cell's ground line, 4.5× at unit 1, lifted when
    /// selected. The icon's own padding keeps its feet on the line.
    /// </summary>
    public static SKRect IconRect(SKRect cell, float unit, float lift)
    {
        var w = 40f * IconScale * unit;
        var h = 30f * IconScale * unit;
        var bottom = cell.Bottom + 3f * unit - lift;
        return new SKRect(cell.MidX - w / 2, bottom - h, cell.MidX + w / 2, bottom);
    }

    /// <summary>
    /// Where a sprite that has no Showdown icon (a shiny) stands: scaled to fit the footprint
    /// of the Pokémon's normal icon, so shinies are the same size as their normal form.
    /// </summary>
    public static SKRect FootprintRect(SKRect cell, SKSizeI spriteSize, SKSize footprint, float lift, float unit)
    {
        var scale = Math.Min(footprint.Width / spriteSize.Width, footprint.Height / spriteSize.Height);
        var w = spriteSize.Width * scale;
        var h = spriteSize.Height * scale;
        var bottom = cell.Bottom - 14f * unit - lift;
        return new SKRect(cell.MidX - w / 2, bottom - h, cell.MidX + w / 2, bottom);
    }

    /// <summary>The bounds of the pixels above an alpha threshold within an area; empty when none.</summary>
    public static SKRectI OpaqueBounds(SKBitmap bitmap, SKRectI area, byte threshold = 0)
    {
        int left = area.Right, top = area.Bottom, right = area.Left - 1, bottom = area.Top - 1;
        for (var y = area.Top; y < area.Bottom; y++)
            for (var x = area.Left; x < area.Right; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha <= threshold) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        return right < left ? SKRectI.Empty : new SKRectI(left, top, right + 1, bottom + 1);
    }
}
