using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using SkiaSharp;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>A compact resident field journal, with direct touch and controller actions.</summary>
public sealed class PokeparkResidentCard : IPadHandler
{
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Grid _host;
    private readonly Grid _overlay;
    private readonly Border _window;
    private readonly GamepadRouter? _router;
    private readonly List<Border> _actions = [];
    private static readonly string[] Keys = ["talk", "snack", "play", "relax", "leave"];
    private int _selected;
    private bool _closed;

    public static Task<string?> ShowAsync(Grid host, ParkPokemon mon, ISpriteService sprites,
        PokeparkSpriteService walking, string mood, string activity, string trait, string likes, string story) =>
        new PokeparkResidentCard(host, mon, sprites, walking, mood, activity, trait, likes, story)._result.Task;

    private PokeparkResidentCard(Grid host, ParkPokemon mon, ISpriteService sprites,
        PokeparkSpriteService walking, string mood, string activity, string trait, string likes, string story)
    {
        _host = host;
        _router = IPlatformApplication.Current?.Services.GetService<GamepadRouter>();
        var heading = new Grid { ColumnDefinitions = [new(GridLength.Star), new(40)], HeightRequest = 34 };
        heading.Add(new Label { Text = "POKÉPARK  /  RESIDENT JOURNAL", FontSize = 11, FontAttributes = FontAttributes.Bold,
            TextColor = UiTokens.IndigoInk, VerticalTextAlignment = TextAlignment.Center });
        var close = new Label { Text = "×", FontFamily = "Rounded", FontSize = 26, TextColor = UiTokens.Ink0, HorizontalTextAlignment = TextAlignment.Center };
        Tap(close, () => Close(null)); heading.Add(close); Grid.SetColumn(close, 1);

        var portrait = new SKCanvasView { MinimumHeightRequest = 40 };
        portrait.PaintSurface += (_, e) =>
        {
            var canvas = e.Surface.Canvas; var w = e.Info.Width; var h = e.Info.Height;
            canvas.Clear(SKColor.Parse("#D9ECC5"));
            using var paint = new SKPaint { IsAntialias = false, Color = SKColor.Parse("#BED797") };
            canvas.DrawOval(w * .5f, h * .9f, w * .65f, h * .3f, paint);
            paint.Color = SKColor.Parse("#91B870");
            for (var i = 0; i < 8; i++) canvas.DrawRect((i * 43 + 17) % Math.Max(1, w), h * .78f + i % 3 * 7, 3, 5, paint);
            var frame = walking.GetFrame(mon.Species, mon.Form, mon.Shiny, 0, 0, isWalking: false);
            var bitmap = frame?.Bitmap ?? sprites.GetSprite(mon.Species, mon.Form, mon.Shiny);
            if (bitmap is null) return;
            var source = frame?.Source ?? SKRect.Create(bitmap.Width, bitmap.Height);
            var scale = Math.Min(w * .72f / source.Width, h * .8f / source.Height);
            var dw = source.Width * scale; var dh = source.Height * scale;
            paint.Color = SKColors.White;
            canvas.DrawBitmap(bitmap, source, SKRect.Create((w - dw) / 2, (h - dh) / 2, dw, dh), paint);
        };
        void Repaint() => MainThread.BeginInvokeOnMainThread(() => { if (!_closed) portrait.InvalidateSurface(); });
        sprites.Warm(mon.Species, mon.Form, mon.Shiny, Repaint);
        walking.Warm(mon.Species, mon.Form, mon.Shiny, Repaint);
        var name = new Label { Text = mon.Name + (mon.Shiny ? " ★" : ""), FontSize = 17, FontAttributes = FontAttributes.Bold,
            TextColor = UiTokens.Ink0, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.TailTruncation };
        var chip = new Border { BackgroundColor = Color.FromArgb("#E5EDDC"), StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 9 }, Padding = new Thickness(7, 3),
            Content = new Label { Text = mood, TextColor = Color.FromArgb("#42604A"), FontSize = 11, HorizontalTextAlignment = TextAlignment.Center } };
        var left = new Grid { RowSpacing = 4, RowDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            Children = { portrait, name, chip } };
        Grid.SetRow(name, 1); Grid.SetRow(chip, 2);
        var notes = new VerticalStackLayout { Spacing = 8 };
        notes.Add(Section("RIGHT NOW", activity));
        notes.Add(Section("PERSONALITY", trait));
        notes.Add(Section("FAVORITE LITTLE THINGS", likes));
        notes.Add(Section("MEADOW MEMORY", story));
        notes.Add(new Label { Text = "Park personality is just for fun. Your Pokémon's game data stays unchanged.", FontSize = 10, TextColor = UiTokens.InkSoft });
        var journal = new ScrollView { Content = notes };
        var body = new Grid { ColumnSpacing = 14, ColumnDefinitions = [new(new GridLength(0.36, GridUnitType.Star)), new(new GridLength(0.64, GridUnitType.Star))], Children = { left, journal } };
        Grid.SetColumn(journal, 1);
        var actions = new Grid { ColumnSpacing = 6, ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star), new(GridLength.Star), new(GridLength.Star)] };
        var captions = new[] { ("♪", "Talk"), ("●", "Snack"), ("✦", "Play"), ("~", "Relax") };
        for (var i = 0; i < captions.Length; i++)
        {
            var index = i;
            var tile = new Border { StrokeShape = new RoundRectangle { CornerRadius = 8 }, Padding = new Thickness(3, 5),
                Content = new Label { Text = captions[i].Item1 + "  " + captions[i].Item2, FontFamily = "Rounded", FontSize = 12, FontAttributes = FontAttributes.Bold,
                    TextColor = UiTokens.Ink0, HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center }, HeightRequest = 38 };
            Tap(tile, () => Close(Keys[index])); _actions.Add(tile); actions.Add(tile); Grid.SetColumn(tile, i);
        }
        var bottom = new Grid { ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)] };
        bottom.Add(new Label { Text = "A  Choose    B  Back", FontSize = 10, TextColor = UiTokens.InkSoft, VerticalTextAlignment = TextAlignment.Center });
        var leave = new Border { StrokeShape = new RoundRectangle { CornerRadius = 5 }, Padding = new Thickness(7, 4),
            Content = new Label { Text = "↗  Leave park", FontFamily = "Rounded", FontSize = 11, TextColor = UiTokens.InkSoft } };
        Tap(leave, () => Close("leave")); _actions.Add(leave); bottom.Add(leave); Grid.SetColumn(leave, 1);
        var content = new Grid { RowSpacing = 8, RowDefinitions = [new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            Children = { heading, body, actions, bottom } };
        Grid.SetRow(body, 1); Grid.SetRow(actions, 2); Grid.SetRow(bottom, 3);
        _window = new Border { BackgroundColor = UiTokens.Paper, Stroke = UiTokens.ShellEdge, StrokeThickness = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 14 }, Padding = new Thickness(14, 8), Content = content,
            HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center };
        Resize();
        _overlay = Kit.AttachOverlay(host, _window, () => Close(null));
        host.SizeChanged += HostSizeChanged;
        _overlay.Unloaded += OverlayUnloaded;
        App.Suspended += Suspended;
        Highlight(0); _router?.Push(this);
    }

    private static View Section(string caption, string text) => new VerticalStackLayout
    {
        Spacing = 2,
        Children = { new Label { Text = caption, FontSize = 9, FontAttributes = FontAttributes.Bold, TextColor = UiTokens.IndigoInk },
            new Label { Text = text, FontSize = 12, TextColor = UiTokens.Ink0, LineBreakMode = LineBreakMode.WordWrap } }
    };
    private static void Tap(View view, Action action)
    {
        var tap = new TapGestureRecognizer(); tap.Tapped += (_, _) => action(); view.GestureRecognizers.Add(tap);
    }
    private void Resize()
    {
        _window.WidthRequest = Math.Min(570, Math.Max(0, (_host.Width > 0 ? _host.Width : 640) - 24));
        _window.HeightRequest = Math.Min(308, Math.Max(0, (_host.Height > 0 ? _host.Height : 360) - 24));
    }
    private void HostSizeChanged(object? sender, EventArgs e) => Resize();
    private void OverlayUnloaded(object? sender, EventArgs e) => Close(null);
    private void Suspended() => Close(null);
    public bool OnPadButton(PadButton button)
    {
        switch (button)
        {
            case PadButton.Left: Highlight(_selected == 4 ? 3 : Math.Max(0, _selected - 1)); break;
            case PadButton.Right: Highlight(Math.Min(4, _selected + 1)); break;
            case PadButton.Down: Highlight(4); break;
            case PadButton.Up: Highlight(_selected == 4 ? 0 : _selected); break;
            case PadButton.A: Close(Keys[_selected]); break;
            case PadButton.B: Close(null); break;
        }
        return true;
    }
    private void Highlight(int index)
    {
        _selected = index;
        for (var i = 0; i < _actions.Count; i++)
        {
            _actions[i].BackgroundColor = i == index ? UiTokens.SelectFill : UiTokens.PaperShade;
            _actions[i].Stroke = i == index ? UiTokens.SelectBorder : UiTokens.ShellEdge;
            _actions[i].StrokeThickness = i == index ? 2 : 1;
        }
    }
    private void Close(string? result)
    {
        if (_closed) return;
        _closed = true; App.Suspended -= Suspended; _host.SizeChanged -= HostSizeChanged; _overlay.Unloaded -= OverlayUnloaded;
        _router?.Remove(this); _host.Remove(_overlay); _result.TrySetResult(result);
    }
}
