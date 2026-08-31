using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Theme;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// A PKSM menu row rebuilt as layered navy chrome with a cyan/cobalt focus state.
/// Draws its own chrome in Skia; the label (and optional icon) ride on top.
/// </summary>
public sealed class DsFolderButton : Grid
{
    private readonly SKCanvasView _bg;
    private readonly Label _label;
    private readonly Image? _icon;
    private readonly Grid _labelViewport;
    private readonly string _iconName = "";
    private bool _selected;
    private int _marqueeGeneration;

    public Action? Tapped { get; set; }

    public DsFolderButton(PadOption option, double height = 50)
    {
        HeightRequest = height;
        ColumnDefinitions = [new(new GridLength(30)), new(new GridLength(30)), new(GridLength.Star)];

        _bg = new SKCanvasView { InputTransparent = true };
        _bg.PaintSurface += (_, args) => DrawRow(args.Surface.Canvas, args.Info, _selected);

        // The icon column: a bundled PKSM pixel icon when the option carries a semantic
        // name, else the accent glyph. White on blue buttons, indigo when selected.
        View iconHost;
        if (!string.IsNullOrEmpty(option.IconPath) && !option.IconPath.Contains('/'))
        {
            _iconName = option.IconPath;
            var native = PksmIcons.IsNative(_iconName);
            _icon = new Image
            {
                Source = PksmIcons.Source(option.IconPath, native ? PksmIcons.Native : PksmIcons.White),
                WidthRequest = 22,
                HeightRequest = 22,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                InputTransparent = true,
            };
            iconHost = _icon;
        }
        else if (option.Glyph is not null)
        {
            iconHost = new Label
            {
                Text = option.Glyph,
                FontFamily = "Rounded",
                FontSize = 18,
                TextColor = option.Accent ?? UiTokens.IndigoInk,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center,
            };
        }
        else
        {
            iconHost = new BoxView { InputTransparent = true, WidthRequest = 0 };
        }

        _label = new Label
        {
            Text = option.Label,
            FontFamily = DsChrome.PixelFont,
            FontSize = 15,
            TextColor = UiTokens.Ink0,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.NoWrap,
            HorizontalOptions = LayoutOptions.Start,
        };
        _labelViewport = new Grid { IsClippedToBounds = true, Margin = new Thickness(0, 0, 8, 0) };
        _labelViewport.Children.Add(_label);

        Children.Add(_bg);
        Grid.SetColumnSpan(_bg, 3);
        Children.Add(iconHost);
        Children.Add(_labelViewport);
        Grid.SetColumn(iconHost, 1);
        Grid.SetColumn(_labelViewport, 2);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Tapped?.Invoke();
        GestureRecognizers.Add(tap);
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            _label.TextColor = value ? UiTokens.SelectInk : UiTokens.Ink0;
            if (_icon is not null && !PksmIcons.IsNative(_iconName))
                _icon.Source = PksmIcons.Source(_iconName, value ? PksmIcons.Indigo : PksmIcons.White);
            _bg.InvalidateSurface();
            if (value) StartMarquee();
            else StopMarquee();
        }
    }

    /// <summary>Long selected labels pause, glide left at reading speed, pause, then
    /// return. Short labels allocate no timer or animation.</summary>
    private async void StartMarquee()
    {
        var generation = ++_marqueeGeneration;
        _label.TranslationX = 0;
        await Task.Delay(700);
        if (!_selected || generation != _marqueeGeneration || _labelViewport.Width <= 0) return;

        var measured = _label.Measure(double.PositiveInfinity, HeightRequest).Width;
        var overflow = measured - _labelViewport.Width;
        if (overflow <= 4) return;
        _label.WidthRequest = measured;

        while (_selected && generation == _marqueeGeneration)
        {
            var duration = (uint)Math.Clamp(overflow * 45, 1800, 6500);
            await _label.TranslateToAsync(-overflow, 0, duration, Easing.Linear);
            if (!_selected || generation != _marqueeGeneration) return;
            await Task.Delay(900);
            if (!_selected || generation != _marqueeGeneration) return;
            _label.TranslationX = 0;
            await Task.Delay(700);
        }
    }

    private void StopMarquee()
    {
        _marqueeGeneration++;
        _label.TranslationX = 0;
    }

    /// <summary>Draws the row chrome: glossy black idle, navy + light-blue border selected.</summary>
    internal static void DrawRow(SKCanvas canvas, SKImageInfo info, bool selected)
    {
        var r = new SKRect(1, 1, info.Width - 1, info.Height - 1);
        if (selected) PksmPaint.SelectedButton(canvas, r, 5);
        else PksmPaint.BlackButton(canvas, r, 5);
    }
}

