using Android.Views;

namespace PKForge.App.Services;

/// <summary>
/// A loop that runs once per display refresh, on the UI thread, from the display's own vsync
/// (Android's Choreographer). A dispatcher timer of 16 ms is not that: Android re-arms it only
/// after each tick has run, so it really fires every 17 to 20 ms, drifts against the screen,
/// and every few frames a frame goes by with nothing new drawn. Motion stepped from this loop
/// lands on every frame, and the step is told the real time since the last one.
/// </summary>
public sealed class VsyncLoop : Java.Lang.Object, Choreographer.IFrameCallback
{
    private readonly Func<float, bool> _step;
    private long _last;
    private bool _posted;

    /// <param name="step">Called each frame with the milliseconds since the last one (at most 50);
    /// returns whether the loop goes on.</param>
    public VsyncLoop(Func<float, bool> step) => _step = step;

    public bool IsRunning { get; private set; }

    /// <summary>Starts the loop (from the UI thread); nothing happens if it already runs.</summary>
    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _last = Java.Lang.JavaSystem.NanoTime();
        Post();
    }

    /// <summary>Stops the loop (from the UI thread): no further step runs.</summary>
    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _posted = false;
        Choreographer.Instance?.RemoveFrameCallback(this);
    }

    private void Post()
    {
        // One callback per frame, even when a step stops and restarts the loop.
        if (_posted) return;
        _posted = true;
        Choreographer.Instance?.PostFrameCallback(this);
    }

    public void DoFrame(long frameTimeNanos)
    {
        _posted = false;
        if (!IsRunning) return;
        // The first frame's vsync can predate the start: count it as one frame at 60 Hz.
        var elapsed = (frameTimeNanos - _last) / 1_000_000f;
        elapsed = elapsed <= 0 ? 1000f / 60f : Math.Min(elapsed, 50f);
        _last = frameTimeNanos;
        bool more;
        try { more = _step(elapsed); }
        catch (Exception error)
        {
            // A frame callback that throws would take the app down with it.
            IsRunning = false;
            AppLog.Error("frame", "An animation step failed; it was stopped", error);
            return;
        }
        if (!IsRunning) return; // the step stopped it
        if (more) Post();
        else IsRunning = false;
    }
}
