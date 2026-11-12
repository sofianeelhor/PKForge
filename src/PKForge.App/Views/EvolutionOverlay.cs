using System.Diagnostics;
using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The evolution scene (<see cref="EvolutionScenePaint"/>) played over a page once an evolution
/// has been written: the silhouettes trade places, the stage flashes, the new Pokémon appears
/// in colour and the line says what happened. It only ever starts after the write, never delays one,
/// lasts three seconds, and any button or tap ends it at once. It plays only with the Animations
/// setting on Full; otherwise the caller says it in words.
/// </summary>
internal sealed class EvolutionOverlay
{
    private static EvolutionOverlay? _current;

    private readonly Grid _host;
    private readonly SKCanvasView _canvas = new() { EnableTouchEvents = true, ZIndex = 60 };
    private readonly FrameInvalidator _frame;
    private readonly PadOverlay _buttons;
    private readonly EvolutionMon _from;
    private readonly EvolutionMon _into;
    private readonly string _line;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly SKPaint _dot = new() { IsAntialias = false };
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SKSurface? _art;
    private bool _done;

    /// <summary>
    /// Plays the scene for <paramref name="choice"/> over <paramref name="host"/> (a page's root
    /// grid). Call only once the evolution has been written. Completes when the scene has ended
    /// (or at once when it does not play: Animations not on Full, or the sprites cannot be read).
    /// </summary>
    public static async Task PlayAsync(Grid host, EvolutionChoice choice, ISpriteService sprites)
    {
        if (!MotionSetting.Decorative || host.Handler is null) return;
        var shiny = choice.Plan.Shiny;
        var from = await FrontAsync(sprites, SpriteLook.Of(choice.Plan.Species, choice.Plan.Form, shiny));
        var into = await FrontAsync(sprites, SpriteLook.Of(choice.Option.Species, choice.Option.Form, shiny));
        if (from is null || into is null || host.Handler is null) return;
        _current?.Finish();
        var scene = new EvolutionOverlay(host, new EvolutionMon(from), new EvolutionMon(into), choice.Line);
        _current = scene;
        await scene._ended.Task;
    }

    /// <summary>Ends the scene on screen, if any (the page is leaving).</summary>
    public static void Stop() => _current?.Finish();

    /// <summary>The Gen 5 front (PKHeX's art when Showdown has none), waiting a moment for a load; null when there is none.</summary>
    private static async Task<SKBitmap?> FrontAsync(ISpriteService sprites, SpriteLook look)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (sprites.TryGetShowdownFront(look, () => loaded.TrySetResult(), out var front))
                return front ?? sprites.GetSprite(look);
            // The menu warmed both looks, so this is rare: wait for the load, never long.
            await Task.WhenAny(loaded.Task, Task.Delay(600));
        }
        return sprites.GetSprite(look);
    }

    private EvolutionOverlay(Grid host, EvolutionMon from, EvolutionMon into, string line)
    {
        _host = host;
        _from = from;
        _into = into;
        _line = line;
        _frame = new FrameInvalidator(_canvas);
        host.Add(_canvas);
        Grid.SetRowSpan(_canvas, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(_canvas, Math.Max(1, host.ColumnDefinitions.Count));
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;
        // Every button skips it, and none reaches the page while it plays.
        _buttons = new PadOverlay(Finish, Finish, _ => { Finish(); return true; });
        // If the screen stops painting (the page left), the scene still gives the buttons back.
        host.Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(EvolutionScenePaint.Length / (double)EvolutionScenePaint.FramesPerSecond + 0.5), Finish);
    }

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType == SKTouchAction.Pressed) Finish();
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var probe = PaintClock.Start();
        PaintFrame(args);
        PaintClock.Stop("Evolution", probe);
    }

    private void PaintFrame(SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_done) return;
        var tick = (int)(_clock.Elapsed.TotalSeconds * EvolutionScenePaint.FramesPerSecond);
        if (tick >= EvolutionScenePaint.Length)
        {
            // Queued, never run inside this paint: taking the view off the page while SkiaSharp
            // is still drawing it frees its bitmap under it (see PortalOverlay).
            _canvas.Dispatcher.Dispatch(Finish);
            return;
        }
        // Art pixels at a whole step of the screen, drawn small and enlarged with nearest sampling.
        var scale = EvolutionScenePaint.ScaleFor(args.Info.Width, args.Info.Height);
        var width = (args.Info.Width + scale - 1) / scale;
        var height = (args.Info.Height + scale - 1) / scale;
        if (_art is null || _art.Canvas.DeviceClipBounds.Width != width || _art.Canvas.DeviceClipBounds.Height != height)
        {
            _art?.Dispose();
            _art = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        }
        if (_art is null) return;
        _art.Canvas.Clear(SKColors.Transparent);
        var caption = EvolutionScenePaint.Draw(_art.Canvas, width, height, _from, _into, _line.Length, tick, _dot);
        using (var frame = _art.Snapshot())
            canvas.DrawImage(frame, SKRect.Create(0, 0, width * scale, height * scale), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        if (caption is { } line) EvolutionScenePaint.Caption(canvas, line, SKPoint.Empty, scale, _line, BoxBrowserPage.PixelTypeface());
        _frame.Request();
    }

    private void Finish()
    {
        if (_done) return;
        _done = true;
        if (_current == this) _current = null;
        _buttons.Dispose();
        _canvas.PaintSurface -= Paint;
        _canvas.Touch -= Touch;
        _canvas.InputTransparent = true;
        _canvas.IsVisible = false;
        // Off the page and freed on the next turn: a touch or paint of this view may still be running.
        _host.Dispatcher.Dispatch(() =>
        {
            _host.Remove(_canvas);
            _from.Dispose();
            _into.Dispose();
            _art?.Dispose();
            _dot.Dispose();
        });
        _ended.TrySetResult();
    }
}
