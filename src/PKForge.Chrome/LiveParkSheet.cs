using PKForge.Domain;
using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>One PMD animation facing one way, as the park's sprite service holds it: cells in a sheet, feet from each cell's center, durations.</summary>
public sealed record LiveParkSource(string Clip, SKBitmap Bitmap, SKRectI[] Cells, SKPoint[] Feet, int[] Durations);

/// <summary>
/// Cuts a resident's PMD animations into the small sheet the living park widget reads while
/// PKForge is closed: each frame trimmed to its opaque pixels, one clip per row, with its feet and
/// duration in <see cref="ParkLiveClip"/>. A few kilobytes per resident.
/// </summary>
public static class LiveParkSheet
{
    public static (SKBitmap Sheet, ParkLiveClip[] Clips)? Build(IReadOnlyList<LiveParkSource> sources)
    {
        var rows = new List<(string Clip, List<(SKBitmap Bitmap, SKRectI Cut, int FeetX, int FeetY, int Ms)> Frames)>();
        foreach (var source in sources)
        {
            var frames = new List<(SKBitmap, SKRectI, int, int, int)>();
            for (var i = 0; i < source.Cells.Length; i++)
            {
                var cell = source.Cells[i];
                SKRectI cut;
                lock (source.Bitmap) cut = Opaque(source.Bitmap, cell);
                if (cut.IsEmpty) continue;
                // Feet from the cell's center to the trimmed frame's top-left corner.
                var feetX = (int)MathF.Round(cell.Left + cell.Width / 2f + source.Feet[i].X) - cut.Left;
                var feetY = (int)MathF.Round(cell.Top + cell.Height / 2f + source.Feet[i].Y) - cut.Top;
                frames.Add((source.Bitmap, cut, feetX, feetY, source.Durations[i]));
            }
            if (frames.Count > 0) rows.Add((source.Clip, frames));
        }
        if (rows.Count == 0) return null;
        var width = rows.Max(r => r.Frames.Sum(f => f.Cut.Width + 1));
        var height = rows.Sum(r => r.Frames.Max(f => f.Cut.Height) + 1);
        if (width > 4096 || height > 4096) return null;
        var sheet = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var clips = new List<ParkLiveClip>(rows.Count);
        using (var canvas = new SKCanvas(sheet))
        {
            canvas.Clear(SKColors.Transparent);
            var y = 0;
            foreach (var (clip, frames) in rows)
            {
                var x = 0;
                var list = new List<ParkLiveFrame>(frames.Count);
                foreach (var (bitmap, cut, feetX, feetY, ms) in frames)
                {
                    lock (bitmap) canvas.DrawBitmap(bitmap, cut, SKRect.Create(x, y, cut.Width, cut.Height));
                    list.Add(new(x, y, cut.Width, cut.Height, feetX, feetY, ms));
                    x += cut.Width + 1;
                }
                clips.Add(new(clip, [.. list]));
                y += frames.Max(f => f.Cut.Height) + 1;
            }
        }
        return (sheet, [.. clips]);
    }

    private static SKRectI Opaque(SKBitmap bitmap, SKRectI cell)
    {
        int minX = cell.Right, minY = cell.Bottom, maxX = -1, maxY = -1;
        for (var y = Math.Max(0, cell.Top); y < Math.Min(bitmap.Height, cell.Bottom); y++)
            for (var x = Math.Max(0, cell.Left); x < Math.Min(bitmap.Width, cell.Right); x++)
                if (bitmap.GetPixel(x, y).Alpha > 0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        return maxX < 0 ? SKRectI.Empty : new SKRectI(minX, minY, maxX + 1, maxY + 1);
    }
}
