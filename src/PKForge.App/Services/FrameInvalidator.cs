using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Services;

/// <summary>
/// Collapses a burst of repaint requests into a single queued InvalidateSurface.
/// When a whole box of sprites finishes loading at once, the canvas repaints once -
/// not once per sprite - and background callbacks are marshalled to the UI thread safely.
/// </summary>
public sealed class FrameInvalidator(SKCanvasView view)
{
    private int _pending;

    public void Request()
    {
        // If a repaint is already queued, drop this one: the pending paint will show
        // whatever finished in the meantime. Only the first request schedules work.
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            Interlocked.Exchange(ref _pending, 0);
            view.InvalidateSurface();
        });
    }
}
