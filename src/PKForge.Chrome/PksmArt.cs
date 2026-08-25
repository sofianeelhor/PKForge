using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// Loads and caches the bundled PKSM pixel chrome. Bitmaps render nearest-neighbor at
/// integer-friendly scales so the 3DS-era look stays crisp on high-density screens.
/// </summary>
public sealed class PksmArt
{
    private readonly Dictionary<string, SKBitmap> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SKBitmap> _miss = new(StringComparer.Ordinal);

    

    private static readonly SKPaint NearestPaint = new();

    /// <summary>Registers raw PNG bytes under a name (idempotent; first bytes win).</summary>
    public void Supply(string name, byte[] png)
    {
        if (_cache.ContainsKey(name) || _miss.ContainsKey(name)) return;
        var bmp = SKBitmap.Decode(png);
        if (bmp is null) { _miss[name] = new SKBitmap(1, 1); return; }
        _cache[name] = bmp;
    }

    public SKBitmap? Get(string name) => _cache.TryGetValue(name, out var b) ? b : null;

    public int Width(string name) => Get(name)?.Width ?? 0;
    public int Height(string name) => Get(name)?.Height ?? 0;

    /// <summary>Draws a bitmap into the destination rect, nearest-neighbor.</summary>
    public void Draw(SKCanvas c, string name, SKRect dst)
    {
        var b = Get(name);
        if (b is null) return;
        c.DrawBitmap(b, dst, new SKPaint());
    }

    /// <summary>Draws at integer scale (2x, 3x...) anchored top-left.</summary>
    public void DrawScaled(SKCanvas c, string name, float x, float y, int scale)
    {
        var b = Get(name);
        if (b is null) return;
        c.DrawBitmap(b, new SKRect(x, y, x + b.Width * scale, y + b.Height * scale),
            new SKPaint());
    }

    /// <summary>
    /// Nine-slice stretch: corner cuts in source pixels, everything else stretches.
    /// The classic way PKSM chrome (bars, stripes, windows) scales to any width.
    /// </summary>
    public void Draw9(SKCanvas c, string name, SKRect dst, int cut)
    {
        var b = Get(name);
        if (b is null) return;
        Draw9(c, name, dst, cut, cut, cut, cut);
    }

    public void Draw9(SKCanvas c, string name, SKRect dst, int l, int t, int r, int bCut)
    {
        var bmp = Get(name);
        if (bmp is null) return;
        var w = bmp.Width;
        var h = bmp.Height;
        var p = new SKPaint();

        void Piece(int sx, int sy, int sw, int sh, float dx, float dy, float dw, float dh)
        {
            if (sw <= 0 || sh <= 0 || dw <= 0.01f || dh <= 0.01f) return;
            c.DrawBitmap(bmp, new SKRectI(sx, sy, sx + sw, sy + sh), new SKRect(dx, dy, dx + dw, dy + dh), p);
        }

        // corners
        Piece(0, 0, l, t, dst.Left, dst.Top, l, t);
        Piece(w - r, 0, r, t, dst.Right - r, dst.Top, r, t);
        Piece(0, h - bCut, l, bCut, dst.Left, dst.Bottom - bCut, l, bCut);
        Piece(w - r, h - bCut, r, bCut, dst.Right - r, dst.Bottom - bCut, r, bCut);
        // edges
        Piece(l, 0, w - l - r, t, dst.Left + l, dst.Top, dst.Width - l - r, t);
        Piece(l, h - bCut, w - l - r, bCut, dst.Left + l, dst.Bottom - bCut, dst.Width - l - r, bCut);
        Piece(0, t, l, h - t - bCut, dst.Left, dst.Top + t, l, dst.Height - t - bCut);
        Piece(w - r, t, r, h - t - bCut, dst.Right - r, dst.Top + t, r, dst.Height - t - bCut);
        // center
        Piece(l, t, w - l - r, h - t - bCut, dst.Left + l, dst.Top + t, dst.Width - l - r, dst.Height - t - bCut);
    }

    /// <summary>Tints a copy of a bitmap (used to recolor the flat icon set per context).</summary>
    public SKBitmap Tinted(string name, SKColor color)
    {
        var key = $"{name}#{color}";
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var src = Get(name);
        if (src is null) return new SKBitmap(1, 1);
        var copy = new SKBitmap(src.Width, src.Height);
        using var canvas = new SKCanvas(copy);
        canvas.DrawBitmap(src, 0, 0);
        canvas.DrawRect(0, 0, src.Width, src.Height, new SKPaint { Color = color, BlendMode = SKBlendMode.SrcIn });
        _cache[key] = copy;
        return copy;
    }
}
