using SkiaSharp;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>PMD's 40×40 portraits, drawn at a whole-number scale so their pixels stay square.</summary>
public static class PokeparkPortrait
{
    /// <returns>False when there is no portrait (yet): the caller draws its fallback.</returns>
    public static bool Draw(SKCanvas canvas, SKImageInfo info, SKBitmap? portrait)
    {
        if (portrait is null) return false;
        var scale = Math.Max(1, Math.Min(info.Width / portrait.Width, info.Height / portrait.Height));
        float w = portrait.Width * scale, h = portrait.Height * scale;
        var target = SKRect.Create(MathF.Round((info.Width - w) / 2), MathF.Round((info.Height - h) / 2), w, h);
        var image = Services.SpriteImages.Of(portrait);
        canvas.DrawImage(image, target, new SKSamplingOptions(SKFilterMode.Nearest));
        return true;
    }

    /// <summary>Portraits download on first use: repaint for a few seconds until it shows up.</summary>
    public static void RepaintWhileLoading(SKCanvasView view, Func<bool> ready)
    {
        if (ready()) return;
        var started = DateTime.UtcNow;
        view.Dispatcher.StartTimer(TimeSpan.FromMilliseconds(200), () =>
        {
            view.InvalidateSurface();
            return !ready() && DateTime.UtcNow - started < TimeSpan.FromSeconds(6);
        });
    }
}
