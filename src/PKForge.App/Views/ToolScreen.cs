using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The shell the painted storage tools (Sort, Batch editor, Presets) share: a full-screen canvas
/// over the logo grid that owns the pad while open. The d-pad walks the painted targets by where
/// they sit; a tap selects, a second tap on the same target acts; a drag in the scrolling area
/// scrolls; the key prompts at the foot are tappable. Each screen paints and decides what A, X,
/// Y, L and R do.
/// </summary>
internal abstract class ToolScreen : IPadHandler
{
    protected readonly Grid Host;
    protected readonly SKCanvasView Canvas = new() { EnableTouchEvents = true };
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Grid? _overlay;
    protected ToolHits? Hits;
    protected int Focus;
    protected float Scroll;
    private bool _busy;

    private SKPoint? _press;
    private bool _dragging;
    private float _scrollStart;

    protected ToolScreen(Grid host)
    {
        Host = host;
        Canvas.PaintSurface += PaintGuard.Wrap("ToolScreen Canvas", (_, args) =>
        {
            args.Surface.Canvas.Clear(SKColors.Transparent);
            Hits = Paint(args.Surface.Canvas, args.Info);
        });
        Canvas.Touch += OnTouch;
    }

    /// <summary>Shows the screen until it closes.</summary>
    public async Task RunAsync()
    {
        var screen = new Grid { Padding = new Thickness(14, 12, 14, 8), Children = { Canvas } };
        _overlay = Kit.AttachOverlay(Host, new Grid { Children = { DsChrome.GridBackground(), screen } });
        var router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        router?.Push(this);
        try { await _closed.Task; }
        finally
        {
            router?.Remove(this);
            Host.Remove(_overlay);
        }
    }

    protected void Close() => _closed.TrySetResult();
    protected void Redraw() => Canvas.InvalidateSurface();

    protected abstract ToolHits Paint(SKCanvas canvas, SKImageInfo info);

    /// <summary>The key prompts painted at the foot, with the button each one stands for.</summary>
    protected abstract IReadOnlyList<(string Key, string Label, PadButton Button)> Prompts { get; }

    /// <summary>A or a second tap on the focused target.</summary>
    protected abstract Task ActivateAsync(int id);

    /// <summary>A first tap on a target: focuses it by default (choice chips also choose).</summary>
    protected virtual void Select(int id, SKPoint at) => Focus = id;

    /// <summary>Left/right on the focused target, before the d-pad walks: true when used (a value cycled).</summary>
    protected virtual bool Horizontal(int direction) => false;

    /// <summary>Up/down on the focused target, before the d-pad walks: true when used (a list scrolled).</summary>
    protected virtual bool Vertical(int direction) => false;

    /// <summary>X, Y, L, R and Start: true when used.</summary>
    protected abstract bool Button(PadButton button);

    protected virtual float MaxScroll => Hits?.MaxScroll ?? 0f;
    protected virtual void Scrolled() { }

    /// <summary>Runs an async step (a popup, a write) with the pad held off this screen.</summary>
    protected async Task RunBusyAsync(Func<Task> step)
    {
        if (_busy) return;
        _busy = true;
        try { await step(); }
        finally
        {
            _busy = false;
            Redraw();
        }
    }

    protected void Activate(int id) => _ = RunBusyAsync(() => ActivateAsync(id));

    public bool OnPadButton(PadButton button)
    {
        if (_busy) return true;
        switch (button)
        {
            case PadButton.Up: if (!Vertical(-1)) Walk(0, -1); break;
            case PadButton.Down: if (!Vertical(1)) Walk(0, 1); break;
            case PadButton.Left: if (!Horizontal(-1)) Walk(-1, 0); break;
            case PadButton.Right: if (!Horizontal(1)) Walk(1, 0); break;
            case PadButton.A: Activate(Focus); break;
            case PadButton.B: Close(); break;
            default: Button(button); break;
        }
        Redraw();
        return true; // modal while open
    }

