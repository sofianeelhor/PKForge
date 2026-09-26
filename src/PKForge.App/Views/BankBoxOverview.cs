using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// Every Bank box at a glance: a grid of live box cards (wallpaper, name, every Pokémon).
/// Pick a box up and carry it (Y, or a long press), and the others slide aside to show where
/// it lands; switch to swap to trade two boxes instead. Rename, wallpaper, insert and delete
/// from the box menu (X). Every change is one Bank write through
/// <see cref="BankPage.ApplyBoxRemap"/>, so names, wallpapers, marks and the living dex region
/// always follow their box.
/// </summary>
public sealed class BankBoxOverview : IPadPagingHandler
{
    private const int Slots = 30;
    private const float CardAspect = 5f / 6f; // a box is 6 × 5 slots
    private const float HintHeight = 40f;

    private enum Carry { None, Move, Swap }

    private readonly TaskCompletionSource<int?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly BankPage _page;
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly IBankService _bank;
    private readonly ISpriteService _sprites;
    private readonly GamepadRouter? _router;
    private readonly SKCanvasView _canvas;
    private readonly FrameInvalidator _frame;
    private readonly FramePacer _pacer;
    private readonly SKTypeface _face = BoxBrowserPage.PixelTypeface();

    // Per box (by current index): where its card is drawn, in grid cells, and how "present" it is.
    private readonly List<(Spring X, Spring Y, Spring Appear)> _cards = [];
    private readonly Spring _scroll = new(0);
    private readonly Spring _lift = new(0);
    private readonly Spring _cursorX = new(0);
    private readonly Spring _cursorY = new(0);

    private BankEntry?[][] _boxes = [];
    private int _cursor;
    private int? _held;
    private Carry _carry;
    private bool _busy;
    private int _columns = 6;
    private int _visibleRows = 2;
    private float _density = 1;

    // Touch: a long press lifts the card under the finger, which then follows it.
    private SKPoint? _pressAt;
    private SKPoint _finger;
    private bool _dragging;
    private bool _scrolling;
    private float _scrollStart;
    private IDispatcherTimer? _longPress;

    public static Task<int?> ShowAsync(BankPage page, Grid host, IBankService bank, ISpriteService sprites, int currentBox) =>
        new BankBoxOverview(page, host, bank, sprites, currentBox)._result.Task;

    private BankBoxOverview(BankPage page, Grid host, IBankService bank, ISpriteService sprites, int currentBox)
    {
        _page = page;
        _host = host;
        _bank = bank;
        _sprites = sprites;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _canvas = new SKCanvasView { EnableTouchEvents = true };
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;
        _frame = new FrameInvalidator(_canvas);
        _pacer = new FramePacer(_frame);

        var window = new Border
        {
            Content = _canvas,
            Padding = 0,
            StrokeThickness = 0,
            BackgroundColor = Colors.Transparent,
            Margin = new Thickness(10, 8),
        };
        _overlay = Kit.AttachOverlay(host, window);

        Reload();
        _cursor = Math.Clamp(currentBox, 0, _boxes.Length - 1);
        for (var box = 0; box < _cards.Count; box++) _cards[box].Appear.Snap(1);
        SnapLayout();
        _router?.Push(this);
    }

    // ── Model ──────────────────────────────────────────────────────────────

    /// <summary>Re-reads the Bank; card springs follow <paramref name="remap"/> so moved boxes glide from where they were.</summary>
    private void Reload(BankBoxRemap? remap = null)
    {
        var count = _bank.BoxCount;
        var boxes = new BankEntry?[count][];
        for (var box = 0; box < count; box++) boxes[box] = new BankEntry?[Slots];
        foreach (var entry in _bank.GetAll())
        {
            if ((uint)entry.Box < (uint)count && (uint)entry.Slot < Slots) boxes[entry.Box][entry.Slot] = entry;
        }
        _boxes = boxes;
        ClearCardCache();

        var cards = new (Spring, Spring, Spring)?[count];
        for (var old = 0; old < _cards.Count; old++)
        {
            var now = remap is null ? old : remap.Map(old);
            if (now is { } index && index < count) cards[index] = _cards[old];
        }
        _cards.Clear();
        for (var box = 0; box < count; box++)
        {
            if (cards[box] is { } kept) { _cards.Add(kept); continue; }
            var (column, row) = Cell(box);
            _cards.Add((new Spring(column), new Spring(row), new Spring(0) { Target = 1 }));
        }
        _cursor = Math.Clamp(_cursor, 0, count - 1);
    }

