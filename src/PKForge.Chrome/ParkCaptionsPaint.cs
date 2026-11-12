using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// Captions over the park: what a Pokémon has just done, in a few words over its head, white on a
/// see-through dark pill like the park's other labels. In screen pixels, the same size at any zoom;
/// nudged up so two never cover each other, kept on screen, faded in and out. A group doing the
/// same thing (a scene) gets the words once.
/// </summary>
public static class ParkCaptionsPaint
{
    /// <summary>One caption: its text, the point over the head (screen pixels), how visible (0 to 1).</summary>
    public readonly record struct Caption(string Text, float X, float Y, float Alpha);

    public static void Draw(SKCanvas c, IEnumerable<Caption> captions, int screenWidth, Func<string, float, SKFont> fontFor)
    {
        var size = MathF.Max(13, screenWidth / 92f);
        var placed = new List<SKRect>();
        using var paint = new SKPaint { IsAntialias = true };
        var said = new HashSet<string>();
        foreach (var caption in captions)
        {
            if (!said.Add(caption.Text)) continue;
            var text = caption.Text;
            using var font = fontFor(text, size);
            var most = screenWidth * .4f;
            if (font.MeasureFallback(text, paint) > most)
            {
                while (text.Length > 1 && font.MeasureFallback(text + "…", paint) > most) text = text[..^1];
                text += "…";
            }
            var width = font.MeasureFallback(text, paint) + size;
            var height = size * 1.35f;
            var rect = SKRect.Create(Math.Clamp(caption.X - width / 2, 4, screenWidth - width - 4), caption.Y - height, width, height);
            while (placed.Any(r => r.IntersectsWith(rect))) rect.Offset(0, -(height + 4));
            placed.Add(rect);
            paint.Color = new SKColor(16, 22, 30, (byte)(170 * caption.Alpha));
            c.DrawRoundRect(rect, height / 2, height / 2, paint);
            paint.Color = SKColors.White.WithAlpha((byte)(255 * caption.Alpha));
            c.DrawTextFallback(text, rect.MidX, rect.Top + height * .76f, SKTextAlign.Center, font, paint);
        }
    }

    /// <summary>How visible a caption is at an age: in over 0.2 s, out over the last 0.4 s.</summary>
    public static float Alpha(long age, long lasts) => Math.Clamp(age / 200f, 0, 1) * Math.Clamp((lasts - age) / 400f, 0, 1);
}
