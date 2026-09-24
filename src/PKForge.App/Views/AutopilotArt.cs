using PKForge.App.Services;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace PKForge.App.Views;

/// <summary>
/// Canvas-side game art for the Living Dex Autopilot: the bundled cartridge icon of a game
/// (the same <see cref="GameArt.GetIconAsync"/> lookup, with its pair fallback, the Home shelf uses),
/// decoded once as a bitmap, and the PKSM pixel icons the plan rows speak with. When a game has no
/// bundled art, <see cref="DrawGame"/> draws the generation-colored cartridge mark instead.
/// </summary>
public static class AutopilotArt
{
    private static readonly Dictionary<string, SKBitmap?> Games = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SKBitmap?> Icons = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Loading = new(StringComparer.Ordinal);
    private static readonly List<Action> Waiters = [];
    private static readonly object Gate = new();

    /// <summary>The decoded icon for a game's art label; null while loading (then <paramref name="ready"/> runs) or when none is bundled.</summary>
    public static SKBitmap? Game(string? artLabel, Action? ready)
    {
        if (string.IsNullOrWhiteSpace(artLabel)) return null;
        lock (Gate)
        {
            if (Games.TryGetValue(artLabel, out var bitmap)) return bitmap;
            if (ready is not null) Waiters.Add(ready);
            if (!Loading.Add(artLabel)) return null;
        }
        _ = Task.Run(async () =>
        {
            SKBitmap? decoded = null;
            try
            {
                if (await GameArt.GetIconAsync(artLabel).ConfigureAwait(false) is { } path) decoded = SKBitmap.Decode(path);
            }
            catch
            {
                // No art: the cartridge mark stands in.
            }
            List<Action> waiters;
            lock (Gate)
            {
                Games[artLabel] = decoded;
                Loading.Remove(artLabel);
                waiters = [.. Waiters];
                Waiters.Clear();
            }
            MainThread.BeginInvokeOnMainThread(() => { foreach (var w in waiters) w(); });
        });
        return null;
    }

    /// <summary>A PKSM pixel icon, tinted, as a bitmap (decoded once).</summary>
    public static SKBitmap? Icon(string name, string tint = PksmIcons.White)
    {
        var key = $"{name}|{tint}";
        lock (Gate)
        {
            if (Icons.TryGetValue(key, out var cached)) return cached;
        }
        SKBitmap? bitmap;
        try
        {
            if (name.StartsWith("ui:", StringComparison.Ordinal))
            {
                // A raw PKSM asset with its authored colors (checkboxes).
                using var stream = FileSystem.OpenAppPackageFileAsync($"ui/pksm/{name[3..]}").GetAwaiter().GetResult();
                bitmap = SKBitmap.Decode(stream);
            }
            else bitmap = SKBitmap.Decode(PksmIcons.GetPng(name, tint));
        }
        catch { bitmap = null; }
        lock (Gate) Icons[key] = bitmap;
        return bitmap;
    }

    public static void DrawIcon(SKCanvas c, string name, SKRect r, string tint = PksmIcons.White, byte alpha = 0xFF)
    {
        if (Icon(name, tint) is not { } bitmap) return;
        var size = Math.Min(r.Width, r.Height);
        var dest = new SKRect(r.MidX - size / 2, r.MidY - size / 2, r.MidX + size / 2, r.MidY + size / 2);
        using var image = SKImage.FromBitmap(bitmap);
        using var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha) };
        c.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Nearest), paint);
    }

    /// <summary>
    /// A game's square badge: its bundled cartridge art, the Bank's vault, or the
    /// generation-colored cartridge mark when no art is bundled.
    /// </summary>
    public static void DrawGame(SKCanvas c, SKRect r, string? artLabel, int generation, string? colorKey, bool isBank, Action? invalidate, byte alpha = 0xFF)
    {
        var radius = r.Width * 0.14f;
        if (!isBank && Game(artLabel, invalidate) is { } art)
        {
            c.Save();
            c.ClipRoundRect(new SKRoundRect(r, radius, radius), antialias: true);
            using var image = SKImage.FromBitmap(art);
            using var paint = new SKPaint { Color = SKColors.White.WithAlpha(alpha) };
            c.DrawImage(image, r, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
            c.Restore();
            return;
        }
        var era = isBank ? Pksm.ButtonBlue : SaveColors.For(colorKey, generation).ToSKColor();
        using (var fill = new SKPaint { Color = PksmPaint.Darker(era, 0.35f).WithAlpha(alpha), IsAntialias = true })
            c.DrawRoundRect(r, radius, radius, fill);
        using (var edge = new SKPaint { Color = era.WithAlpha(alpha), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1.5f, r.Width * 0.06f) })
            c.DrawRoundRect(r, radius, radius, edge);
        if (isBank)
        {
            var inset = r.Width * 0.16f;
            DrawIcon(c, "bank", new SKRect(r.Left + inset, r.Top + inset, r.Right - inset, r.Bottom - inset), PksmIcons.Native, alpha);
            return;
        }
        GameCartridgeMark.DrawBall(c, new SKPoint(r.MidX, r.MidY), r.Width * 0.26f, era.WithAlpha(alpha), Pksm.Ink.WithAlpha(alpha));
    }
}