/// <summary>The drawn pixel-icon set for concepts PKSM has no asset for. 16x16 grid, outline + highlight.</summary>
public enum DsIcon { Storage, Bank, Events, Pokedex, Trainer, Settings }

public static class DsIcons
{
    // Each row is 16 chars; a space is transparent. Palettes are per-icon.
    private static readonly Dictionary<DsIcon, (string[] Map, Dictionary<char, SKColor> Pal)> Data = new()
    {
        [DsIcon.Storage] = (new[]{
            "                ","   oooooooooo   ","  ohhhhhhhhhho  ","  owwwwwwwwwwo  ","  owwwwwwwwwwo  ",
            "  ooooooooooo o ","  owwwwwwwwwwo  ","  owwwoooowwwo  ","  owwoddddowwo  ","  owwoddddowwo  ",
            "  owwoooooowwo  ","  owwwwwwwwwwo  ","  ooooooooooo   ","                ","                ","                "},
            Pal('o',"#3a2f2a",'w',"#c98f52",'d',"#a06a34",'h',"#e6b877")),
        [DsIcon.Pokedex] = (new[]{
            "                ","   oooooooooo   ","  orrrrrrrrrro  ","  orwwrrrrrrro  ","  orrrrrrrrrro  ",
            "  orkkkkkkkkro  ","  orkggggggkro  ","  orkggggggkro  ","  orkkkkkkkkro  ","  orrrrrrrrrro  ",
            "  orwwrrrrwwro  ","  orrrrrrrrrro  ","   oooooooooo   ","                ","                ","                "},
            Pal('o',"#5a1414",'r',"#d23b3b",'k',"#2a2a2a",'g',"#5fd06a",'w',"#f4d84a")),
        [DsIcon.Bank] = (new[]{
            "                ","   oooooooooo   ","  obbbbbbbbbbo  ","  obcccccccccbo "," obcrrrrrrrrcbo ",
            " obcrrkkkkrrcbo "," obcrkwwwwkrcbo "," obcrkwrrwkrcbo ","  obcrrkkkkrrcbo","  obcrrrrrrrrcbo",
            "  obcccccccccbo","  obbbbbbbbbbo  ","   oooooooooo   ","                ","                ","                "},
            Pal('o',"#1d3a5a",'b',"#3a6ea5",'c',"#7fc3e0",'w',"#f4f4f4",'r',"#e2453e",'k',"#111111")),
        [DsIcon.Trainer] = (new[]{
            "                ","                ","  oooooooooooo  ","  oyyyyyyyyyyo  ","  oywwwoyyyyyo  ",
            "  oybbwoykkkyo  ","  oywwwoyyyyyo  ","  oyyyyoykkkyo  ","  oyyyyoyyyyyo  ","  oyyyyoykkkyo  ",
            "  oyyyyyyyyyyo  ","  oooooooooooo  ","                ","                ","                ","                "},
            Pal('o',"#7a4e10",'y',"#e0a03c",'w',"#ffffff",'b',"#3a6ea5",'k',"#5a3a0a")),
        [DsIcon.Events] = (new[]{
            "       yy       ","     yywwyy     ","      ywwy      ","   oooooooooo   ","  orrryyyrrrro  ",
            "  orrryyyrrrro  ","  ooooyyyooooo  ","  orrryyyrrrro  ","  orrryyyrrrro  ","  orrryyyrrrro  ",
            "  orrryyyrrrro  ","  oooooooooooo  ","                ","                ","                ","                "},
            Pal('o',"#5a1414",'r',"#e2453e",'y',"#f4d84a",'w',"#ffe9a8")),
        [DsIcon.Settings] = (new[]{
            "      oooo      ","   o oggggo o   ","   oggggggggo   ","  oggghhhhgggo  "," oggghkkkkhgggo ",
            " ogghkkwwkkhggo ","  ggkkw  wkkgg  "," ogghkkwwkkhggo "," oggghkkkkhgggo ","  oggghhhhgggo  ",
            "   oggggggggo   ","   o oggggo o   ","      oooo      ","                ","                ","                "},
            Pal('o',"#1f5a2a",'g',"#4fbf5f",'h',"#bfeec6",'k',"#12401c",'w',"#ffffff")),
    };

