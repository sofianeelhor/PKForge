using System.Diagnostics;
using PKForge.App.Services;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>A Pokémon leaving through the portal: its slot as the grid drew it, and its cell
/// in the shown box (null when it sits in another box or in the party).</summary>
internal readonly record struct PortalSource(Domain.SlotSummary Slot, int? Cell);

/// <summary>
/// The transfer portal (<see cref="PortalPaint"/>) played over a box grid after a send to the
/// Bank or another game has succeeded: the Pokémon sinks through a warp pad where it stood, a
/// group hopping onto one pad one after another. It only ever starts after the write, never delays
/// one, lasts about a second, and any button or tap ends it at once. It plays only with the
/// Animations setting on Full.
/// </summary>
internal sealed class PortalOverlay
{
    private static PortalOverlay? _current;
    private static Task<(SKImage Pad, SKImage Star)?>? _art;

    private readonly SKCanvasView _canvas;
    private readonly SKCanvasView _source;
    private readonly FrameInvalidator _frame;
    private readonly PadOverlay _buttons;
    private readonly PortalMember[] _members;
    private readonly PortalMon[] _mons;
    private readonly SKImage _pad;
    private readonly SKImage _star;
    // Where art pixel (0, 0) sits on the host, in device-independent units, and one art pixel's size there.
    private readonly SKPoint _origin;
    private readonly float _artScale;
    private readonly SKPoint _centre;
    private readonly int _length;
    // Started by the first frame actually drawn, not when the portal is built: a late first frame
    // (the first portal after the app starts) still opens with the Pokémon standing in its slot.
    private readonly Stopwatch _clock = new();
    private readonly SKPaint _dot = new() { IsAntialias = false };
    private bool _done;

    /// <summary>
    /// Readies the portal for <paramref name="host"/> before any transfer: the pad and star frames
    /// load in the background (once), and the page's portal surface is made and laid out now,
    /// hidden, so the first portal after the app starts paints in the same frame as the emptied grid.
    /// </summary>
    public static void Warm(Grid host)
    {
        if (!MotionSetting.Decorative) return;
        _art ??= Task.Run(LoadArt);
        if (host.Handler is not null) Surfaces.GetValue(host, Attach);
    }

    /// <summary>Ends the portal on screen, if any (the page is leaving).</summary>
    public static void Stop() => _current?.Finish();

    /// <summary>
    /// Plays the portal over <paramref name="source"/> (a storage grid painted by
    /// <see cref="BoxGridRenderer.PaintScene"/>) for <paramref name="members"/>, read before the
    /// transfer emptied their slots. <paramref name="liftedCell"/> is the cursor's cell, whose
    /// Pokémon stands lifted. Call only once the transfer has succeeded.
    /// </summary>
    public static void Play(Grid host, SKCanvasView source, IReadOnlyList<PortalSource> members, ISpriteService sprites,
        bool bdspStyle = false, int liftedCell = -1)
    {
        if (!MotionSetting.Decorative || members.Count == 0 || source.Width <= 0 || source.CanvasSize.Width <= 0) return;
        // The art as the grid shows it right now, before the slots repaint empty.
        var shots = new List<(PortalMon Mon, int? Cell)>();
        foreach (var member in members.Take(PortalPaint.MaxMembers))
        {
            if (member.Slot.Species is null) continue;
            using var icon = BoxGridRenderer.Snapshot(member.Slot, sprites, bdspStyle);
            if (icon is not null) shots.Add((new PortalMon(icon), member.Cell));
        }
        if (shots.Count == 0) return;
        _ = StartAsync(host, source, shots, liftedCell);
    }

    private static async Task StartAsync(Grid host, SKCanvasView source, List<(PortalMon Mon, int? Cell)> shots, int liftedCell)
    {
        _art ??= Task.Run(LoadArt);
        // The grid already shows the slots empty: a portal that could only start later would bring
        // the Pokémon back on screen after it had gone. Without its art ready now, none plays (the
        // art is there for the next one).
        var art = _art.IsCompletedSuccessfully ? await _art : null;
        if (art is not { } loaded || source.Handler is null || host.Handler is null)
        {
            foreach (var (mon, _) in shots) mon.Dispose();
            source.InvalidateSurface();
            return;
        }
        _current?.Finish();
        _current = new PortalOverlay(host, source, shots, liftedCell, loaded.Pad, loaded.Star);
    }

