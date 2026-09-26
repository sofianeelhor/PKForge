using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// Paints a Bank box's wallpaper: the palette flat of its position, a chosen flat, or an
/// in-game wallpaper picture. Pictures load once in the background; the flat stands in
/// until they arrive.
/// </summary>
public static class BankBoxArt
{
    private static readonly Dictionary<string, SKBitmap?> Pictures = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SKColor> Averages = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Loading = new(StringComparer.Ordinal);
    private static readonly Lock Gate = new();

    // Wallpapers are pixel art: keep them crisp when scaled up.
    private static readonly SKSamplingOptions PixelSampling = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>The wallpaper a box shows: its chosen one, or the palette flat of its position.</summary>
    public static BankWallpaper Of(int box) =>
        BankBoxDecor.Wallpapers.Get(box) ?? BankWallpaper.Color(PaletteIndex(box));

    /// <summary>The flat color standing for a wallpaper (a picture's average once loaded).</summary>
    public static SKColor Tone(BankWallpaper wallpaper)
    {
        if (wallpaper.ColorIndex is { } color) return Pksm.BoxWallpapers[color % Pksm.BoxWallpapers.Length];
        lock (Gate) return Averages.TryGetValue(wallpaper.ArtAsset!, out var average) ? average : Pksm.BoxWallpapers[0];
    }

    public static SKColor Tone(int box) => Tone(Of(box));

    /// <summary>True once the box's wallpaper can be drawn as it will finally look.</summary>
    public static bool IsReady(int box)
    {
        if (Of(box).ArtAsset is not { } asset) return true;
        lock (Gate) return Pictures.ContainsKey(asset);
    }

    public static void Paint(SKCanvas canvas, SKRect rect, int box, Action invalidate) => Paint(canvas, rect, Of(box), invalidate);

    /// <summary>Fills <paramref name="rect"/> with the wallpaper, a picture cropped to fill.</summary>
    public static void Paint(SKCanvas canvas, SKRect rect, BankWallpaper wallpaper, Action invalidate)
    {
        if (wallpaper.ArtAsset is { } asset && Picture(asset, invalidate) is { } picture)
        {
            var scale = Math.Max(rect.Width / picture.Width, rect.Height / picture.Height);
            var w = picture.Width * scale;
            var h = picture.Height * scale;
            var dest = new SKRect(rect.MidX - w / 2, rect.MidY - h / 2, rect.MidX + w / 2, rect.MidY + h / 2);
            canvas.Save();
            canvas.ClipRect(rect);
            using var image = SKImage.FromBitmap(picture);
            canvas.DrawImage(image, dest, PixelSampling);
            canvas.Restore();
            return;
        }
        PksmPaint.Wallpaper(canvas, rect, Tone(wallpaper));
    }

    private static int PaletteIndex(int box) =>
        ((box % Pksm.BoxWallpapers.Length) + Pksm.BoxWallpapers.Length) % Pksm.BoxWallpapers.Length;

    private static SKBitmap? Picture(string asset, Action invalidate)
    {
        lock (Gate)
        {
            if (Pictures.TryGetValue(asset, out var cached)) return cached;
            if (!Loading.Add(asset)) return null;
        }
        _ = Task.Run(async () =>
        {
            SKBitmap? bitmap = null;
            try
            {
                await using var stream = await FileSystem.OpenAppPackageFileAsync($"wallpapers/{asset}.png").ConfigureAwait(false);
                bitmap = SKBitmap.Decode(stream);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Missing asset: the flat keeps standing in.
            }
            lock (Gate)
            {
                Loading.Remove(asset);
                Pictures[asset] = bitmap;
                if (bitmap is not null) Averages[asset] = Average(bitmap);
            }
            MainThread.BeginInvokeOnMainThread(invalidate);
        });
        return null;
    }

    private static SKColor Average(SKBitmap bitmap)
    {
        using var tiny = bitmap.Resize(new SKImageInfo(1, 1), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        var color = tiny?.GetPixel(0, 0) ?? Pksm.BoxWallpapers[0];
        return color.WithAlpha(0xFF);
    }
}