    private static Dictionary<char, SKColor> Pal(params object[] pairs)
    {
        var d = new Dictionary<char, SKColor>();
        for (var i = 0; i < pairs.Length; i += 2) d[(char)pairs[i]] = SKColor.Parse((string)pairs[i + 1]);
        return d;
    }

    public static void Draw(SKCanvas canvas, SKImageInfo info, DsIcon icon)
    {
        canvas.Clear(SKColors.Transparent);
        if (!Data.TryGetValue(icon, out var d)) return;
        var s = Math.Min(info.Width, info.Height) / 16f;
        var ox = (info.Width - 16 * s) / 2f;
        var oy = (info.Height - 16 * s) / 2f;
        using var paint = new SKPaint { IsAntialias = false };
        for (var r = 0; r < d.Map.Length; r++)
        {
            var rowText = d.Map[r];
            for (var q = 0; q < rowText.Length; q++)
            {
                if (!d.Pal.TryGetValue(rowText[q], out var col)) continue;
                paint.Color = col;
                canvas.DrawRect(ox + q * s, oy + r * s, s + 0.6f, s + 0.6f, paint);
            }
        }
    }
}

/// <summary>
/// A PKSM icon card: layered navy tile, bundled pixel icon as hero, pixel label beneath.
/// </summary>
public sealed class DsCard : Grid
{
    private readonly SKCanvasView _bg;
    private readonly SKCanvasView _iconCanvas;
    private readonly Label _label;
    private readonly DsIcon? _drawnIcon;
    private readonly string? _assetIcon;
    private bool _selected;

    public Action? Tapped { get; set; }

    public DsCard(DsIcon icon, string label, double height = 88)
        : this((DsIcon?)icon, null, label, height) { }

    public DsCard(string pksmAssetIcon, string label, double height = 88)
        : this(null, pksmAssetIcon, label, height) { }

    private DsCard(DsIcon? drawn, string? asset, string label, double height)
    {
        _drawnIcon = drawn;
        _assetIcon = asset;
        HeightRequest = height;

        _bg = new SKCanvasView { InputTransparent = true };
        _bg.PaintSurface += (_, a) => DrawTile(a.Surface.Canvas, a.Info, _selected);

        _iconCanvas = new SKCanvasView { InputTransparent = true, WidthRequest = 36, HeightRequest = 36 };
        _iconCanvas.PaintSurface += (_, a) =>
        {
            if (_drawnIcon is { } di) DsIcons.Draw(a.Surface.Canvas, a.Info, di);
        };
        View iconView = _iconCanvas;
        if (asset is not null)
            iconView = PksmIcons.Icon(asset, 36, PksmIcons.White);

        _label = new Label
        {
            Text = label, FontFamily = DsChrome.PixelFont, FontSize = 15, TextColor = UiTokens.Ink0,
            VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.WordWrap, MaxLines = 2,
            HorizontalTextAlignment = TextAlignment.Center,
        };

        var row = new VerticalStackLayout
        {
            Padding = new Thickness(12, 9, 12, 11), Spacing = 7, VerticalOptions = LayoutOptions.Center,
            Children = { iconView, _label },
        };

        Children.Add(_bg);
        Children.Add(row);

        var tap = new TapGestureRecognizer();
        tap.Tapped += (_, _) => Tapped?.Invoke();
        GestureRecognizers.Add(tap);
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            _bg.InvalidateSurface();
            // The Nintendo focus pop: a short spring scale, never a slow fade.
            _ = _selected
                ? this.ScaleToAsync(1.05, 130, Easing.SpringOut)
                : this.ScaleToAsync(1.0, 110, Easing.CubicOut);
        }
    }

    /// <summary>Glossy black tile; the navy + light-blue selection when chosen.</summary>
    internal static void DrawTile(SKCanvas canvas, SKImageInfo info, bool selected)
    {
        var r = new SKRect(1, 1, info.Width - 1, info.Height - 1);
        if (selected) PksmPaint.SelectedButton(canvas, r, 7);
        else PksmPaint.BlackButton(canvas, r, 7);
    }
}

