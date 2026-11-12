using PKForge.App.Services;
using PKForge.Chrome;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// The box well and its wallpaper, drawn once into an image and then copied under the
/// Pokémon on every frame. Scaling and tinting the wallpaper is most of a box frame's cost,
/// and the cursor's hop alone repaints the box about ten times per step. The image is the
/// exact pixels the well would draw, so the look does not change. A few boxes are kept, so
/// flipping back and forth with L and R does not redraw them.
/// </summary>
public sealed class WellLayer
{
    private const int Kept = 3;

    private readonly record struct Made(SKImageInfo Info, float Unit, object Key, int Theme, StoragePaint.WallpaperStyle Style);

    private readonly List<(Made Made, SKImage Image)> _images = [];

    /// <summary>
    /// Draws the well and <paramref name="backdrop"/> filling <paramref name="info"/>. Everything
    /// the backdrop draws must follow from <paramref name="key"/> (with the colour scheme and the
    /// box background style); a null key draws straight to the canvas.
    /// </summary>
    public void Draw(SKCanvas canvas, SKImageInfo info, float unit, object? key, Action<SKCanvas, SKRect, float>? backdrop)
    {
        var well = new SKRect(0, 0, info.Width, info.Height);
        if (key is null || !canvas.TotalMatrix.IsIdentity)
        {
            Paint(canvas, well, unit, backdrop);
            return;
        }
        var made = new Made(info, unit, key, ColorTheme.Version, BoxBackground.Style);
        var index = _images.FindIndex(entry => entry.Made.Equals(made));
        SKImage image;
        if (index >= 0)
        {
            image = _images[index].Image;
            if (index > 0)
            {
                _images.RemoveAt(index);
                _images.Insert(0, (made, image));
            }
        }
        else
        {
            using var surface = SKSurface.Create(info);
            if (surface is null)
            {
                Paint(canvas, well, unit, backdrop);
                return;
            }
            surface.Canvas.Clear(SKColors.Transparent);
            Paint(surface.Canvas, well, unit, backdrop);
            image = surface.Snapshot();
            _images.Insert(0, (made, image));
            while (_images.Count > Kept)
            {
                _images[^1].Image.Dispose();
                _images.RemoveAt(_images.Count - 1);
            }
        }
        canvas.DrawImage(image, 0, 0);
    }

    private static void Paint(SKCanvas canvas, SKRect well, float unit, Action<SKCanvas, SKRect, float>? backdrop)
    {
        StoragePaint.WellPanel(canvas, well, unit);
        backdrop?.Invoke(canvas, well, unit);
    }
}
