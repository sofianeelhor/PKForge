using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// One of the park's events, announced over the park (see <see cref="ParkEventPaint"/>): the
/// encounter cinematic, then the title plate, the Pokémon standing big by the dialogue box, then "Watch" or "Leave it".
/// Up and Down pick, A chooses, B leaves it; A or a tap during the animation skips to the
/// choices. Left alone it closes by itself, watching in spectator mode.
/// </summary>
public sealed class PokeparkEventOverlay : IPadHandler
{
    private const long WaitFor = 12_000, SpectatorWaitFor = 5000;

    private readonly Grid _host;
    private readonly SKCanvasView _view = new() { EnableTouchEvents = true };
    private readonly GamepadRouter? _router;
    private readonly TaskCompletionSource<bool> _done = new();
    private readonly ParkHighlight _highlight;
    private readonly ParkLifeResident? _star;
    private readonly (Func<long, SKBitmap?> Frame, bool Shiny)[] _sprites;
    private readonly ParkEventPaint.Evolution? _evolution;
    private readonly string[] _choices;
    private readonly Services.VsyncLoop _timer;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly bool _spectating;
    private long _skipped;
    private int _selected;
    private SKRect[] _rects = [];
    private bool _closed;

    /// <summary>True when the player chose to go and see it.</summary>
    public static Task<bool> ShowAsync(Grid host, ParkHighlight highlight, IEnumerable<ParkLifeResident> cast, ISpriteService showdown, PokeparkSpriteService park, bool spectating) =>
        new PokeparkEventOverlay(host, highlight, cast.ToArray(), showdown, park, spectating)._done.Task;

    private PokeparkEventOverlay(Grid host, ParkHighlight highlight, ParkLifeResident[] cast, ISpriteService showdown, PokeparkSpriteService park, bool spectating)
    {
        _host = host; _highlight = highlight; _spectating = spectating;
        _star = cast.FirstOrDefault();
        _sprites = cast.Select(mon => SpriteOf(mon, showdown, park)).ToArray();
        if (highlight.EvolvesInto != 0 && _star is { } evolving)
        {
            var into = SpriteOf(evolving with { Species = highlight.EvolvesInto, Form = 0 }, showdown, park);
            _evolution = new(into.Frame, evolving.Shiny, highlight.StopsEvolving, highlight.Before ?? "");
        }
        // A moment that needs the player (Snorlax across the bridge, Psyduck in the water) says so.
        _choices = highlight.Text.Contains("Talk to it", StringComparison.Ordinal) ? ["Go there", "Leave it"] : ["Watch", "Leave it"];
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _view.BackgroundColor = Colors.Transparent;
        _view.PaintSurface += PaintGuard.Wrap("PokeparkEventOverlay.Paint", Paint);
        _view.Touch += Touch;
        host.Add(_view);
        Grid.SetRowSpan(_view, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(_view, Math.Max(1, host.ColumnDefinitions.Count));
        // A frame on every screen refresh (see VsyncLoop).
        _timer = new Services.VsyncLoop(_ =>
        {
            if (Now > ChoicesAt + (_spectating ? SpectatorWaitFor : WaitFor)) Close(_spectating);
            else _view.InvalidateSurface();
            return true;
        });
        _timer.Start();
        App.Suspended += Later;
        _router?.Push(this);
        try { HapticFeedback.Default.Perform(HapticFeedbackType.LongPress); } catch { }
    }

    // Time in the animation: skipping jumps it straight to the choices.
    private long Now => _clock.ElapsedMilliseconds + _skipped;

    private long ParkAt => ParkEventPaint.ParkAtFor(_evolution is not null);
    private long ChoicesAt => ParkEventPaint.ChoicesAtFor(_evolution is not null);

    private bool ChoicesShown => Now >= ChoicesAt;

    // A first press skips the cinematic, a second the rest of the way to the choices.
    private void Skip() => _skipped = Math.Max(_skipped, (Now < ParkAt ? ParkAt : ChoicesAt) - _clock.ElapsedMilliseconds);

    private void Paint(object? sender, SKPaintSurfaceEventArgs e)
    {
        e.Surface.Canvas.Clear(SKColors.Transparent);
        _rects = ParkEventPaint.Paint(e.Surface.Canvas, e.Info.Width, e.Info.Height, Now, _highlight.Event ?? "",
            _evolution is { Stops: false } ? _highlight.EvolvesIntoName ?? "" : _star?.Name ?? "", _highlight.Text,
            _highlight.Secret, _sprites, _choices, _selected, PixelFont.Face, _evolution);
    }

    // Showdown's animated sprite for each Pokémon in it; the park's own sprite where Showdown has none.
    private static (Func<long, SKBitmap?> Frame, bool Shiny) SpriteOf(ParkLifeResident mon, ISpriteService showdown, PokeparkSpriteService park)
    {
        var look = new SpriteLook(mon.Species, mon.Form, mon.Shiny);
        showdown.WarmShowdown(look, static () => { });
        park.Warm(mon.Species, mon.Form, mon.Shiny, static () => { });
        return (time =>
        {
            if (!showdown.TryGetShowdown(look, out var animated)) return null;
            if (animated is not null) return animated.FrameAt(time);
            return park.GetFrame(mon.Species, mon.Form, mon.Shiny, ParkPose.Idle, 0, time) is { } frame ? Cut(frame) : null;
        }, mon.Shiny);
    }

    // A PMD frame lives on a sheet: the painter wants it on its own.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SKBitmap, Dictionary<SKRect, SKBitmap>> Cuts = new();

    private static SKBitmap Cut(ParkSpriteFrame frame)
    {
        var cuts = Cuts.GetOrCreateValue(frame.Bitmap);
        if (cuts.TryGetValue(frame.Source, out var cut)) return cut;
        cut = new SKBitmap((int)frame.Source.Width, (int)frame.Source.Height);
        frame.Bitmap.ExtractSubset(cut, SKRectI.Round(frame.Source));
        return cuts[frame.Source] = cut;
    }

    private void Touch(object? sender, SKTouchEventArgs e)
    {
        e.Handled = true;
        if (e.ActionType != SKTouchAction.Released) return;
        if (!ChoicesShown) { Skip(); return; }
        for (var i = 0; i < _rects.Length; i++)
            if (_rects[i].Contains(e.Location)) { Close(i == 0); return; }
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.A or PadButton.B when !ChoicesShown: Skip(); break;
            case PadButton.Up or PadButton.Down or PadButton.Left or PadButton.Right: _selected = 1 - _selected; break;
            case PadButton.A: Close(_selected == 0); break;
            case PadButton.B: Close(false); break;
        }
        return true;
    }

    private void Later() => Close(false);

    private void Close(bool go)
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        App.Suspended -= Later;
        _router?.Remove(this);
        _host.Remove(_view);
        _done.TrySetResult(go);
    }
}