    private int Occupied(int box) => _boxes[box].Count(e => e is not null);

    private (int Column, int Row) Cell(int position) => (position % _columns, position / _columns);

    /// <summary>
    /// The position each box's card heads for. Carrying in move mode, the held box sits at the
    /// cursor and the rest close ranks around it: exactly the order a drop would write.
    /// </summary>
    private int PositionOf(int box)
    {
        if (_held is not { } held || _carry != Carry.Move) return box;
        if (box == held) return _cursor;
        var withoutHeld = box > held ? box - 1 : box;
        return withoutHeld >= _cursor ? withoutHeld + 1 : withoutHeld;
    }

    private void Retarget()
    {
        for (var box = 0; box < _cards.Count; box++)
        {
            var position = _carry == Carry.Swap && _held is { } held
                ? box == held ? _cursor : box == _cursor ? held : box
                : PositionOf(box);
            var (column, row) = Cell(position);
            _cards[box].X.Target = column;
            _cards[box].Y.Target = row;
        }
        var (cursorColumn, cursorRow) = Cell(_cursor);
        _cursorX.Target = cursorColumn;
        _cursorY.Target = cursorRow;
        _lift.Target = _held is null ? 0 : 1;

        // Keep the cursor's row on screen with a row of context when there is one.
        var top = _scroll.Target;
        if (cursorRow < top + 0.2f) top = Math.Max(0, cursorRow - 0.35f);
        else if (cursorRow > top + _visibleRows - 1.2f) top = cursorRow - _visibleRows + 1.35f;
        var lastRow = (_boxes.Length - 1) / _columns;
        _scroll.Target = Math.Clamp(top, 0, Math.Max(0, lastRow - _visibleRows + 1.2f));
        _pacer.Kick();
    }

    private void SnapLayout()
    {
        Retarget();
        foreach (var (x, y, _) in _cards) { x.Snap(x.Target); y.Snap(y.Target); }
        _cursorX.Snap(_cursorX.Target);
        _cursorY.Snap(_cursorY.Target);
        _scroll.Snap(_scroll.Target);
        _frame.Request();
    }

    private bool Step(float dt)
    {
        var moving = false;
        foreach (var (x, y, appear) in _cards)
        {
            x.Step(dt, 340f, 36.9f);
            y.Step(dt, 340f, 36.9f);
            appear.Step(dt, 300f, 26f); // a touch of bounce as a new box pops in
            moving |= !x.Settled || !y.Settled || !appear.Settled;
        }
        _cursorX.Step(dt, 900f, 60f);
        _cursorY.Step(dt, 900f, 60f);
        _scroll.Step(dt, 260f, 32.2f);
        _lift.Step(dt, 520f, 34f); // the lifted box overshoots a hair, like a picked-up card
        moving |= !_cursorX.Settled || !_cursorY.Settled || !_scroll.Settled || !_lift.Settled;
        return moving || _held is not null; // a held box keeps bobbing
    }

    // ── Input ──────────────────────────────────────────────────────────────

    public bool OnPadButton(PadButton button)
    {
        if (_busy) return true;
        switch (button)
        {
            case PadButton.Left: MoveCursor(-1); return true;
            case PadButton.Right: MoveCursor(1); return true;
            case PadButton.Up: MoveCursor(-_columns); return true;
            case PadButton.Down: MoveCursor(_columns); return true;
            case PadButton.L: MoveCursor(-_columns * Math.Max(1, _visibleRows - 1)); return true;
            case PadButton.R: MoveCursor(_columns * Math.Max(1, _visibleRows - 1)); return true;
            case PadButton.A:
                if (_held is not null) Drop();
                else Close(_cursor);
                return true;
            case PadButton.Y:
                if (_held is null) PickUp(_cursor);
                else Drop();
                return true;
            case PadButton.X:
                if (_held is not null) ToggleCarryMode();
                else _ = BoxMenuAsync();
                return true;
            case PadButton.Start:
                if (_held is null) _ = AddBoxAsync(_boxes.Length);
                return true;
            case PadButton.B:
                if (_held is not null) CancelCarry();
                else Close(null);
                return true;
            default:
                return true;
        }
    }

    private void MoveCursor(int delta)
    {
        var target = _cursor + delta;
        if (Math.Abs(delta) == 1)
            target = (target + _boxes.Length) % _boxes.Length; // left/right wrap through the whole bank
        _cursor = Math.Clamp(target, 0, _boxes.Length - 1);
        Retarget();
    }

