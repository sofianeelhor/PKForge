using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// Shows a picked image in the frame it will be used in (<see cref="GameArtPaint.Preview"/>)
/// before it replaces the game's art. A uses it, B cancels; the hint bar is tappable.
/// </summary>
public sealed class GameArtPreview : IPadPagingHandler
{
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly SKCanvasView _canvas;
    private readonly SKTypeface _face = BoxBrowserPage.PixelTypeface();
    private readonly SKImage? _image;
    private readonly GameArtKind _kind;
    private readonly string _gameName;
    private readonly string _caption;
    private readonly SKColor _plastic;
    private (SKRect Rect, PadButton Button)[] _hintHits = [];

    /// <summary>True when the player chose to use the image.</summary>
    public static Task<bool> ShowAsync(Grid host, byte[] png, GameArtKind kind, string gameName, string caption, Color plastic) =>
        new GameArtPreview(host, png, kind, gameName, caption, plastic)._result.Task;

    private GameArtPreview(Grid host, byte[] png, GameArtKind kind, string gameName, string caption, Color plastic)
    {
        _host = host;
        _kind = kind;
        _gameName = gameName;
        _caption = caption;
        _plastic = plastic.ToSKColor();
        try { _image = SKImage.FromEncodedData(png); }
        catch (ArgumentException) { _image = null; }
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        _canvas = new SKCanvasView { EnableTouchEvents = true };
        _canvas.PaintSurface += PaintGuard.Wrap("GameArtPreview.Paint", Paint);
        _canvas.Touch += Touch;
        var window = new Border { Content = _canvas, StrokeThickness = 0, BackgroundColor = Colors.Transparent, Margin = new Thickness(60, 20) };
        _overlay = Kit.AttachOverlay(host, window, () => Close(false));
        _router?.Push(this);
    }

    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.A: Close(_image is not null); return true;
            case PadButton.B: Close(false); return true;
            default: return true;
        }
    }

    private void Close(bool use)
    {
        if (_result.Task.IsCompleted) return;
        _router?.Remove(this);
        _host.Remove(_overlay);
        _image?.Dispose();
        _result.TrySetResult(use);
    }

    private void Touch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Released) return;
        foreach (var (rect, button) in _hintHits)
        {
            if (!rect.Contains(args.Location)) continue;
            OnPadButton(button);
            return;
        }
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var info = args.Info;
        var density = _canvas.Width > 0 ? info.Width / (float)_canvas.Width : 1;
        canvas.Clear(SKColors.Transparent);
        var hint = 40 * density;
        GameArtPaint.Preview(canvas, new SKRect(0, 0, info.Width, info.Height - hint), _result.Task.IsCompleted ? null : _image,
            _kind, _gameName, _caption, _plastic, _face, density);
        using var small = new SKFont(_face, 15 * density);
        var bar = new SKRect(0, info.Height - hint, info.Width, info.Height);
        (string, string)[] prompts = _image is null ? [("B", "Back")] : [("A", "Use this image"), ("B", "Cancel")];
        PksmPaint.HintBar(canvas, bar, prompts, small);
        PadButton[] buttons = _image is null ? [PadButton.B] : [PadButton.A, PadButton.B];
        _hintHits = [.. PksmPaint.HintBarHitRects(bar, prompts, small).Select((r, i) => (r, buttons[i]))];
    }
}
