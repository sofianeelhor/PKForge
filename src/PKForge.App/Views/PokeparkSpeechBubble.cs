using PKForge.App.Theme;
using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using SkiaSharp;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>A small in-world, typewritten dialogue window; leaves the meadow visible.</summary>
public sealed class PokeparkSpeechBubble : IPadHandler
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly Label _line;
    private readonly Label _hint;
    private readonly IDispatcherTimer _timer;
    private readonly GamepadRouter? _router;
    private readonly string _message;
    private int _characters;
    private bool _closed;

    public static Task ShowAsync(Grid host, string speaker, string message, ParkPokemon? mon = null,
        ISpriteService? sprites = null, PokeparkSpriteService? walking = null) =>
        new PokeparkSpeechBubble(host, speaker, message, mon, sprites, walking)._done.Task;

    private PokeparkSpeechBubble(Grid host, string speaker, string message, ParkPokemon? mon,
        ISpriteService? sprites, PokeparkSpriteService? walking)
    {
        _host = host; _message = message;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        var ink = Color.FromArgb("#355443");
        _line = new Label { TextColor = ink, FontFamily = "PixelUI", FontSize = 17,
            LineBreakMode = LineBreakMode.WordWrap, VerticalOptions = LayoutOptions.Center };
        _hint = new Label { Text = "A / tap · reveal", TextColor = Color.FromArgb("#74846A"),
            FontFamily = "PixelUI", FontSize = UiTokens.TextSmall, HorizontalOptions = LayoutOptions.End };
        var name = new Label { Text = speaker, FontFamily = "PixelUI", FontSize = 17,
            TextColor = Color.FromArgb("#FFF7DA"), FontAttributes = FontAttributes.Bold };
        var nameplate = new Border { BackgroundColor = ink, StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 6 }, Padding = new Thickness(12, 4),
            HorizontalOptions = LayoutOptions.Start, Margin = new Thickness(14, 0, 0, -5), Content = name, ZIndex = 2 };
        var text = new Grid { RowDefinitions = [new(GridLength.Star), new(GridLength.Auto)], RowSpacing = 4,
            Children = { _line, _hint } };
        Grid.SetRow(_hint, 1);
        var body = new Grid { ColumnDefinitions = [new(GridLength.Auto), new(GridLength.Star)], ColumnSpacing = 12 };
        if (mon is not null && sprites is not null && walking is not null)
        {
            var portrait = new SKCanvasView { WidthRequest = 64, HeightRequest = 64, VerticalOptions = LayoutOptions.Center };
            portrait.PaintSurface += (_, e) =>
            {
                var c = e.Surface.Canvas; c.Clear(SKColor.Parse("#Dceabc"));
                var frame = walking.GetFrame(mon.Species, mon.Form, mon.Shiny, 0, 0, false);
                var bitmap = frame?.Bitmap ?? sprites.GetSprite(mon.Species, mon.Form, mon.Shiny);
                if (bitmap is null) return;
                var source = frame?.Source ?? SKRect.Create(bitmap.Width, bitmap.Height);
                var scale = Math.Min(e.Info.Width / source.Width, e.Info.Height / source.Height);
                var w = source.Width * scale; var h = source.Height * scale;
                c.DrawBitmap(bitmap, source, SKRect.Create((e.Info.Width - w) / 2, (e.Info.Height - h) / 2, w, h));
            };
            body.Add(portrait);
        }
        body.Add(text); Grid.SetColumn(text, 1);
        var window = new Border
        {
            BackgroundColor = Color.FromArgb("#FFF9E6"), Stroke = ink, StrokeThickness = 3,
            StrokeShape = new RoundRectangle { CornerRadius = 13 }, Padding = new Thickness(13, 11),
            MinimumHeightRequest = 110,
            Shadow = Kit.HardShadow(),
            Content = body
        };
        var tail = new SKCanvasView { HeightRequest = 15, WidthRequest = 28, Margin = new Thickness(0, 0, 35, -3),
            HorizontalOptions = LayoutOptions.End, InputTransparent = true };
        tail.PaintSurface += (_, e) =>
        {
            var c = e.Surface.Canvas; c.Clear(SKColors.Transparent);
            using var p = new SKPaint { Color = SKColor.Parse("#355443"), IsAntialias = false };
            using var triangle = new SKPath(); triangle.MoveTo(0, e.Info.Height); triangle.LineTo(e.Info.Width * .8f, 0);
            triangle.LineTo(e.Info.Width, e.Info.Height); triangle.Close(); c.DrawPath(triangle, p);
            p.Color = SKColor.Parse("#FFF9E6"); c.Save(); c.Translate(0, 5); c.DrawPath(triangle, p); c.Restore();
        };
        var stack = new VerticalStackLayout { Spacing = 0, Children = { tail, nameplate, window },
            MaximumWidthRequest = 720, HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.End, Margin = new Thickness(16, 8, 16, 18) };
        _overlay = new Grid { BackgroundColor = Colors.Transparent, Children = { stack } };
        var tap = new TapGestureRecognizer(); tap.Tapped += (_, _) => Advance(); _overlay.GestureRecognizers.Add(tap);
        host.Add(_overlay);
        Grid.SetRowSpan(_overlay, Math.Max(1, host.RowDefinitions.Count));
        Grid.SetColumnSpan(_overlay, Math.Max(1, host.ColumnDefinitions.Count));
        _router?.Push(this);
        _timer = host.Dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromMilliseconds(26);
        _timer.Tick += (_, _) =>
        {
            if (_characters < _message.Length) _line.Text = _message[..++_characters];
            else { _timer.Stop(); _hint.Text = "A / tap · continue"; }
        };
        App.Suspended += Close;
        _host.Unloaded += HostUnloaded;
        _timer.Start();
    }
    private void HostUnloaded(object? sender, EventArgs e) => Close();
    private void Advance()
    {
        if (_characters < _message.Length)
        { _characters = _message.Length; _line.Text = _message; _timer.Stop(); _hint.Text = "A / tap · continue"; }
        else Close();
    }
    public bool OnPadButton(PadButton button)
    {
        if (button is PadButton.A or PadButton.B or PadButton.Start) Advance();
        return true;
    }
    private void Close()
    {
        if (_closed) return; _closed = true;
        _timer.Stop(); App.Suspended -= Close; _host.Unloaded -= HostUnloaded;
        _router?.Remove(this); _host.Remove(_overlay); _done.TrySetResult();
    }
}