    private void PickUp(int box)
    {
        _held = box;
        _carry = Carry.Move;
        Retarget();
    }

    private void ToggleCarryMode()
    {
        _carry = _carry == Carry.Move ? Carry.Swap : Carry.Move;
        Retarget();
    }

    private void CancelCarry()
    {
        if (_held is { } held) _cursor = held;
        _held = null;
        _carry = Carry.None;
        _dragging = false;
        Retarget();
    }

    private void Drop()
    {
        if (_held is not { } held) return;
        var remap = _carry == Carry.Swap
            ? BankBoxRemap.Swap(_boxes.Length, held, _cursor)
            : BankBoxRemap.Move(_boxes.Length, held, _cursor);
        _held = null;
        _carry = Carry.None;
        _dragging = false;
        if (_page.ApplyBoxRemap(remap)) Reload(remap);
        Retarget();
    }

    private async Task BoxMenuAsync()
    {
        var box = _cursor;
        var count = Occupied(box);
        var options = new List<PadOption>
        {
            new("Open this box", IconPath: "box"),
            new("Pick it up to move it", IconPath: "move"),
            new("Rename…", IconPath: "rename"),
            new("Wallpaper…", IconPath: "fashion"),
            new("Insert a new box before it", IconPath: "create"),
            new("Insert a new box after it", IconPath: "create"),
        };
        if (_boxes.Length > 1) options.Add(new PadOption("Delete this box…", IconPath: "delete"));

        _busy = true;
        string? choice;
        try
        {
            choice = await PadMenu.ShowAsync(_host, BankBoxDecor.Label(box),
                count == 0 ? "Empty box." : $"{count} / {Slots} Pokémon.", [.. options]);
        }
        finally { _busy = false; }

        switch (choice)
        {
            case "Open this box":
                Close(box);
                return;
            case "Pick it up to move it":
                PickUp(box);
                return;
            case "Rename…":
                await RunAsync(() => _page.RenameBoxAsync(box));
                return;
            case "Wallpaper…":
                await RunAsync(() => _page.PickWallpaperAsync(box));
                return;
            case "Insert a new box before it":
                await AddBoxAsync(box);
                return;
            case "Insert a new box after it":
                await AddBoxAsync(box + 1);
                _cursor = box + 1;
                Retarget();
                return;
            case "Delete this box…":
                // One reload, mapped from the layout before the delete, so the rest close ranks.
                await RunAsync(() => _page.DeleteBoxAsync(box), BankBoxRemap.Remove(_boxes.Length, box));
                Retarget();
                return;
        }
    }

    private async Task<bool> RunAsync(Func<Task<bool>> action, BankBoxRemap? remap = null)
    {
        _busy = true;
        try
        {
            var changed = await action();
            if (changed) Reload(remap);
            _frame.Request();
            return changed;
        }
        finally { _busy = false; }
    }

    private Task AddBoxAsync(int at)
    {
        var remap = BankBoxRemap.Insert(_boxes.Length, at);
        if (_page.ApplyBoxRemap(remap))
        {
            Reload(remap);
            _cursor = at;
        }
        Retarget();
        return Task.CompletedTask;
    }

    private void Close(int? box)
    {
        _longPress?.Stop();
        ClearCardCache();
        _router?.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(box);
    }

