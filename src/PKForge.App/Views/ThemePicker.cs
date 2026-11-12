using PKForge.App.Services;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// Chooses the color scheme: every theme as a small preview of the box and the editor in its own
/// colors. D-pad moves, A picks, B cancels; tap picks.
/// </summary>
public sealed class ThemePicker : IPadPagingHandler
{
    private const float TileAspect = 100f / 160f;

    private readonly TaskCompletionSource<ColorTheme?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly SKCanvasView _canvas;
    private readonly FramePacer _pacer;
    private readonly SKTypeface _face = BoxBrowserPage.PixelTypeface();
    private readonly IReadOnlyList<ColorTheme> _themes = ColorThemes.All;
    private readonly string _currentId;
    private readonly Spring _scroll = new(0);
    private int _cursor;
    private int _columns = 4;
    private float _density = 1;
    private readonly List<(SKRect Rect, int Index)> _hits = [];
    private (SKRect Rect, PadButton Button)[] _hintHits = [];

    public static Task<ColorTheme?> ShowAsync(Grid host) => new ThemePicker(host)._result.Task;

    private ThemePicker(Grid host)
    {
        _host = host;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _currentId = ColorTheme.Current.Id;
        _cursor = Math.Max(0, _themes.ToList().FindIndex(theme => theme.Id == _currentId));

        _canvas = new SKCanvasView { EnableTouchEvents = true };
        _canvas.PaintSurface += PaintGuard.Wrap("ThemePicker.Paint", Paint);
        _canvas.Touch += Touch;
        _pacer = new FramePacer(new FrameInvalidator(_canvas));
        var window = new Border { Content = _canvas, StrokeThickness = 0, BackgroundColor = Colors.Transparent, Margin = new Thickness(40, 16) };
        _overlay = Kit.AttachOverlay(host, window, () => Close(null));
        _router?.Push(this);
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left: Move(_cursor - 1); return true;
            case PadButton.Right: Move(_cursor + 1); return true;
            case PadButton.Up: Move(_cursor >= _columns ? _cursor - _columns : _cursor); return true;
            case PadButton.Down: Move(Math.Min(_themes.Count - 1, _cursor + _columns)); return true;
            case PadButton.A: Close(_themes[_cursor]); return true;
            case PadButton.B: Close(null); return true;
            default: return true;
        }
    }

    private void Move(int index)
    {
        _cursor = Math.Clamp(index, 0, _themes.Count - 1);
        _pacer.Kick();
    }

    private void Close(ColorTheme? chosen)
    {
        _router?.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult(chosen);
    }

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released) return;
        // Touch locations arrive in canvas pixels, like the tile rects.
        var point = args.Location;
        foreach (var (rect, button) in _hintHits)
        {
            if (!rect.Contains(point)) continue;
            OnPadButton(button);
            return;
        }
        foreach (var (rect, index) in _hits)
        {
            if (!rect.Contains(point)) continue;
            if (index == _cursor) Close(_themes[index]);
            else Move(index);
            return;
        }
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var info = args.Info;
        _scroll.Step(_pacer.Advance(), 300f, 34.6f);
        _density = _canvas.Width > 0 ? info.Width / (float)_canvas.Width : 1;
        _columns = info.Width / _density >= 700 ? 4 : 2;
        canvas.Clear(SKColors.Transparent);
        PksmPaint.Panel(canvas, new SKRect(0, 0, info.Width, info.Height), Pksm.Housing, 10 * _density);

        using var title = new SKFont(_face, 22 * _density);
        using var small = new SKFont(_face, 15 * _density);
        PksmPaint.CenterText(canvas, "Color scheme", 16 * _density, 24 * _density, title, ColorTheme.Current.Bright, Pksm.LogoVoid);

        var hint = 40 * _density;
        var area = new SKRect(0, 44 * _density, info.Width, info.Height - hint);
        var gap = 12 * _density;
        var caption = 22 * _density;
        var tileW = (area.Width - gap * (_columns + 1)) / _columns;
        var tileH = tileW * TileAspect;
        var rowH = tileH + caption + gap;
        var rows = (_themes.Count + _columns - 1) / _columns;

        // Scroll so the cursor's row stays in view.
        var cursorTop = _cursor / _columns * rowH;
        var top = _scroll.Target;
        if (cursorTop < top) top = cursorTop;
        else if (cursorTop + rowH > top + area.Height) top = cursorTop + rowH - area.Height + gap;
        _scroll.Target = Math.Clamp(top, 0, Math.Max(0, rows * rowH + gap - area.Height));
        var offset = area.Top + gap * 0.5f - _scroll.Value;

        canvas.Save();
        canvas.ClipRect(area);
        _hits.Clear();
        for (var i = 0; i < _themes.Count; i++)
        {
            var tile = SKRect.Create(gap + i % _columns * (tileW + gap), offset + i / _columns * rowH, tileW, tileH);
            if (tile.Bottom + caption < area.Top || tile.Top > area.Bottom) continue;
            var theme = _themes[i];
            ThemePreviewPaint.Paint(canvas, tile, theme, _face);
            using var border = new SKPaint
            {
                Color = i == _cursor ? Pksm.ShinyGold : SKColors.White.WithAlpha(0x50),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = (i == _cursor ? 4 : 1.5f) * _density,
                IsAntialias = true,
            };
            canvas.DrawRoundRect(tile, tileH * 0.04f, tileH * 0.04f, border);
            var name = theme.Id == _currentId ? $"{theme.Name} (in use)" : theme.Name;
            PksmPaint.CenterText(canvas, name, tile.MidX, tile.Bottom + caption * 0.6f, small,
                i == _cursor ? Pksm.ShinyGold : ColorTheme.Current.Bright, SKColors.Black, SKTextAlign.Center);
            _hits.Add((SKRect.Create(tile.Left, tile.Top, tile.Width, tile.Height + caption), i));
        }
        canvas.Restore();

        var hintBar = new SKRect(0, info.Height - hint, info.Width, info.Height);
        (string, string)[] prompts = [("A", "Choose"), ("B", "Cancel")];
        PksmPaint.HintBar(canvas, hintBar, prompts, small);
        PadButton[] buttons = [PadButton.A, PadButton.B];
        _hintHits = [.. PksmPaint.HintBarHitRects(hintBar, prompts, small).Select((r, i) => (r, buttons[i]))];
        _pacer.Continue(!_scroll.Settled);
    }
}