    private static (SKImage Pad, SKImage Star)? LoadArt()
    {
        try
        {
            static SKImage Load(string name)
            {
                using var stream = FileSystem.OpenAppPackageFileAsync($"ui/portal/{name}").GetAwaiter().GetResult();
                using var bitmap = SKBitmap.Decode(stream) ?? throw new InvalidDataException(name);
                return SKImage.FromBitmap(bitmap);
            }
            var pad = Load("pad.png");
            var star = Load("star.png");
            if (pad.Width == PortalPaint.PadWidth * PortalPaint.PadFrames && pad.Height == PortalPaint.PadHeight * 2 && star.Width == PortalPaint.StarSize * PortalPaint.StarFrames)
                return (pad, star);
            AppLog.Warn("portal", "The portal PNGs do not match PortalPaint's frame sizes; transfers play without it.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            AppLog.Warn("portal", $"The portal art could not be read: {error.Message}");
        }
        return null;
    }

    private PortalOverlay(Grid host, SKCanvasView source, List<(PortalMon Mon, int? Cell)> shots, int liftedCell, SKImage pad, SKImage star)
    {
        _pad = pad;
        _star = star;
        _source = source;
        _mons = [.. shots.Select(s => s.Mon)];

        // The grid's own geometry: its cells, and the icons' scale (one art pixel).
        var pixels = source.CanvasSize;
        var density = pixels.Width / (float)source.Width;
        var layout = BoxGridRenderer.StorageMetrics(pixels);
        var offset = OffsetIn(source, host);
        _artScale = StoragePaint.IconScale * layout.Unit / density;
        SKPoint IconTopLeft(int cell)
        {
            var lift = cell == liftedCell ? StoragePaint.CursorLift * layout.Unit : 0f;
            var rect = StoragePaint.IconRect(layout.Cell(cell), layout.Unit, lift);
            return new SKPoint(offset.X + rect.Left / density, offset.Y + rect.Top / density);
        }
        var seen = shots.Where(s => s.Cell is >= 0 and < BoxGridRenderer.Columns * BoxGridRenderer.Rows).Select(s => s.Cell!.Value).ToArray();
        // Art pixels line up with the first visible Pokémon's, so it does not jump when the portal takes it.
        _origin = seen.Length > 0 ? IconTopLeft(seen[0])
            : new SKPoint(offset.X + (float)source.Width / 2 - PortalMon.Width / 2f * _artScale,
                offset.Y + (float)source.Height / 2 - PortalMon.Height / 2f * _artScale);
        SKPoint Art(SKPoint dip) => new((dip.X - _origin.X) / _artScale, (dip.Y - _origin.Y) / _artScale);

        // The pad's middle sits where a Pokémon's feet stand.
        var feet = new SKPoint(PortalMon.Width / 2f, PortalPaint.Feet);
        if (seen.Length == 0) _centre = feet;
        else if (seen.Length == 1 && shots.Count == 1) _centre = Art(IconTopLeft(seen[0])) + feet;
        else
        {
            // Under the group's middle, the whole pad inside the grid.
            var points = seen.Select(cell => Art(IconTopLeft(cell))).ToArray();
            var padHalf = PortalPaint.PadWidth / 2f;
            var lowX = Art(new SKPoint(offset.X + layout.Area.Left / density, 0)).X + padHalf;
            var highX = Art(new SKPoint(offset.X + layout.Area.Right / density, 0)).X - padHalf;
            var lowY = Art(new SKPoint(0, offset.Y + layout.Area.Top / density)).Y + PortalMon.Height;
            var highY = Art(new SKPoint(0, offset.Y + layout.Area.Bottom / density)).Y - PortalPaint.PadHeight / 2f;
            _centre = new SKPoint(
                Math.Clamp(points.Average(p => p.X) + feet.X, lowX, Math.Max(lowX, highX)),
                Math.Clamp(points.Average(p => p.Y) + feet.Y, lowY, Math.Max(lowY, highY)));
        }
        // Pokémon from other boxes (or the party) hop in from the top of the grid's middle;
        // with none shown, they stand on the pad from the start.
        var elsewhere = seen.Length == 0
            ? new SKPoint(_centre.X - feet.X, _centre.Y - feet.Y)
            : new SKPoint(Art(new SKPoint(offset.X + (float)source.Width / 2, 0)).X - feet.X, Art(new SKPoint(0, offset.Y)).Y);
        _members = [.. shots.Select(s => new PortalMember(s.Mon,
            s.Cell is >= 0 and < BoxGridRenderer.Columns * BoxGridRenderer.Rows ? Art(IconTopLeft(s.Cell.Value)) : elsewhere))];
        _length = PortalPaint.Length(_members.Length);

        (_canvas, _frame) = Surfaces.GetValue(host, Attach);
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;
        _canvas.InputTransparent = false;
        _canvas.IsVisible = true;
        _canvas.InvalidateSurface();
        // Every button skips it, and none reaches the page while it plays.
        _buttons = new PadOverlay(Finish, Finish, _ => { Finish(); return true; });
        // If the screen stops painting (the page left), the portal still gives the buttons back.
        host.Dispatcher.DispatchDelayed(TimeSpan.FromSeconds(_length / (double)PortalPaint.FramesPerSecond + 1.5), Finish);
    }

    // One canvas per page, made the first time a portal plays there, then hidden and shown
    // again: no view is added to or taken off the page on each transfer.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Grid, Surface> Surfaces = new();

    private sealed record Surface(SKCanvasView Canvas, FrameInvalidator Frame);

    private static Surface Attach(Grid host)
    {
        var canvas = new SKCanvasView { EnableTouchEvents = true, ZIndex = 60, IsVisible = false, InputTransparent = true };
        host.Add(canvas);
        Grid.SetRowSpan(canvas, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(canvas, Math.Max(1, host.ColumnDefinitions.Count));
        return new Surface(canvas, new FrameInvalidator(canvas));
    }

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType == SKTouchAction.Pressed) Finish();
    }