/// <summary>Reusable screen chrome: continuous dark rails, hint footer, and logo-grid field.</summary>
public static class DsChrome
{
    public const string PixelFont = "PixelUI";

    /// <summary>The Gen-5 title strip translated into the logo's cobalt/cyan chrome.</summary>
    public static View TitleBar(string title = "PKForge")
    {
        var bar = new Grid
        {
            HeightRequest = 28,
            BackgroundColor = UiTokens.MaroonDeep,
            Padding = new Thickness(14, 0),
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Star)],
            Children =
            {
                new BoxView { Color = UiTokens.BagCyanEdge, HeightRequest = 2, VerticalOptions = LayoutOptions.End, InputTransparent = true },
                new Label
                {
                    Text = title, FontFamily = PixelFont, FontSize = 16, TextColor = Colors.White,
                    FontAttributes = FontAttributes.Bold, VerticalTextAlignment = TextAlignment.Center,
                    HorizontalOptions = LayoutOptions.Center, HorizontalTextAlignment = TextAlignment.Center,
                },
            },
        };
        bar.SetColumn((View)bar.Children[1], 1);
        bar.SetColumnSpan((View)bar.Children[0], 3);
        return bar;
    }

    /// <summary>Dark status strip: a title on the left, status words + a battery on the right.</summary>
    public static View StatusStrip(string left, params string[] flags)
    {
        var row = new HorizontalStackLayout { Spacing = 15, VerticalOptions = LayoutOptions.Center };
        row.Children.Add(new Label { Text = left, FontFamily = PixelFont, FontSize = 14, TextColor = UiTokens.Ink0, VerticalTextAlignment = TextAlignment.Center });
        foreach (var f in flags)
        {
            row.Children.Add(new BoxView { WidthRequest = 6, HeightRequest = 6, CornerRadius = 1, Color = UiTokens.InkSoft, VerticalOptions = LayoutOptions.Center });
            row.Children.Add(new Label { Text = f, FontFamily = PixelFont, FontSize = 13, TextColor = UiTokens.InkSoft, VerticalTextAlignment = TextAlignment.Center });
        }

        var batt = new Border
        {
            WidthRequest = 22, HeightRequest = 12, StrokeThickness = 2, Stroke = UiTokens.InkSoft,
            StrokeShape = new RoundRectangle { CornerRadius = 2 }, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center,
            Content = new BoxView { Color = UiTokens.Green, Margin = new Thickness(1, 1, 6, 1) },
        };
        var grid = new Grid
        {
            BackgroundColor = UiTokens.Paper, Padding = new Thickness(12, 4),
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { row, batt },
        };
        Grid.SetColumn(batt, 1);
        return grid;
    }

    /// <summary>Hint footer — the shared PKSM hint bar (blue key discs + pixel labels).</summary>
    public static View Footer(params (string Button, string Label, Action? OnTap)[] hints)
        => Kit.HintBar(hints);

    /// <summary>The logo's navy/cobalt grid behind every global menu body.</summary>
    public static SKCanvasView GridBackground()
    {
        var view = new SKCanvasView { InputTransparent = true };
        view.PaintSurface += (_, a) =>
        {
            var c = a.Surface.Canvas;
            PksmPaint.LogoGrid(c, new SKRect(0, 0, a.Info.Width, a.Info.Height));
        };
        return view;
    }
}
