using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// The bundled NDS12 pixel face for Skia painters. MAUI font aliases never resolve through
/// SKTypeface.FromFamilyName, and bundled fonts live at assets/NDS12.ttf (no Fonts/ prefix),
/// so this loads the bytes from the app package once and caches the face.
/// </summary>
public static class PixelFont
{
    private static SKTypeface? _face;

    public static SKTypeface Face
    {
        get
        {
            if (_face is not null) return _face;
            try
            {
                using var stream = FileSystem.OpenAppPackageFileAsync("NDS12.ttf").GetAwaiter().GetResult();
                var bytes = new MemoryStream();
                stream.CopyTo(bytes);
                var cache = System.IO.Path.Combine(FileSystem.CacheDirectory, "NDS12.ttf");
                File.WriteAllBytes(cache, bytes.ToArray());
                _face = SKTypeface.FromFile(cache);
            }
            catch
            {
                _face = SKTypeface.Default;
            }
            return _face ?? SKTypeface.Default;
        }
    }

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
