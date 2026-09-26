using System.Diagnostics;

namespace PKForge.App.Services;

/// <summary>
/// A critically damped spring toward a target: moves fast, eases in, never overshoots unless
/// asked to. The feel of the games' box cursor and lifted boxes, independent of frame rate.
/// </summary>
public sealed class Spring(float value)
{
    public float Value { get; private set; } = value;
    public float Target { get; set; } = value;
    public float Velocity { get; private set; }

    public bool Settled => MathF.Abs(Target - Value) < 0.001f && MathF.Abs(Velocity) < 0.001f;

    /// <summary>Jumps straight to <paramref name="value"/> with no motion.</summary>
    public void Snap(float value)
    {
        Value = Target = value;
        Velocity = 0;
    }

    /// <summary>Advances by <paramref name="dt"/> seconds. Lower <paramref name="damping"/> than 2√stiffness gives a bounce.</summary>
    public void Step(float dt, float stiffness = 420f, float damping = 41f)
    {
        // Small sub-steps keep the integration stable through a dropped frame.
        var steps = Math.Max(1, (int)MathF.Ceiling(dt / (1f / 240f)));
        var h = dt / steps;
        for (var i = 0; i < steps; i++)
        {
            Velocity += (stiffness * (Target - Value) - damping * Velocity) * h;
            Value += Velocity * h;
        }
        if (Settled) Snap(Target);
    }
}

/// <summary>
/// Paces a canvas animation on the display's own refresh: each paint advances the motion by
/// the real time since the last one, and asks for the next frame only while something still
/// moves. No timer, so frames land on vsync and an idle screen costs nothing.
/// </summary>
public sealed class FramePacer(FrameInvalidator frame)
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private long _last;
    private bool _running;

    /// <summary>Seconds since the pacer was created; for looping effects like a bob.</summary>
    public float Now => (float)_watch.Elapsed.TotalSeconds;

    /// <summary>Starts (or keeps) the frame loop going.</summary>
    public void Kick()
    {
        if (!_running)
        {
            _running = true;
            _last = _watch.ElapsedTicks;
        }
        frame.Request();
    }

    /// <summary>Call first thing in a paint: seconds to advance by (0 while idle).</summary>
    public float Advance()
    {
        if (!_running) return 0;
        var now = _watch.ElapsedTicks;
        var dt = (float)((now - _last) / (double)Stopwatch.Frequency);
        _last = now;
        return Math.Clamp(dt, 0, 0.05f);
    }

    /// <summary>Call last in a paint: queues the next frame while <paramref name="animating"/>.</summary>
    public void Continue(bool animating)
    {
        _running = animating;
        if (animating) frame.Request();
    }
}
