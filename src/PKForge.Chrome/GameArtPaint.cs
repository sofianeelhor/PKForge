using PKForge.Domain;
using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The player's own game art: turning a picked file into the PNG PKForge keeps, and the
/// preview screen that shows it in place (a cartridge label, a title logo, a wide banner)
/// before it is used. Theme roles only, flat panels, no glow.
/// </summary>
public static class GameArtPaint
{
    /// <summary>Picked files larger than this are refused before decoding.</summary>
    public const int MaxInputBytes = 24 * 1024 * 1024;
    private const int MaxSourcePixels = 8192 * 8192;

    /// <summary>The longest side kept per kind, and the size small pixel art is grown toward.</summary>
    public static SKSizeI Bounds(GameArtKind kind) => kind switch
    {
        GameArtKind.Logo => new SKSizeI(1024, 512),
        GameArtKind.Hero => new SKSizeI(1600, 900),
        _ => new SKSizeI(512, 512),
    };

    /// <summary>The frame the preview (and the shelf) shows the art in, as width / height.</summary>
    public static float FrameAspect(GameArtKind kind) => kind switch
    {
        GameArtKind.Logo => 2f,
        GameArtKind.Hero => 16f / 9f,
        _ => 62f / 66f,
    };

    /// <summary>
    /// Decodes any image Skia reads (PNG, JPEG, WebP, GIF, BMP...) and returns the PNG to keep:
    /// aspect always kept; a large image scaled down smoothly to <see cref="Bounds"/>; a small one
    /// (pixel art) grown by a whole factor with nearest sampling, so its pixels stay square and
    /// sharp wherever it is drawn. Null for anything that is not a readable image.
    /// </summary>
    public static byte[]? Normalize(byte[] data, GameArtKind kind)
    {
        if (data is null || data.Length == 0 || data.Length > MaxInputBytes) return null;
        try
        {
            using var stream = new SKMemoryStream(data);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return null;
            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxSourcePixels) return null;
            using var source = SKBitmap.Decode(codec, new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (source is null) return null;

            var bounds = Bounds(kind);
            var fit = Math.Min(bounds.Width / (float)source.Width, bounds.Height / (float)source.Height);
            int width, height;
            SKSamplingOptions sampling;
            if (fit < 1)
            {
                width = Math.Max(1, (int)MathF.Round(source.Width * fit));
                height = Math.Max(1, (int)MathF.Round(source.Height * fit));
                sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
            }
            else
            {
                var step = Math.Max(1, (int)MathF.Floor(fit));
                width = source.Width * step;
                height = source.Height * step;
                sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
            }

            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return null;
            surface.Canvas.Clear(SKColors.Transparent);
            using (var image = SKImage.FromBitmap(source))
                surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height), sampling);
            using var snapshot = surface.Snapshot();
            using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            return png?.ToArray();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null; // a damaged or hostile file is simply not an image
        }
    }

    /// <summary>Draws <paramref name="image"/> fitted (aspect kept) and centred in <paramref name="r"/>;
    /// whole-step nearest when it is enlarged, smooth when reduced.</summary>
    public static void DrawFitted(SKCanvas c, SKImage image, SKRect r)
    {
        var fit = Math.Min(r.Width / image.Width, r.Height / image.Height);
        var nearest = fit >= 1;
        if (nearest) fit = MathF.Floor(fit);
        var w = image.Width * fit;
        var h = image.Height * fit;
        var target = SKRect.Create(MathF.Round(r.MidX - w / 2), MathF.Round(r.MidY - h / 2), w, h);
        var sampling = nearest
            ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        c.DrawImage(image, target, sampling);
    }

    /// <summary>
    /// The preview screen: a panel with the kind as its header, the art in the frame it will be
    /// shown in (a cartridge in the save's plastic, a logo on the grid, a banner), the game's name
    /// and a one-line caption. <paramref name="image"/> null draws the empty frame.
    /// </summary>
    public static void Preview(SKCanvas c, SKRect r, SKImage? image, GameArtKind kind, string gameName, string caption,
        SKColor plastic, SKTypeface face, float density)
    {
        var d = density;
        PksmPaint.Panel(c, r, Pksm.Housing, 10 * d);
        using var title = new SKFont(face, 20 * d);
        using var small = new SKFont(face, 14 * d);
        var header = new SKRect(r.Left + 14 * d, r.Top + 12 * d, r.Right - 14 * d, r.Top + 42 * d);
        PksmPaint.HeaderStrip(c, header, KindTitle(kind), title);

        var stage = new SKRect(r.Left + 18 * d, header.Bottom + 12 * d, r.Right - 18 * d, r.Bottom - 54 * d);
        using (var well = new SKPaint { Color = Pksm.PaperShade, IsAntialias = true })
            c.DrawRoundRect(stage, 6 * d, 6 * d, well);
        PksmPaint.LogoGrid(c, stage, 24 * d, 1.5f * d);
        using (var edge = new SKPaint { Color = Pksm.PaperEdge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * d })
            c.DrawRoundRect(stage, 6 * d, 6 * d, edge);

        var frame = FrameIn(SKRect.Inflate(stage, -16 * d, -14 * d), kind);
        switch (kind)
        {
            case GameArtKind.Cartridge:
                Cartridge(c, frame, image, plastic, d);
                break;
            default:
                using (var back = new SKPaint { Color = Pksm.LogoVoid.WithAlpha(kind == GameArtKind.Hero ? (byte)0xFF : (byte)0x00), IsAntialias = true })
                    c.DrawRect(frame, back);
                if (image is not null)
                {
                    c.Save();
                    c.ClipRect(frame);
                    DrawFitted(c, image, frame);
                    c.Restore();
                }
                else Empty(c, frame, small, d);
                using (var rim = new SKPaint { Color = Pksm.PaperEdgeDeep, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * d })
                    c.DrawRect(frame, rim);
                break;
        }

        PksmPaint.CenterText(c, gameName, r.MidX, r.Bottom - 38 * d, small, ColorTheme.Current.Bright, Pksm.LogoVoid, SKTextAlign.Center);
        PksmPaint.CenterText(c, caption, r.MidX, r.Bottom - 18 * d, small, Pksm.InkSoft, Pksm.LogoVoid, SKTextAlign.Center);
    }

    public static string KindTitle(GameArtKind kind) => kind switch
    {
        GameArtKind.Logo => "Title logo",
        GameArtKind.Hero => "Hero banner",
        _ => "Cartridge art",
    };

    private static SKRect FrameIn(SKRect area, GameArtKind kind)
    {
        var aspect = FrameAspect(kind);
        var w = Math.Min(area.Width, area.Height * aspect);
        var h = w / aspect;
        return SKRect.Create(MathF.Round(area.MidX - w / 2), MathF.Round(area.MidY - h / 2), MathF.Round(w), MathF.Round(h));
    }

    /// <summary>A cartridge as the shelf draws it: plastic body, contact strip, the label sticker.</summary>
    private static void Cartridge(SKCanvas c, SKRect frame, SKImage? image, SKColor plastic, float d)
    {
        var body = SKRect.Create(frame.MidX - frame.Height * 0.45f, frame.Top, frame.Height * 0.9f, frame.Height);
        var radius = body.Width * 0.09f;
        using (var p = new SKPaint { Color = plastic, IsAntialias = true }) c.DrawRoundRect(body, radius, radius, p);
        using (var p = new SKPaint { Color = PksmPaint.Darker(plastic, 0.35f), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * d })
            c.DrawRoundRect(body, radius, radius, p);
        var strip = new SKRect(body.Left + 2 * d, body.Top + 2 * d, body.Right - 2 * d, body.Top + body.Height * 0.1f);
        using (var p = new SKPaint { Color = PksmPaint.Darker(plastic, 0.45f) }) c.DrawRect(strip, p);
        var sticker = new SKRect(body.Left + body.Width * 0.09f, strip.Bottom + body.Height * 0.05f, body.Right - body.Width * 0.09f, body.Bottom - body.Height * 0.08f);
        using (var p = new SKPaint { Color = Pksm.LogoDeck, IsAntialias = true }) c.DrawRoundRect(sticker, 4 * d, 4 * d, p);
        using (var p = new SKPaint { Color = Pksm.LogoGrid, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * d })
            c.DrawRoundRect(sticker, 4 * d, 4 * d, p);
        var inside = SKRect.Inflate(sticker, -3 * d, -3 * d);
        if (image is null)
        {
            using var small = new SKFont { Size = 12 * d };
            Empty(c, inside, small, d);
            return;
        }
        c.Save();
        c.ClipRect(inside);
        DrawFitted(c, image, inside);
        c.Restore();
    }

    private static void Empty(SKCanvas c, SKRect r, SKFont font, float d)
    {
        using var dash = new SKPaint
        {
            Color = Pksm.InkSoft.WithAlpha(0x90), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f * d, IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash([6 * d, 5 * d], 0),
        };
        c.DrawRect(SKRect.Inflate(r, -4 * d, -4 * d), dash);
        PksmPaint.CenterText(c, "Default", r.MidX, r.MidY, font, Pksm.InkSoft, Pksm.LogoVoid, SKTextAlign.Center);
    }
}