    // ── Touch ──────────────────────────────────────────────────────────────

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (_busy) return;
        var point = args.Location;
        switch (args.ActionType)
        {
            case SKTouchAction.Pressed:
                _pressAt = point;
                _finger = point;
                _scrolling = false;
                _scrollStart = _scroll.Target;
                StartLongPress();
                break;
            case SKTouchAction.Moved when _pressAt is { } start:
                _finger = point;
                if (_dragging)
                {
                    if (BoxAt(point) is { } over && over != _cursor) { _cursor = over; Retarget(); }
                    _frame.Request();
                }
                else if (_scrolling || Distance(start, point) > 18 * _density)
                {
                    _longPress?.Stop();
                    _scrolling = true;
                    _scroll.Target = _scrollStart - (point.Y - start.Y) / RowHeight();
                    _scroll.Snap(Math.Clamp(_scroll.Target, 0, Math.Max(0, (_boxes.Length - 1) / _columns - _visibleRows + 1.2f)));
                    _frame.Request();
                }
                break;
            case SKTouchAction.Released:
            case SKTouchAction.Cancelled:
                _longPress?.Stop();
                if (_dragging) Drop();
                else if (!_scrolling && _pressAt is not null && args.ActionType == SKTouchAction.Released && BoxAt(point) is { } box)
                {
                    if (_held is not null) { _cursor = box; Drop(); }
                    else if (box == _cursor) Close(box); // a second tap opens it
                    else { _cursor = box; Retarget(); }
                }
                _pressAt = null;
                _scrolling = false;
                break;
        }
    }

    private void StartLongPress()
    {
        _longPress ??= Application.Current?.Dispatcher.CreateTimer();
        if (_longPress is null) return;
        _longPress.Stop();
        _longPress.Interval = TimeSpan.FromMilliseconds(380);
        _longPress.IsRepeating = false;
        _longPress.Tick -= OnLongPress;
        _longPress.Tick += OnLongPress;
        _longPress.Start();
    }

    private void OnLongPress(object? sender, EventArgs e)
    {
        if (_pressAt is not { } start || _scrolling || _held is not null || BoxAt(start) is not { } box) return;
        _cursor = box;
        _dragging = true;
        PickUp(box);
    }

    private static float Distance(SKPoint a, SKPoint b) => MathF.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ── Layout & paint ─────────────────────────────────────────────────────

    private SKRect _grid;
    private float _cardWidth;
    private float _cardHeight;
    private float _gap;

    private float RowHeight() => _cardHeight + _gap;

    private void Measure(SKImageInfo info)
    {
        _density = _canvas.Width > 0 ? info.Width / (float)_canvas.Width : 1;
        var hint = HintHeight * _density;
        var header = 44 * _density;
        _grid = new SKRect(0, header, info.Width, info.Height - hint);
        var columns = info.Width / _density >= 900 ? 6 : info.Width / _density >= 560 ? 4 : 3;
        if (columns != _columns)
        {
            _columns = columns;
            SnapLayout();
        }
        _gap = 12 * _density;
        _cardWidth = (_grid.Width - _gap * (_columns + 1)) / _columns;
        _cardHeight = _cardWidth * CardAspect + 22 * _density;
        _visibleRows = Math.Max(1, (int)(_grid.Height / (_cardHeight + _gap)));
    }

    private SKRect CardRect(float column, float row) =>
        SKRect.Create(_grid.Left + _gap + column * (_cardWidth + _gap),
            _grid.Top + _gap * 0.5f + (row - _scroll.Value) * (_cardHeight + _gap), _cardWidth, _cardHeight);

    private int? BoxAt(SKPoint viewPoint)
    {
        var point = new SKPoint(viewPoint.X * (_canvas.CanvasSize.Width / Math.Max(1f, (float)_canvas.Width)),
            viewPoint.Y * (_canvas.CanvasSize.Height / Math.Max(1f, (float)_canvas.Height)));
        if (!_grid.Contains(point)) return null;
        var column = (int)((point.X - _grid.Left - _gap * 0.5f) / (_cardWidth + _gap));
        var row = (int)MathF.Floor((point.Y - _grid.Top) / (_cardHeight + _gap) + _scroll.Value);
        if (column < 0 || column >= _columns || row < 0) return null;
        var position = row * _columns + column;
        return position < _boxes.Length ? position : null;
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var info = args.Info;
        var moving = Step(_pacer.Advance());
        _redrawsThisFrame = 0;
        _staleCards = false;
        Measure(info);
        canvas.Clear(SKColors.Transparent);
        PksmPaint.Panel(canvas, new SKRect(0, 0, info.Width, info.Height), Pksm.Housing, 10 * _density);

        using var title = new SKFont(_face, 22 * _density);
        using var small = new SKFont(_face, 15 * _density);
        var total = _boxes.Sum(b => b.Count(e => e is not null));
        var heading = _held is { } h
            ? $"{(_carry == Carry.Swap ? "Swapping" : "Moving")} {BankBoxDecor.Label(h)}"
            : $"All boxes · {_boxes.Length} boxes · {total} Pokémon";
        PksmPaint.CenterText(canvas, heading, 16 * _density, 24 * _density, title, SKColors.White, Pksm.LogoVoid);

        canvas.Save();
        canvas.ClipRect(_grid);
        for (var box = 0; box < _boxes.Length; box++)
        {
            if (box == _held) continue;
            var (x, y, appear) = _cards[box];
            var rect = CardRect(x.Value, y.Value);
            if (rect.Bottom < _grid.Top || rect.Top > _grid.Bottom) continue;
            DrawCard(canvas, rect, box, small, appear.Value);
        }

        var cursor = CardRect(_cursorX.Value, _cursorY.Value);
        if (_held is null)
            PksmPaint.Selection(canvas, SKRect.Inflate(cursor, 3 * _density, 3 * _density));
        canvas.Restore();

        if (_held is { } held)
        {
            // The carried card rides above everything: lifted, scaled up, bobbing gently,
            // following the finger while dragged.
            var lift = _lift.Value;
            var bob = MathF.Sin(_pacer.Now * 5.2f) * 3f * _density * lift;
            var target = _dragging
                ? SKRect.Create(_finger.X * _density - _cardWidth / 2, _finger.Y * _density - _cardHeight / 2, _cardWidth, _cardHeight)
                : cursor;
            var grow = 1 + 0.08f * lift;
            var rect = SKRect.Create(target.MidX - target.Width * grow / 2, target.MidY - target.Height * grow / 2 - 14 * _density * lift + bob,
                target.Width * grow, target.Height * grow);
            // A soft drop shadow from two stacked translucent plates: no blur filter to pay for.
            using (var shadow = new SKPaint { Color = SKColors.Black.WithAlpha((byte)(50 * lift)), IsAntialias = true })
            {
                var drop = SKRect.Create(rect.Left + 3 * _density, rect.Top + 14 * _density * lift, rect.Width, rect.Height);
                canvas.DrawRoundRect(SKRect.Inflate(drop, 3 * _density, 3 * _density), 12 * _density, 12 * _density, shadow);
                canvas.DrawRoundRect(drop, 10 * _density, 10 * _density, shadow);
            }
            canvas.Save();
            canvas.RotateDegrees(MathF.Sin(_pacer.Now * 3.1f) * 1.2f * lift, rect.MidX, rect.MidY);
            DrawCard(canvas, rect, held, small, 1);
            using (var rim = new SKPaint { Color = Pksm.ShinyGold, Style = SKPaintStyle.Stroke, StrokeWidth = 3 * _density, IsAntialias = true })
                canvas.DrawRoundRect(rect, 9 * _density, 9 * _density, rim);
            canvas.Restore();
            if (_carry == Carry.Swap && held != _cursor)
                PaintSwapBadge(canvas, CardRect(_cards[_cursor].X.Value, _cards[_cursor].Y.Value), small);
        }

        var hintBar = new SKRect(0, info.Height - HintHeight * _density, info.Width, info.Height);
        IReadOnlyList<(string, string)> prompts = _held is null
            ? [("A", "Open"), ("Y", "Pick up"), ("X", "Box menu"), ("+", "New box"), ("L/R", "Page"), ("B", "Close")]
            : [("A", "Drop here"), ("X", _carry == Carry.Swap ? "Move instead" : "Swap instead"), ("B", "Cancel")];
        PksmPaint.HintBar(canvas, hintBar, prompts, small);
        _pacer.Continue(moving || _staleCards);
    }

    // ── Card cache ─────────────────────────────────────────────────────────
    // A card (wallpaper, name, 30 sprites) is drawn once into an image; each frame only moves
    // images around, which keeps the slide and the lift smooth. A card drawn before all its
    // sprites or its wallpaper arrived is redrawn, a couple per frame, until it is complete.

    private readonly Dictionary<int, (SKImage Image, bool Complete)> _cardImages = [];
    private bool _staleCards;
    private int _redrawsThisFrame;

    private void ClearCardCache()
    {
        foreach (var (image, _) in _cardImages.Values) image.Dispose();
        _cardImages.Clear();
    }

    private void DrawCard(SKCanvas canvas, SKRect rect, int box, SKFont font, float appear)
    {
        if (appear < 0.01f) return;
        var width = (int)MathF.Ceiling(_cardWidth);
        var height = (int)MathF.Ceiling(_cardHeight);
        if (!_cardImages.TryGetValue(box, out var cached) || cached.Image.Width != width || cached.Image.Height != height
            || (!cached.Complete && _redrawsThisFrame < 2))
        {
            _redrawsThisFrame++;
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null) return;
            surface.Canvas.Clear(SKColors.Transparent);
            var complete = PaintCard(surface.Canvas, SKRect.Create(0, 0, _cardWidth, _cardHeight), box, font);
            if (_cardImages.TryGetValue(box, out var old)) old.Image.Dispose();
            cached = (surface.Snapshot(), complete);
            _cardImages[box] = cached;
        }
        _staleCards |= !cached.Complete;
        canvas.Save();
        if (appear < 0.999f) canvas.Scale(appear, appear, rect.MidX, rect.MidY);
        canvas.DrawImage(cached.Image, rect, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        canvas.Restore();
    }

    /// <summary>Draws one box card; false while a sprite or its wallpaper is still loading.</summary>
    private bool PaintCard(SKCanvas canvas, SKRect rect, int box, SKFont font)
    {
        var complete = true;
        var radius = 9 * _density;

        var tone = BankBoxArt.Tone(box);
        var shade = Pksm.WallpaperShade(tone);
        using var clip = new SKRoundRect(rect, radius, radius);
        canvas.Save();
        canvas.ClipRoundRect(clip, antialias: true);
        BankBoxArt.Paint(canvas, rect, box, _frame.Request);

        var band = new SKRect(rect.Left, rect.Top, rect.Right, rect.Top + 22 * _density);
        using (var bandPaint = new SKPaint { Color = SKColors.Black.WithAlpha(0x70) })
            canvas.DrawRect(band, bandPaint);
        var name = BankBoxDecor.Label(box);
        PksmPaint.CenterText(canvas, Fit(name, font, rect.Width - 58 * _density), rect.Left + 8 * _density, band.MidY, font, SKColors.White, SKColors.Black);
        var count = Occupied(box);
        PksmPaint.CenterText(canvas, count == Slots ? "FULL" : $"{count}/{Slots}", rect.Right - 8 * _density, band.MidY, font,
            count == Slots ? Pksm.ShinyGold : SKColors.White, SKColors.Black, SKTextAlign.Right);

        // The 30 slots, every Pokémon drawn small: the box reads like the real one.
        var area = new SKRect(rect.Left + 6 * _density, band.Bottom + 4 * _density, rect.Right - 6 * _density, rect.Bottom - 6 * _density);
        var cellW = area.Width / 6f;
        var cellH = area.Height / 5f;
        using var dot = new SKPaint { Color = SKColors.White.WithAlpha(0x2E), IsAntialias = true };
        var entries = _boxes[box];
        for (var slot = 0; slot < Slots; slot++)
        {
            var cx = area.Left + (slot % 6 + 0.5f) * cellW;
            var cy = area.Top + (slot / 6 + 0.5f) * cellH;
            if (entries[slot] is not { } entry)
            {
                canvas.DrawCircle(cx, cy, Math.Min(cellW, cellH) * 0.1f, dot);
                continue;
            }
            var sprite = _sprites.GetSprite(entry.Info.Look);
            if (sprite is null)
            {
                complete = false;
                _sprites.Warm(entry.Info.Look, _frame.Request);
                continue;
            }
            var size = Math.Min(cellW, cellH) * 1.08f;
            var scale = size / Math.Max(sprite.Width, sprite.Height);
            var w = sprite.Width * scale;
            var hgt = sprite.Height * scale;
            using var image = SKImage.FromBitmap(sprite);
            canvas.DrawImage(image, new SKRect(cx - w / 2, cy - hgt / 2, cx + w / 2, cy + hgt / 2), BoxGridRenderer.SpriteSampling);
        }
        canvas.Restore();

        using (var border = new SKPaint
        {
            Color = shade,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2 * _density,
            IsAntialias = true,
        })
        {
            canvas.DrawRoundRect(clip, border);
        }
        if (LivingDexAutopilot.BankStartBox == box)
            PksmPaint.CenterText(canvas, "Living dex ▸", rect.Left + 8 * _density, rect.Bottom - 10 * _density, font, Pksm.ShinyGold, SKColors.Black);

        return complete && BankBoxArt.IsReady(box);
    }

    private void PaintSwapBadge(SKCanvas canvas, SKRect rect, SKFont font)
    {
        using var veil = new SKPaint { Color = SKColors.Black.WithAlpha(0x55), IsAntialias = true };
        canvas.DrawRoundRect(rect, 9 * _density, 9 * _density, veil);
        PksmPaint.CenterText(canvas, "⇄ swap", rect.MidX, rect.MidY, font, SKColors.White, SKColors.Black, SKTextAlign.Center);
    }

    private static string Fit(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width) return text;
        while (text.Length > 1 && font.MeasureText(text + "…") > width) text = text[..^1];
        return text + "…";
    }
}
