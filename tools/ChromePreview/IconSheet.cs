using PKForge.Chrome;
using SkiaSharp;

namespace ChromePreview;

/// <summary>Writes the px_* icon PNGs from <see cref="PixelIcons"/> and renders the vocabulary review sheet.</summary>
internal static class IconSheet
{
    public static void Generate(string pksmDir)
    {
        foreach (var (name, map) in PixelIcons.Maps)
        {
            if (map.Length != PixelIcons.Grid || map.Any(row => row.Length != PixelIcons.Grid))
                throw new InvalidOperationException($"pixel icon '{name}' is not {PixelIcons.Grid}x{PixelIcons.Grid}");
            var size = PixelIcons.Grid * PixelIcons.Scale;
            using var bmp = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            bmp.Erase(SKColors.Transparent);
            for (var y = 0; y < size; y++)
                for (var x = 0; x < size; x++)
                    if (map[y / PixelIcons.Scale][x / PixelIcons.Scale] == '#')
                        bmp.SetPixel(x, y, SKColors.White);
            using var img = SKImage.FromBitmap(bmp);
            using var data = img.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(pksmDir, $"px_{name}.png"), data.ToArray());
        }
        Console.WriteLine($"wrote {PixelIcons.Maps.Count} pixel icons to {pksmDir}");
    }

    /// <summary>Every vocabulary entry as a menu row: unselected (white on the black button) and
    /// selected (cyan on the selected button), at the on-device 22dp icon size (x2), plus a 4x zoom.</summary>
    public static void Render(string pksmDir, string fontPath, string output)
    {
        var entries = PksmIconCatalog.Entries;
        const int cols = 4, cellW = 460, cellH = 64;
        var rows = (entries.Count + cols - 1) / cols;
        using var surface = SKSurface.Create(new SKImageInfo(cols * cellW + 24, rows * cellH + 24));
        var c = surface.Canvas;
        c.Clear(Pksm.LogoVoid);
        using var typeface = SKTypeface.FromFile(fontPath);
        using var font = new SKFont(typeface, 20);
        using var small = new SKFont(typeface, 14);
        var missing = entries.Where(e => !File.Exists(Resolve(pksmDir, e.File))).Select(e => e.File).ToList();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var x = 12 + i % cols * cellW;
            var y = 12 + i / cols * cellH;
            var path = Resolve(pksmDir, e.File);
            using var src = File.Exists(path) ? SKBitmap.Decode(path) : null;
            var off = new SKRect(x, y + 4, x + 64, y + cellH - 4);
            var on = new SKRect(x + 70, y + 4, x + 134, y + cellH - 4);
            PksmPaint.BlackButton(c, off, 5);
            PksmPaint.SelectedButton(c, on, 5);
            if (src is not null)
            {
                DrawTinted(c, src, new SKRect(off.MidX - 22, off.MidY - 22, off.MidX + 22, off.MidY + 22), SKColors.White);
                DrawTinted(c, src, new SKRect(on.MidX - 22, on.MidY - 22, on.MidX + 22, on.MidY + 22), Pksm.LogoCyan);
            }
            using var ink = new SKPaint { Color = SKColors.White, IsAntialias = true };
            using var dim = new SKPaint { Color = new SKColor(0xB8, 0xC4, 0xE8), IsAntialias = true };
            c.DrawText(e.Name, x + 144, y + 28, font, ink);
            c.DrawText(e.Meaning, x + 144, y + 50, small, dim);
        }
        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(output, data.ToArray());
        Console.WriteLine($"wrote {output} ({entries.Count} icons{(missing.Count > 0 ? ", MISSING: " + string.Join(", ", missing) : "")})");
    }

    // ribbon_award.png is mapped into ui/pksm/ from PKHeX's ribbon art at build time (see PKForge.App.csproj).
    private static string Resolve(string pksmDir, string file) => file == "ribbon_award.png"
        ? Path.GetFullPath(Path.Combine(pksmDir, "../../../../../external/PKHeX/PKHeX.Drawing.Misc/Resources/img/ribbons/ribbonchampionkalos.png"))
        : Path.Combine(pksmDir, file);

    private static void DrawTinted(SKCanvas c, SKBitmap src, SKRect dst, SKColor color)
    {
        // Same path as the app: SrcIn tint over the alpha mask, nearest-neighbor scaling.
        var fit = Math.Min(dst.Width / src.Width, dst.Height / src.Height);
        var w = src.Width * fit;
        var h = src.Height * fit;
        var r = new SKRect(dst.MidX - w / 2, dst.MidY - h / 2, dst.MidX + w / 2, dst.MidY + h / 2);
        using var filter = SKColorFilter.CreateBlendMode(color, SKBlendMode.SrcIn);
        using var paint = new SKPaint { ColorFilter = filter };
        using var image = SKImage.FromBitmap(src);
        c.DrawImage(image, r, new SKSamplingOptions(SKFilterMode.Nearest), paint);
    }
}