    /// <summary>Moves the focus to the nearest target that way, measured between centres.</summary>
    protected bool Walk(int dx, int dy)
    {
        if (Hits is not { } hits || hits.Targets.FirstOrDefault(t => t.Id == Focus) is not { Rect.Width: > 0 } from) return false;
        var origin = new SKPoint(from.Rect.MidX, from.Rect.MidY);
        var best = -1;
        var bestScore = float.MaxValue;
        foreach (var (id, rect) in hits.Targets)
        {
            if (id == Focus || !Walkable(id)) continue;
            var along = dx != 0 ? (rect.MidX - origin.X) * dx : (rect.MidY - origin.Y) * dy;
            // Must lie that way, past the edge of the current target (not merely beside it).
            var edge = dx != 0 ? from.Rect.Width / 2 : from.Rect.Height / 2;
            if (along < edge * 0.5f) continue;
            var across = dx != 0 ? Math.Abs(rect.MidY - origin.Y) : Math.Abs(rect.MidX - origin.X);
            var score = along + across * 2.2f;
            if (score < bestScore) { bestScore = score; best = id; }
        }
        if (best < 0) return false;
        Focus = best;
        return true;
    }

    /// <summary>Whether the d-pad may land on a target (a big area like a list may opt out).</summary>
    protected virtual bool Walkable(int id) => true;

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (_busy || Hits is not { } hits) return;
        var point = args.Location;
        switch (args.ActionType)
        {
            case SKTouchAction.Pressed:
                _press = point;
                _dragging = false;
                _scrollStart = Scroll;
                break;
            case SKTouchAction.Moved when _press is { } start:
                if (_dragging || (hits.ScrollArea.Contains(start) && Math.Abs(point.Y - start.Y) > 18 * hits.Unit))
                {
                    _dragging = true;
                    Scroll = Math.Clamp(_scrollStart - (point.Y - start.Y) / hits.Unit, 0f, MaxScroll);
                    Scrolled();
                    Redraw();
                }
                break;
            case SKTouchAction.Released:
                var tapped = !_dragging && _press is not null;
                _press = null;
                _dragging = false;
                if (!tapped) return;
                for (var i = 0; i < hits.Prompts.Count && i < Prompts.Count; i++)
                    if (hits.Prompts[i].Contains(point))
                    {
                        OnPadButton(Prompts[i].Button);
                        return;
                    }
                // The smallest target under the finger wins (a chip inside a panel).
                var hit = hits.Targets.Where(t => t.Rect.Contains(point)).OrderBy(t => t.Rect.Width * t.Rect.Height).FirstOrDefault();
                if (hit.Rect.Width <= 0) return;
                if (hit.Id == Focus) Activate(hit.Id);
                else Select(hit.Id, point);
                Redraw();
                break;
            case SKTouchAction.Cancelled:
                _press = null;
                _dragging = false;
                break;
        }
    }

    // ── Shared look-ups ──

    // One image per icon cell of the sheet, cut once for the app's lifetime.
    private static readonly Dictionary<(SKBitmap, SKRectI), SKImage> Icons = [];

    /// <summary>The storage screen's box icon for <paramref name="look"/> (Showdown's sheet), or the
    /// sprite while there is none; null while it loads (<paramref name="loaded"/> is called then).</summary>
    internal static SKImage? IconOf(ISpriteService sprites, SpriteLook look, Action loaded)
    {
        if (!sprites.TryGetShowdownIcon(look, loaded, out var sheet, out var cell)) return null;
        if (sheet is not null)
            lock (Icons)
            {
                if (Icons.TryGetValue((sheet, cell), out var icon)) return icon;
                icon = SpriteImages.Of(sheet).Subset(cell);
                if (icon is not null) Icons[(sheet, cell)] = icon;
                return icon;
            }
        if (sprites.GetSprite(look) is { } sprite) return SpriteImages.Of(sprite);
        sprites.Warm(look, loaded);
        return null;
    }

    /// <summary>The big sprite for <paramref name="look"/>, or null while it loads.</summary>
    internal static SKImage? SpriteOf(ISpriteService sprites, SpriteLook look, Action loaded)
    {
        if (sprites.GetSprite(look) is { } bitmap) return SpriteImages.Of(bitmap);
        sprites.Warm(look, loaded);
        return null;
    }

    /// <summary>The prompts as the painter takes them.</summary>
    protected IReadOnlyList<(string Key, string Label)> PromptLabels() => [.. Prompts.Select(p => (p.Key, p.Label))];

    /// <summary>Calls back on the UI thread to repaint (sprites finishing their load).</summary>
    protected void RedrawSoon() => MainThread.BeginInvokeOnMainThread(Redraw);
}
