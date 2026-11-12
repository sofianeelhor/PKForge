using PKForge.App.Services;
using SkiaSharp.Views.Maui;

namespace PKForge.App.Views;

/// <summary>
/// A paint handler runs inside Android's OnDraw: an exception there is not caught by anything
/// and closes the app (a Gen 1 box read past its 20th slot did exactly that). Guarded, a
/// failed frame is logged once and drawn blank; the next frame tries again.
/// </summary>
public static class PaintGuard
{
    private static readonly HashSet<string> Reported = [];

    public static EventHandler<SKPaintSurfaceEventArgs> Wrap(string area, EventHandler<SKPaintSurfaceEventArgs> paint) =>
        (sender, args) =>
        {
            var probe = PaintClock.Start();
            try { paint(sender, args); }
            catch (Exception error) { Failed(area, args.Surface, error); }
            PaintClock.Stop(area, probe);
        };

    /// <summary>The same guard for a GPU-backed view (the park).</summary>
    public static EventHandler<SKPaintGLSurfaceEventArgs> WrapGl(string area, EventHandler<SKPaintGLSurfaceEventArgs> paint) =>
        (sender, args) =>
        {
            var probe = PaintClock.Start();
            try { paint(sender, args); }
            catch (Exception error) { Failed(area, args.Surface, error); }
            PaintClock.Stop(area, probe);
        };

    private static void Failed(string area, SkiaSharp.SKSurface surface, Exception error)
    {
        try { surface.Canvas.Clear(); } catch (Exception) { /* the frame stays as Skia left it */ }
        bool first;
        lock (Reported) first = Reported.Add(area);
        if (first) AppLog.Error("paint", $"{area} could not be drawn; the frame was left blank", error);
    }
}
