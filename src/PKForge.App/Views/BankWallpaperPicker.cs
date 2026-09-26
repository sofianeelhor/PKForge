using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// Chooses a Bank box wallpaper: the storage palette's flats, then every game's own box
/// wallpapers, grouped by game. D-pad moves, L/R jump a game, A picks, B cancels; tap picks.
/// </summary>
public sealed class BankWallpaperPicker : IPadPagingHandler
{
    private const float TileAspect = 5f / 6f;

    private sealed record Section(string Title, IReadOnlyList<BankWallpaper> Items);

    private readonly TaskCompletionSource<BankWallpaper?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly SKCanvasView _canvas;
    private readonly FrameInvalidator _frame;
    private readonly FramePacer _pacer;
    private readonly SKTypeface _face = BoxBrowserPage.PixelTypeface();
    private readonly string _title;
    private readonly List<Section> _sections;
    private readonly List<(int Section, int Index)> _flat = [];
    private readonly Spring _scroll = new(0);
    private int _cursor;
    private int _columns = 6;
    private float _density = 1;
    private readonly List<(SKRect Rect, int Flat)> _hits = [];

    public static Task<BankWallpaper?> ShowAsync(Grid host, string title, BankWallpaper current) =>
        new BankWallpaperPicker(host, title, current)._result.Task;

    private BankWallpaperPicker(Grid host, string title, BankWallpaper current)
    {
        _host = host;
        _title = title;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _sections =
        [
            new("Colors", [.. Enumerable.Range(0, Pksm.BoxWallpapers.Length).Select(BankWallpaper.Color)]),
            .. BankWallpaper.ArtCatalog.Select(g => new Section(g.Title, [.. g.Assets.Select(BankWallpaper.Art)])),
        ];
        for (var s = 0; s < _sections.Count; s++)
            for (var i = 0; i < _sections[s].Items.Count; i++)
            {
                if (_sections[s].Items[i].Id == current.Id) _cursor = _flat.Count;
                _flat.Add((s, i));
            }

        _canvas = new SKCanvasView { EnableTouchEvents = true };
        _canvas.PaintSurface += Paint;
        _canvas.Touch += Touch;
        _frame = new FrameInvalidator(_canvas);
        _pacer = new FramePacer(_frame);
        var window = new Border { Content = _canvas, StrokeThickness = 0, BackgroundColor = Colors.Transparent, Margin = new Thickness(40, 16) };
        _overlay = Kit.AttachOverlay(host, window, () => Close(null));
        _router?.Push(this);
    }

    private BankWallpaper At(int flat) => _sections[_flat[flat].Section].Items[_flat[flat].Index];

    public bool OnPadButton(PadButton button)
    {
        var (section, index) = _flat[_cursor];
        switch (button)
        {
            case PadButton.Left: Move(_cursor - 1); return true;
            case PadButton.Right: Move(_cursor + 1); return true;
            case PadButton.Up:
                Move(index >= _columns ? _cursor - _columns
                    : section > 0 ? FlatOf(section - 1, Math.Min(_sections[section - 1].Items.Count - 1, LastRowStart(section - 1) + index % _columns)) : _cursor);
                return true;
            case PadButton.Down:
                Move(index + _columns < _sections[section].Items.Count ? _cursor + _columns
                    : LastRowStart(section) > index ? FlatOf(section, _sections[section].Items.Count - 1)
                    : section + 1 < _sections.Count ? FlatOf(section + 1, Math.Min(_sections[section + 1].Items.Count - 1, index % _columns)) : _cursor);
                return true;
            case PadButton.L: Move(FlatOf(Math.Max(0, index == 0 ? section - 1 : section), 0)); return true;
            case PadButton.R: Move(section + 1 < _sections.Count ? FlatOf(section + 1, 0) : _cursor); return true;
            case PadButton.A: Close(At(_cursor)); return true;
            case PadButton.B: Close(null); return true;
            default: return true;
        }
    }

    private int LastRowStart(int section) => (_sections[section].Items.Count - 1) / _columns * _columns;

    private int FlatOf(int section, int index) => _flat.FindIndex(f => f.Section == section && f.Index == index);

