using SkiaSharp;
using SkiaSharp.Views.Maui;

namespace PKForge.App.Views;

/// <summary>
/// Touch scrolling for the lists drawn on a canvas: drag, a flick that keeps going and slows
/// down, and a scroll thumb you can grab (or tap on its track) to jump through a long list.
/// It works in whatever units the caller measures in (the touch point, the offset and the
/// track share them); a press that neither drags nor grabs the thumb comes back as a tap.
/// The thumb is for touch: it shows when a finger touches the list and fades out shortly
/// after the list stops moving, so pad players never see it.
/// </summary>
internal sealed class TouchScroller
{
    private const float FlingFriction = 0.95f; // velocity kept per 16 ms frame
    private const long ShowMs = 900;  // the thumb stays this long after the last movement
    private const long FadeMs = 300;  // then fades out over this long
    private readonly IDispatcher _dispatcher;
    private readonly Func<float> _offset;
    private readonly Action<float> _scrollTo;
    private readonly Func<float> _max;
    private readonly Func<float> _viewport;
    private readonly Func<SKRect> _track;
    private readonly float _slop;
    private readonly float _grabWidth;

    private SKPoint _start;
    private float _startOffset;
    private float _grab;
    private bool _dragging;
    private readonly List<(long Ms, float Y)> _trail = [];
    private Services.VsyncLoop? _fling;
    private IDispatcherTimer? _fade;
    private float _velocity; // units per ms
    private long _lastActive = long.MinValue / 2;

    /// <param name="track">The scrollbar's track, where the thumb runs.</param>
    /// <param name="slop">How far a finger moves before a press becomes a drag.</param>
    /// <param name="grabWidth">How far left of the track a press still grabs the thumb.</param>
    public TouchScroller(IDispatcher dispatcher, Func<float> offset, Action<float> scrollTo, Func<float> max,
        Func<float> viewport, Func<SKRect> track, float slop, float grabWidth)
    {
        _dispatcher = dispatcher;
        _offset = offset;
        _scrollTo = scrollTo;
        _max = max;
        _viewport = viewport;
        _track = track;
        _slop = slop;
        _grabWidth = grabWidth;
    }

    /// <summary>True while the thumb is held: the caller draws it wider.</summary>
    public bool HoldingThumb { get; private set; }

    /// <summary>How visible the thumb is, 0 to 1: the caller scales its alpha and skips it at 0.</summary>
    public float Visibility
    {
        get
        {
            if (HoldingThumb || _dragging) return 1;
            var idle = Environment.TickCount64 - _lastActive;
            return idle <= ShowMs ? 1 : Math.Clamp(1 - (idle - ShowMs) / (float)FadeMs, 0, 1);
        }
    }

    // A touch or a moving list keeps the thumb shown; once it rests, a timer repaints the fade.
    private void Touched()
    {
        _lastActive = Environment.TickCount64;
        if (_fade is null)
        {
            _fade = _dispatcher.CreateTimer();
            _fade.Interval = TimeSpan.FromMilliseconds(32);
            _fade.Tick += (_, _) =>
            {
                if (Environment.TickCount64 - _lastActive < ShowMs) return;
                _scrollTo(_offset()); // repaint
                if (Visibility <= 0) _fade!.Stop();
            };
        }
        _fade.Start();
    }

    /// <summary>The thumb's rectangle on the track, or empty when the list fits.</summary>
    public SKRect Thumb()
    {
        var max = _max();
        if (max <= 0) return SKRect.Empty;
        var track = _track();
        var viewport = _viewport();
        var length = Math.Max(Math.Min(track.Height, 48 * Math.Max(1, _slop / 8)), track.Height * viewport / (viewport + max));
        var top = track.Top + (track.Height - length) * Math.Clamp(_offset() / max, 0, 1);
        return new SKRect(track.Left, top, track.Right, top + length);
    }

    public void Stop() => _fling?.Stop();

    /// <summary>Feeds one touch event; returns the point of a tap (a press released without dragging).</summary>
    public SKPoint? Handle(SKTouchAction action, SKPoint point)
    {
        var now = Environment.TickCount64;
        switch (action)
        {
            case SKTouchAction.Pressed:
                Stop();
                // Only a thumb the player can see is grabbed; a hidden one leaves the press to the list.
                var visible = Visibility > 0;
                Touched();
                _start = point;
                _startOffset = _offset();
                _dragging = false;
                _trail.Clear();
                _trail.Add((now, point.Y));
                var thumb = Thumb();
                if (visible && !thumb.IsEmpty && point.X >= _track().Left - _grabWidth)
                {
                    // On the thumb: hold it where it was taken. On the track: the thumb jumps under the finger.
                    HoldingThumb = true;
                    _grab = point.Y >= thumb.Top && point.Y <= thumb.Bottom ? point.Y - thumb.Top : thumb.Height / 2;
                    MoveThumb(point.Y);
                }
                return null;
            case SKTouchAction.Moved:
                Touched();
                if (HoldingThumb) { MoveThumb(point.Y); return null; }
                if (!_dragging && Math.Abs(point.Y - _start.Y) > _slop) _dragging = true;
                if (_dragging)
                {
                    _scrollTo(Math.Clamp(_startOffset - (point.Y - _start.Y), 0, _max()));
                    _trail.Add((now, point.Y));
                    if (_trail.Count > 8) _trail.RemoveAt(0);
                }
                return null;
            case SKTouchAction.Released:
                Touched();
                if (HoldingThumb) { HoldingThumb = false; _scrollTo(_offset()); return null; }
                if (_dragging) { _dragging = false; Fling(now); return null; }
                return point;
            case SKTouchAction.Cancelled:
                HoldingThumb = _dragging = false;
                return null;
            default:
                return null;
        }
    }

    private void MoveThumb(float y)
    {
        var track = _track();
        var thumb = Thumb();
        var room = track.Height - thumb.Height;
        if (room <= 0) return;
        var fraction = Math.Clamp((y - _grab - track.Top) / room, 0, 1);
        _scrollTo(fraction * _max());
    }

    private void Fling(long now)
    {
        // The finger's speed over its last ~100 ms; a finger that stopped before lifting does not fling.
        var recent = _trail.Where(t => now - t.Ms <= 100).ToList();
        if (recent.Count < 2 || recent[^1].Ms == recent[0].Ms) return;
        _velocity = -(recent[^1].Y - recent[0].Y) / (recent[^1].Ms - recent[0].Ms);
        if (Math.Abs(_velocity) * 16 < _slop * 0.25f) return;
        // On each screen refresh, by the time that really passed: the glide is even, and as long
        // as it was when a 16 ms timer stepped it.
        _fling ??= new Services.VsyncLoop(elapsed =>
        {
            var target = Math.Clamp(_offset() + _velocity * elapsed, 0, _max());
            _velocity *= MathF.Pow(FlingFriction, elapsed / 16f);
            Touched();
            _scrollTo(target);
            return !(target <= 0 || target >= _max() || Math.Abs(_velocity) * 16 < _slop * 0.05f);
        });
        _fling.Start();
    }
}
