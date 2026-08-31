using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// A Pokémon-style dialogue box: a DS message window that types its text out one
/// character at a time, with a blinking advance arrow. A / B / Start (or a tap) reveals
/// the rest of the line, then moves to the next. Owns the gamepad while open.
/// </summary>
public sealed class DialogueBox : IPadHandler
{
    private readonly TaskCompletionSource _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly GamepadRouter? _router;
    private readonly Label _text;
    private readonly View _more;
    private readonly string[] _lines;
    private int _lineIndex;
    private int _charIndex;
    private string _current = "";
    private IDispatcherTimer? _typer;
    private IDispatcherTimer? _blink;

    public static Task ShowSequenceAsync(Grid host, params string[] lines) =>
        new DialogueBox(host, lines)._result.Task;

    private DialogueBox(Grid host, string[] lines)
    {
        _host = host;
        _lines = lines.Length == 0 ? [""] : lines;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();

        _text = new Label
        {
            FontFamily = DsChrome.PixelFont, FontSize = 16, TextColor = Colors.White,
            LineBreakMode = LineBreakMode.WordWrap, VerticalOptions = LayoutOptions.Start,
        };
        _more = AdvanceArrow();

        // Gen-5 message window: maroon face, deep-maroon frame, white pixel text.
        var window = new Border
        {
            BackgroundColor = UiTokens.Maroon,
            Stroke = UiTokens.MaroonDeep,
            StrokeThickness = 3,
            StrokeShape = new RoundRectangle { CornerRadius = 10 },
            Shadow = new Shadow { Brush = Brush.Black, Opacity = 0.28f, Radius = 10, Offset = new Point(0, 4) },
            Padding = new Thickness(16, 13),
            Margin = new Thickness(18, 0, 18, 16),
            MinimumHeightRequest = 96,
            VerticalOptions = LayoutOptions.End,
            Content = new Grid { Children = { _text, _more } },
        };

        var scrim = new BoxView { Color = UiTokens.Scrim };
        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Advance();
        scrim.GestureRecognizers.Add(tap);
        var winTap = new TapGestureRecognizer();
        winTap.Tapped += (_, _) => Advance();
        window.GestureRecognizers.Add(winTap);

        _overlay = new Grid { Children = { scrim, window } };
        host.Add(_overlay);
        Grid.SetRowSpan(_overlay, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(_overlay, Math.Max(1, host.ColumnDefinitions.Count));

        _router?.Push(this);
        StartLine();
        StartBlink();
    }

    /// <summary>The blinking advance indicator: a drawn white triangle on the maroon face.</summary>
    private static SKCanvasView AdvanceArrow()
    {
        var arrow = new SKCanvasView
        {
            WidthRequest = 18, HeightRequest = 12, InputTransparent = true, IsVisible = false,
            HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.End,
        };
        arrow.PaintSurface += (_, args) =>
        {
            var canvas = args.Surface.Canvas;
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { Color = Pksm.Ink, IsAntialias = false };
            using var path = new SKPath();
            var w = args.Info.Width;
            var h = args.Info.Height;
            path.MoveTo(w * 0.26f, h * 0.22f);
            path.LineTo(w * 0.74f, h * 0.22f);
            path.LineTo(w * 0.5f, h * 0.78f);
            path.Close();
            canvas.DrawPath(path, paint);
        };
        return arrow;
    }

    private void StartLine()
    {
        _current = _lines[_lineIndex];
        _charIndex = 0;
        _text.Text = "";
        _more.IsVisible = false;
        _typer?.Stop();
        _typer = _host.Dispatcher.CreateTimer();
        _typer.Interval = TimeSpan.FromMilliseconds(24);
        _typer.Tick += (_, _) =>
        {
            if (_charIndex >= _current.Length) { _typer?.Stop(); _more.IsVisible = true; return; }
            _charIndex++;
            _text.Text = _current[.._charIndex];
        };
        _typer.Start();
    }

    private void StartBlink()
    {
        _blink = _host.Dispatcher.CreateTimer();
        _blink.Interval = TimeSpan.FromMilliseconds(480);
        _blink.Tick += (_, _) => { if (_more.IsVisible) _more.Opacity = _more.Opacity > 0.5 ? 0.2 : 1.0; };
        _blink.Start();
    }

    private void Advance()
    {
        if (_charIndex < _current.Length)
        {
            _typer?.Stop();
            _charIndex = _current.Length;
            _text.Text = _current;
            _more.IsVisible = true;
            return;
        }
        _lineIndex++;
        if (_lineIndex >= _lines.Length) { Close(); return; }
        StartLine();
    }

    public bool OnPadButton(PadButton button)
    {
        if (button is PadButton.A or PadButton.B or PadButton.Start) Advance();
        return true; // own the pad while the dialogue is up
    }

    private void Close()
    {
        _typer?.Stop();
        _blink?.Stop();
        _router?.Remove(this);
        _host.Remove(_overlay);
        _result.TrySetResult();
    }
}