    private void Move(int flat)
    {
        _cursor = Math.Clamp(flat, 0, _flat.Count - 1);
        _pacer.Kick();
    }

    private void Close(BankWallpaper? chosen)
    {
        _router?.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(chosen);
    }

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released) return;
        var point = new SKPoint(args.Location.X * _density, args.Location.Y * _density);
        foreach (var (rect, flat) in _hits)
        {
            if (!rect.Contains(point)) continue;
            if (flat == _cursor) Close(At(flat));
            else Move(flat);
            return;
        }
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var info = args.Info;
        _scroll.Step(_pacer.Advance(), 300f, 34.6f);
        _density = _canvas.Width > 0 ? info.Width / (float)_canvas.Width : 1;
        _columns = info.Width / _density >= 700 ? 8 : 5;
        canvas.Clear(SKColors.Transparent);
        PksmPaint.Panel(canvas, new SKRect(0, 0, info.Width, info.Height), Pksm.Housing, 10 * _density);

        using var title = new SKFont(_face, 22 * _density);
        using var small = new SKFont(_face, 15 * _density);
        PksmPaint.CenterText(canvas, _title, 16 * _density, 24 * _density, title, SKColors.White, Pksm.LogoVoid);

        var hint = 40 * _density;
        var area = new SKRect(0, 44 * _density, info.Width, info.Height - hint);
        var gap = 10 * _density;
        var tileW = (area.Width - gap * (_columns + 1)) / _columns;
        var tileH = tileW * TileAspect;
        var heading = 26 * _density;

        // Lay everything out in content space, then scroll so the cursor stays in view.
        var y = 0f;
        var layout = new List<(SKRect Rect, int Flat)>(_flat.Count);
        var headings = new List<(float Y, string Title)>();
        for (var s = 0; s < _sections.Count; s++)
        {
            headings.Add((y, _sections[s].Title));
            y += heading;
            for (var i = 0; i < _sections[s].Items.Count; i++)
            {
                var col = i % _columns;
                var row = i / _columns;
                layout.Add((SKRect.Create(gap + col * (tileW + gap), y + row * (tileH + gap), tileW, tileH), layout.Count));
            }
            y += ((_sections[s].Items.Count + _columns - 1) / _columns) * (tileH + gap) + gap * 0.5f;
        }
        var cursorRect = layout[_cursor].Rect;
        var top = _scroll.Target;
        if (cursorRect.Top - heading < top) top = cursorRect.Top - heading - gap;
        else if (cursorRect.Bottom > top + area.Height) top = cursorRect.Bottom - area.Height + gap;
        _scroll.Target = Math.Clamp(top, 0, Math.Max(0, y - area.Height));
        var offset = area.Top - _scroll.Value;

        canvas.Save();
        canvas.ClipRect(area);
        foreach (var (hy, text) in headings)
            PksmPaint.CenterText(canvas, text, gap, hy + offset + heading * 0.5f, small, Pksm.ShinyGold, SKColors.Black);
        _hits.Clear();
        foreach (var (content, flat) in layout)
        {
            var rect = new SKRect(content.Left, content.Top + offset, content.Right, content.Bottom + offset);
            if (rect.Bottom < area.Top || rect.Top > area.Bottom) continue;
            _hits.Add((rect, flat));
            using var clip = new SKRoundRect(rect, 6 * _density, 6 * _density);
            canvas.Save();
            canvas.ClipRoundRect(clip, antialias: true);
            BankBoxArt.Paint(canvas, rect, At(flat), _frame.Request);
            canvas.Restore();
            using var border = new SKPaint
            {
                Color = flat == _cursor ? Pksm.ShinyGold : SKColors.White.WithAlpha(0x50),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (flat == _cursor ? 4 : 1.5f) * _density,
                IsAntialias = true,
            };
            canvas.DrawRoundRect(clip, border);
        }
        canvas.Restore();

        PksmPaint.HintBar(canvas, new SKRect(0, info.Height - hint, info.Width, info.Height),
            [("A", "Choose"), ("L/R", "Game"), ("B", "Cancel")], small);
        _pacer.Continue(!_scroll.Settled);
    }
}
