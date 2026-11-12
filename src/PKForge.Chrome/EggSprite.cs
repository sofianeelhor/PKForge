using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The artist's egg (12×14 pixel art, <c>Resources/UI/egg/egg-*.png</c>), the one every surface
/// that shows an egg draws: the summary, the Day Care, the Poképark. Scaled by whole steps with
/// no filtering. The app says once how to read a frame (<see cref="Load"/>); each frame is read
/// on first use and kept.
/// </summary>
public static class EggSprite
{
    public const int Width = 12;
    public const int Height = 14;

    /// <summary>The egg standing still.</summary>
    public const string Still = "wobble-mid";

    /// <summary>Reads one frame by name ("wobble-mid", "crack-left"…); null when it cannot.</summary>
    public static Func<string, SKImage?>? Load { get; set; }

    private static readonly Dictionary<string, SKImage?> Frames = [];
    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    /// <summary>One frame, or null when the app has not said how to read it or it cannot be read.</summary>
    public static SKImage? Get(string frame = Still)
    {
        lock (Frames)
        {
            if (Frames.TryGetValue(frame, out var cached)) return cached;
            if (Load is not { } load) return null;
            SKImage? image;
            try { image = load(frame); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException) { image = null; }
            Frames[frame] = image;
            return image;
        }
    }

    /// <summary>
    /// Draws <paramref name="frame"/> as large as whole steps allow inside <paramref name="box"/>
    /// (no more than <paramref name="maxScale"/>), centred, standing on the box's bottom edge.
    /// Returns where it went, or null when the frame is not available.
    /// </summary>
    public static SKRect? Draw(SKCanvas canvas, SKRect box, string frame = Still, float maxScale = float.MaxValue, SKPaint? paint = null)
    {
        if (Get(frame) is not { } egg) return null;
        var dest = PaintKit.Fit(egg, box, maxScale);
        dest.Offset(0, box.Bottom - dest.Bottom);
        // On whole canvas units (art pixels where the canvas is scaled to them), so it never straddles.
        dest = new SKRect(MathF.Round(dest.Left), MathF.Round(dest.Top), MathF.Round(dest.Right), MathF.Round(dest.Bottom));
        canvas.DrawImage(egg, dest, Nearest, paint);
        return dest;
    }
}