    private static SKPoint OffsetIn(VisualElement view, Element host)
    {
        float x = 0, y = 0;
        for (Element? at = view; at is not null && at != host; at = at.Parent)
            if (at is VisualElement visual)
            {
                x += (float)(visual.X + visual.TranslationX);
                y += (float)(visual.Y + visual.TranslationY);
            }
        return new SKPoint(x, y);
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var probe = PaintClock.Start();
        PaintFrame(args);
        PaintClock.Stop("Portal", probe);
    }

    private void PaintFrame(SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_done) return;
        if (!_clock.IsRunning) _clock.Start();
        var tick = (int)(_clock.Elapsed.TotalSeconds * PortalPaint.FramesPerSecond);
        if (tick >= _length)
        {
            // Queued, never run inside this paint: hiding the view while SkiaSharp is still
            // drawing it frees its bitmap under it and crashes (MainThread.BeginInvokeOnMainThread
            // would run it right here, being on the main thread already).
            _canvas.Dispatcher.Dispatch(Finish);
            return;
        }
        var density = args.Info.Width / (float)Math.Max(1, _canvas.Width);
        canvas.Save();
        canvas.Translate(_origin.X * density, _origin.Y * density);
        canvas.Scale(_artScale * density);
        PortalPaint.Draw(canvas, _pad, _star, _members, _centre, tick, _dot);
        canvas.Restore();
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
        foreach (var mon in _mons) mon.Dispose();
        _dot.Dispose();
        // The grid is drawn again from the save as it is now, in the frame the portal goes away,
        // so what stood in the left slots can never show there once the portal is gone.
        _source.InvalidateSurface();
    }
}
