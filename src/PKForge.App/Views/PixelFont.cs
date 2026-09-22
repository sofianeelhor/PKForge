using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// The bundled NDS12 pixel face for Skia painters. MAUI font aliases never resolve through
/// SKTypeface.FromFamilyName, and bundled fonts live at assets/NDS12.ttf (no Fonts/ prefix),
/// so this loads the bytes from the app package once and caches the face.
/// </summary>
public static class PixelFont
{
    private static readonly object Gate = new();
    private static SKTypeface? _face;
    private static Task? _loadTask;

    public static SKTypeface Face
    {
        get
        {
            var face = Volatile.Read(ref _face);
            if (face is not null) return face;
            _ = WarmAsync();
            // Font loading must never block a Skia paint callback. The next timer
            // repaint automatically picks up the bundled face after it is ready.
            return SKTypeface.Default;
        }
    }

    public static Task WarmAsync()
    {
        lock (Gate)
            return _loadTask ??= Task.Run(Load);
    }

    private static void Load()
    {
        SKTypeface face;
        try
        {
            using var stream = FileSystem.OpenAppPackageFileAsync("NDS12.ttf").GetAwaiter().GetResult();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            var cache = System.IO.Path.Combine(FileSystem.CacheDirectory, "NDS12.ttf");
            File.WriteAllBytes(cache, bytes.ToArray());
            face = SKTypeface.FromFile(cache) ?? SKTypeface.Default;
        }
        catch
        {
            face = SKTypeface.Default;
        }
        Volatile.Write(ref _face, face);
    }

    /// <summary>The bundled M PLUS Rounded face: full Latin + Japanese coverage, the
    /// UI's own rounded style. The fallback for text the pixel face cannot draw.</summary>
    public static SKTypeface FallbackFace
    {
        get
        {
            var face = Volatile.Read(ref _fallback);
            if (face is not null) return face;
            _ = WarmFallbackAsync();
            return SKTypeface.Default;
        }
    }

    private static SKTypeface? _fallback;

    public static Task WarmFallbackAsync()
    {
        lock (Gate)
            return _fallbackTask ??= Task.Run(() =>
            {
                SKTypeface face;
                try
                {
                    // MauiFont resources land at the assets root, not in Fonts/.
                    using var stream = FileSystem.OpenAppPackageFileAsync("MPLUSRounded1c-Regular.ttf").GetAwaiter().GetResult();
                    using var bytes = new MemoryStream();
                    stream.CopyTo(bytes);
                    var cache = System.IO.Path.Combine(FileSystem.CacheDirectory, "MPLUSRounded1c-Regular.ttf");
                    File.WriteAllBytes(cache, bytes.ToArray());
                    face = SKTypeface.FromFile(cache) ?? SKTypeface.Default;
                }
                catch
                {
                    face = SKTypeface.Default;
                }
                Volatile.Write(ref _fallback, face);
            });
    }

    private static Task? _fallbackTask;

    /// <summary>The pixel face at a size, with antialiasing off-ish (it is a pixel font).</summary>
    public static SKFont At(float size) => new(Face, size) { Edging = SKFontEdging.Antialias, Embolden = true };

    /// <summary>The pixel face when it can draw every glyph of the text, else the system default (CJK nicknames).</summary>
    public static SKFont For(string text, float size)
    {
        var face = Face;
        var covered = text.All(c => face.GetGlyph(c) != 0);
        return new SKFont(covered ? face : SKTypeface.Default, size) { Edging = SKFontEdging.Antialias, Embolden = true };
    }
}
