using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The PKForge statue's credits, as the games end: the park dims behind, little stars twinkle,
/// and the names roll up the screen by themselves, the PKForge logo first. Each section has its
/// heading in gold between two diamonds; each name has what they did underneath; the Pokémon
/// living in the park walk beside the names of those who drew them. Up and Down (or a finger)
/// take over the scrolling for a moment; B, A or a tap close it.
/// </summary>
public sealed class PokeparkCreditsOverlay : IPadHandler
{

    private readonly Grid _host;
    private readonly SKCanvasView _view = new() { EnableTouchEvents = true };
    private readonly GamepadRouter? _router;
    private readonly TaskCompletionSource _done = new();
    private readonly IReadOnlyList<ParkCreditsPaint.Section> _sections;
    private readonly PokeparkSpriteService _sprites;
    private readonly SKBitmap? _logo;
    private readonly Services.VsyncLoop _timer;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private float _scroll, _contentHeight, _height;
    private long _lastTick, _heldUntil;
    private float? _dragFrom;
    private float _dragScroll;
    private bool _dragged, _closed;

    public static Task ShowAsync(Grid host, IReadOnlyList<ParkCreditsPaint.Section> sections, PokeparkSpriteService sprites) =>
        new PokeparkCreditsOverlay(host, sections, sprites)._done.Task;

    private PokeparkCreditsOverlay(Grid host, IReadOnlyList<ParkCreditsPaint.Section> sections, PokeparkSpriteService sprites)
    {
        _host = host; _sections = sections; _sprites = sprites;
        if (ThemeLogo.Png() is { } png) _logo = SKBitmap.Decode(png);
        foreach (var entry in sections.SelectMany(s => s.Entries))
            if (entry.Sprite is { } look) sprites.Warm(look.Species, look.Form, look.Shiny, static () => { });
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _view.BackgroundColor = Colors.Transparent;
        _view.PaintSurface += PaintGuard.Wrap("PokeparkCreditsOverlay.Paint", Paint);
        _view.Touch += Touch;
        host.Add(_view);
        Grid.SetRowSpan(_view, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(_view, Math.Max(1, host.ColumnDefinitions.Count));
        // A frame on every screen refresh (see VsyncLoop).
        _timer = new Services.VsyncLoop(_ =>
        {
            _view.InvalidateSurface();
            return true;
        });
        _timer.Start();
        App.Suspended += Close;
        _router?.Push(this);
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var (w, h) = (e.Info.Width, e.Info.Height);
        var unit = w / 1920f;
        _height = h;
        var now = _clock.ElapsedMilliseconds;
        // The names roll up by themselves, unless the player is scrolling.
        if (now > _heldUntil && _dragFrom is null)
            _scroll = Math.Min(_scroll + (now - _lastTick) * .045f * unit, MaxScroll(unit));
        _lastTick = now;
        // The park shows through: start each frame from clear.
        e.Surface.Canvas.Clear(SKColors.Transparent);
        _contentHeight = ParkCreditsPaint.Paint(e.Surface.Canvas, w, h, _sections, _scroll, now, _logo, PixelFont.Face, (look, time) =>
            _sprites.GetFrame(look.Species, look.Form, look.Shiny, ParkPose.Walk, 0, time) is { } frame ? new(frame.Bitmap, frame.Source) : null);
    }

    private float MaxScroll(float unit) => Math.Max(0, _contentHeight - _height * .38f + 60 * unit);

    private void Hold() => _heldUntil = _clock.ElapsedMilliseconds + 4000;

    private void Scroll(float by)
    {
        Hold();
        _scroll = Math.Clamp(_scroll + by, 0, MaxScroll(_height / 1080f));
    }

    private void Touch(object? sender, SKTouchEventArgs e)
    {
        e.Handled = true;
        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:
                _dragFrom = e.Location.Y; _dragScroll = _scroll; _dragged = false;
                break;
            case SKTouchAction.Moved when _dragFrom is { } from:
                if (MathF.Abs(e.Location.Y - from) > 12) _dragged = true;
                Hold();
                _scroll = Math.Clamp(_dragScroll + from - e.Location.Y, 0, MaxScroll(_height / 1080f));
                break;
            case SKTouchAction.Released:
                if (!_dragged) Close();
                _dragFrom = null;
                break;
        }
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Up: Scroll(-_height / 4); break;
            case PadButton.Down: Scroll(_height / 4); break;
            case PadButton.L: Scroll(-_height * .8f); break;
            case PadButton.R: Scroll(_height * .8f); break;
            case PadButton.B or PadButton.A: Close(); break;
        }
        return true;
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        App.Suspended -= Close;
        _router?.Remove(this);
        _host.Remove(_view);
        _logo?.Dispose();
        _done.TrySetResult();
    }
}
