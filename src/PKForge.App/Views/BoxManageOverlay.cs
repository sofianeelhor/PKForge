using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The box-management MODE: the box grid becomes tiles (one per box, wallpaper-colored,
/// with counts). Cursor moves between boxes; A grabs a box (breathing, ghost at origin);
/// A on another box swaps their order live-previewed; X marks boxes for bulk actions;
/// Start opens the action menu (swap / delete marked / clear marked). B exits.
/// All writes go through the page's normal safe pipeline via the provided callbacks.
/// </summary>
public sealed class BoxManageOverlay : IPadHandler
{
    private readonly Grid _host;
    private readonly SKCanvasView _canvas;
    private readonly GamepadRouter? _router;
    private readonly Grid _overlay = null!;
    private readonly int _boxCount;
    private readonly int _slotsPerBox;
    private readonly Func<int, int> _countFor;
    private readonly Func<int, string> _nameFor;
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _cursor;
    private int? _carryBox;
    private readonly HashSet<int> _marked = [];
    private bool _live;

    /// <summary>Wire-up for the host page: perform a swap of two boxes (safe write), report status.</summary>
    public required Func<int, int, Task<bool>> SwapBoxesAsync { get; init; }
    /// <summary>Bulk clear: empty every marked box (safe write).</summary>
    public required Func<IReadOnlyList<int>, Task<bool>> ClearBoxesAsync { get; init; }
    /// <summary>Bulk delete: rescue mons out of every marked box then empty them (safe write).</summary>
    public required Func<IReadOnlyList<int>, Task<bool>> DeleteBoxesAsync { get; init; }
    public Action<string>? Status { get; init; }

    private int Columns => Math.Min(6, Math.Max(3, (_boxCount + 2) / ((_boxCount + 5) / 6)));

    private BoxManageOverlay(Grid host, int boxCount, int slotsPerBox, Func<int, int> countFor, Func<int, string> nameFor, int startBox)
    {
        _host = host;
        _boxCount = boxCount;
        _slotsPerBox = slotsPerBox;
        _countFor = countFor;
        _nameFor = nameFor;
        _cursor = Math.Clamp(startBox, 0, boxCount - 1);
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

        _canvas = new SKCanvasView { EnableTouchEvents = true };
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;

        var hints = Kit.HintBar(
            ("A", "Grab / Swap", null),
            ("X", "Mark", null),
            ("START", "Actions", null),
            ("B", "Done", null));

        var content = new Grid
        {
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto)],
            Children = { Kit.HeaderBar("MANAGE BOXES"), _canvas, hints },
        };
        Grid.SetRow(_canvas, 1);
        Grid.SetRow(hints, 2);

        var window = new Border
        {
            BackgroundColor = UiTokens.Housing,
            Stroke = UiTokens.ShellEdge,
            StrokeThickness = 2,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 6 },
            Padding = 8,
            MaximumWidthRequest = 900,
            MaximumHeightRequest = host.Height > 0 ? host.Height - 16 : 344,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = content,
        };
        _overlay = new Grid { Children = { new BoxView { Color = UiTokens.Scrim }, window } };
        host.Add(_overlay);
        Grid.SetRowSpan(_overlay, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(_overlay, Math.Max(1, host.ColumnDefinitions.Count));
        Kit.AnimateIn(window);
        _router?.Push(this);
    }

    public static Task ShowAsync(Grid host, int boxCount, int slotsPerBox, Func<int, int> countFor,
        Func<int, string> nameFor, int startBox, Func<int, int, Task<bool>> swap,
        Func<IReadOnlyList<int>, Task<bool>> clear, Func<IReadOnlyList<int>, Task<bool>> delete) =>
        new BoxManageOverlay(host, boxCount, slotsPerBox, countFor, nameFor, startBox)
        {
            SwapBoxesAsync = swap,
            ClearBoxesAsync = clear,
            DeleteBoxesAsync = delete,
        }.RunAsync();

    private Task RunAsync()
    {
        _live = true;
        _canvas.InvalidateSurface();
        Tick();
        return _closed.Task;
    }

    private void Close()
    {
        if (!_live) return;
        _live = false;
        _router?.Remove(this);
        _host.Remove(_overlay);
        _closed.TrySetResult();
    }

    // ── Layout ──

    private (SKRect Rect, int Cols, int Rows) TileAt(SKImageInfo info, int index)
    {
        var cols = Columns;
        var rows = (_boxCount + cols - 1) / cols;
        var pad = 12f;
        var gap = 10f;
        var w = (info.Width - pad * 2 - gap * (cols - 1)) / cols;
        var h = (info.Height - pad * 2 - gap * (rows - 1)) / rows;
        var col = index % cols;
        var row = index / cols;
        var x = pad + col * (w + gap);
        var y = pad + row * (h + gap);
        return (new SKRect(x, y, x + w, y + h), cols, rows);
    }

    // ── Paint ──

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        if (!_live) return;
        var c = args.Surface.Canvas;
        var info = args.Info;
        c.Clear(Pksm.Housing);
        using var grid = new SKPaint { Color = Pksm.HousingLine, StrokeWidth = 1 };
        for (float x = 0; x < info.Width; x += 26) c.DrawLine(x, 0, x, info.Height, grid);
        for (float y = 0; y < info.Height; y += 26) c.DrawLine(0, y, info.Width, y, grid);

        var cols = Columns;
        var breath = 1f + 0.02f * (0.5f + 0.5f * MathF.Sin(Environment.TickCount64 / 220f));

        for (var i = 0; i < _boxCount; i++)
        {
            var (rect, _, _) = TileAt(info, i);
            var wallpaper = Pksm.BoxWallpapers[i % Pksm.BoxWallpapers.Length];
            var isCarried = _carryBox == i;
            var lifted = isCarried ? SKRect.Inflate(rect, 2, -3 * (breath - 1f) * 10) : rect;

            // tile
            using (var fill = new SKPaint { Color = wallpaper, IsAntialias = true })
                c.DrawRoundRect(lifted, 6, 6, fill);
            using (var edge = new SKPaint { Color = Pksm.WallpaperShade(wallpaper), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
                c.DrawRoundRect(lifted, 6, 6, edge);

            // box number
            using var numFont = new SKFont(PixelFont.Face, Math.Max(14f, lifted.Height * 0.24f)) { Edging = SKFontEdging.Antialias, Embolden = true };
            using (var ink = new SKPaint { Color = Pksm.InkOver(wallpaper), IsAntialias = true })
                c.DrawText($"{i + 1:00}", lifted.Left + 10, lifted.Top + lifted.Height * 0.34f, SKTextAlign.Left, numFont, ink);

            // name + count
            using var small = new SKFont(PixelFont.Face, Math.Max(11f, lifted.Height * 0.16f)) { Edging = SKFontEdging.Antialias, Embolden = true };
            using var soft = new SKPaint { Color = Pksm.InkOver(wallpaper).WithAlpha(0xCC), IsAntialias = true };
            var name = _nameFor(i);
            c.DrawText(name.Length > 14 ? name[..14] : name, lifted.Left + 10, lifted.Top + lifted.Height * 0.62f, SKTextAlign.Left, small, soft);
            var count = _countFor(i);
            c.DrawText($"{count}/{_slotsPerBox}", lifted.Right - 10, lifted.Top + lifted.Height * 0.62f, SKTextAlign.Right, small, soft);

            // tiny sprite dots: a mini preview of the box's fill
            var dotR = lifted.Height * 0.05f;
            var shown = Math.Min(count, 12);
            using var dot = new SKPaint { Color = Pksm.Paper.WithAlpha(0xB4), IsAntialias = true };
            for (var d = 0; d < shown; d++)
            {
                var dx = lifted.Left + 10 + (d % 6) * dotR * 2.6f;
                var dy = lifted.Bottom - 12 - (d / 6) * dotR * 2.8f;
                c.DrawCircle(dx, dy, dotR, dot);
            }

            // mark badge
            if (_marked.Contains(i))
                PksmPaint.Crosshair(c, SKRect.Inflate(lifted, 2, 2), 10, 3);

            // carried ghost at origin
            if (isCarried)
            {
                var ghost = new SKPaint { Color = Pksm.SelectBorder.WithAlpha(0x90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
                ghost.PathEffect = SKPathEffect.CreateDash([6, 5], 0);
                c.DrawRoundRect(rect, 6, 6, ghost);
            }

            // swap preview: carried tile's content drawn in aimed tile
            if (_carryBox is { } src && src != i && i == _cursor)
            {
                using var veil = new SKPaint { Color = new SKColor(0x14, 0x1D, 0x3E, 0xB4), IsAntialias = true };
                c.DrawRoundRect(SKRect.Inflate(rect, -2, -2), 5, 5, veil);
                PksmPaint.Selection(c, rect);
            }

            // cursor
            if (i == _cursor && !isCarried)
                PksmPaint.Selection(c, rect);
        }
    }

    private void Tick()
    {
        if (!_live) return;
        _canvas.InvalidateSurface();
        _ = Task.Delay(60).ContinueWith(_ =>
        {
            if (_live) MainThread.BeginInvokeOnMainThread(Tick);
        });
    }

    // ── Input ──

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
        if (args.ActionType != SKTouchAction.Released) return;
        args.Handled = true;
        var info = new SKImageInfo((int)_canvas.CanvasSize.Width, (int)_canvas.CanvasSize.Height);
        for (var i = 0; i < _boxCount; i++)
        {
            var (rect, _, _) = TileAt(info, i);
            if (!rect.Contains(args.Location.X, args.Location.Y)) continue;
            if (i == _cursor) { Confirm(); return; }
            _cursor = i;
            _canvas.InvalidateSurface();
            return;
        }
    }

    public bool OnPadButton(PadButton button)
    {
        var cols = Columns;
        switch (button)
        {
            case PadButton.Left: _cursor = (_cursor + _boxCount - 1) % _boxCount; break;
            case PadButton.Right: _cursor = (_cursor + 1) % _boxCount; break;
            case PadButton.Up: _cursor = (_cursor - cols + _boxCount) % _boxCount; break;
            case PadButton.Down: _cursor = (_cursor + cols) % _boxCount; break;
            case PadButton.A: Confirm(); break;
            case PadButton.X: ToggleMark(); break;
            case PadButton.Start: _ = ShowActionsAsync(); break;
            case PadButton.B:
                if (_carryBox is not null) { _carryBox = null; Status?.Invoke("Box released."); }
                else Close();
                break;
            default: return true;
        }
        _canvas.InvalidateSurface();
        return true;
    }

    private void Confirm()
    {
        if (_carryBox is { } source)
        {
            if (source == _cursor) { _carryBox = null; Status?.Invoke("Box released."); return; }
            var from = source;
            _carryBox = null;
            _ = Task.Run(async () =>
            {
                var ok = await SwapBoxesAsync(from, _cursor);
                await MainThread.InvokeOnMainThreadAsync(() =>
                {
                    Status?.Invoke(ok ? $"Boxes {from + 1:00} and {_cursor + 1:00} swapped." : "Swap failed.");
                    _canvas.InvalidateSurface();
                });
            });
        }
        else
        {
            _carryBox = _cursor;
            Status?.Invoke($"Holding box {_cursor + 1:00} - aim with the pad, A swaps, B releases.");
        }
    }

    private void ToggleMark()
    {
        if (_carryBox is not null) return;
        if (!_marked.Remove(_cursor)) _marked.Add(_cursor);
        Status?.Invoke(_marked.Count == 0 ? "No boxes marked." : $"{_marked.Count} box(es) marked.");
    }

    private async Task ShowActionsAsync()
    {
        var markedList = _marked.OrderBy(x => x).ToList();
        var markLabel = markedList.Count > 0 ? $"Marked actions ({markedList.Count})" : "-";
        var choice = await PadMenu.ShowAsync(_host, "BOX ACTIONS",
            _carryBox is { } held ? $"Holding box {held + 1:00}" : null,
            new PadOption("Mark all boxes", IconPath: "storage"),
            new PadOption("Clear marks", IconPath: "hex"),
            new PadOption(markedList.Count > 0 ? $"Clear marked boxes ({markedList.Count})" : "-", IconPath: "hex"),
            new PadOption(markedList.Count > 0 ? $"Delete marked boxes ({markedList.Count}, mons rescued)" : "-", IconPath: "release"));
        switch (choice)
        {
            case "Mark all boxes":
                for (var i = 0; i < _boxCount; i++) _marked.Add(i);
                Status?.Invoke($"{_boxCount} boxes marked.");
                break;
            case "Clear marks":
                _marked.Clear();
                Status?.Invoke("Marks cleared.");
                break;
            case var clear when clear?.StartsWith("Clear marked", StringComparison.Ordinal) == true:
            {
                var confirmed = await PadMenu.ConfirmAsync(_host, "CLEAR MARKED BOXES?",
                    $"{markedList.Count} box(es) emptied. Pokémon released. Backed up first.", "Clear");
                if (!confirmed) break;
                var ok = await ClearBoxesAsync(markedList);
                _marked.Clear();
                Status?.Invoke(ok ? "Marked boxes cleared." : "Clear failed.");
                break;
            }
            case var del when del?.StartsWith("Delete marked", StringComparison.Ordinal) == true:
            {
                var confirmed = await PadMenu.ConfirmAsync(_host, "DELETE MARKED BOXES?",
                    $"{markedList.Count} box(es) emptied; their Pokémon are rescued into other boxes.", "Delete");
                if (!confirmed) break;
                var ok = await DeleteBoxesAsync(markedList);
                _marked.Clear();
                Status?.Invoke(ok ? "Marked boxes deleted, mons rescued." : "Delete failed.");
                break;
            }
        }
        _canvas.InvalidateSurface();
    }
}
