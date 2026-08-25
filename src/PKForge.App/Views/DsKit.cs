using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Theme;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// A PKSM menu row: white surface, warm-grey hairline, indigo-light selected band with an
/// indigo edge and the red glove pointer — the striped-list cursor of the 3DS language.
/// Draws its own chrome in Skia; the label (and optional icon) ride on top.
/// </summary>
public sealed class DsFolderButton : Grid
{
    private readonly SKCanvasView _bg;
    private readonly Label _label;
    private readonly Label? _glyph;
    private readonly SKCanvasView? _pointer;
    private bool _selected;

    public Action? Tapped { get; set; }

    public DsFolderButton(PadOption option, double height = 50)
    {
        HeightRequest = height;
        ColumnDefinitions = [new(new GridLength(30)), new(new GridLength(30)), new(GridLength.Star)];

        _bg = new SKCanvasView { InputTransparent = true };
        _bg.PaintSurface += (_, args) => DrawRow(args.Surface.Canvas, args.Info, _selected);

        _pointer = new SKCanvasView { InputTransparent = true };
        _pointer.PaintSurface += (_, args) =>
        {
            if (!_selected) return;
            PksmPaint.Pointer(args.Surface.Canvas, new SKPoint(4, args.Info.Height * 0.18f), args.Info.Height * 0.5f);
        };

        _glyph = option.Glyph is null ? null : new Label
        {
            Text = option.Glyph,
            FontFamily = "Rounded",
            FontSize = 18,
            TextColor = option.Accent ?? UiTokens.IndigoInk,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center,
        };

        _label = new Label
        {
            Text = option.Label,
            FontFamily = DsChrome.PixelFont,
            FontSize = 15,
            TextColor = UiTokens.Ink0,
            VerticalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.TailTruncation,
        };

        Children.Add(_bg);
        Grid.SetColumnSpan(_bg, 3);
        View iconHost = _glyph is not null ? _glyph : new BoxView { InputTransparent = true, WidthRequest = 0 };
        Children.Add(iconHost);
        Children.Add(_label);
        Grid.SetColumn(_pointer, 0);
        Grid.SetColumn(iconHost, 1);
        Grid.SetColumn(_label, 2);

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
            _label.TextColor = value ? UiTokens.IndigoInk : UiTokens.Ink0;
            _bg.InvalidateSurface();
            _pointer?.InvalidateSurface();
        }
    }

    /// <summary>Draws the row chrome: white idle, indigo-light band + indigo edge when selected.</summary>
    internal static void DrawRow(SKCanvas canvas, SKImageInfo info, bool selected)
    {
        var r = new SKRect(0, 1, info.Width, info.Height - 1);
        using var fill = new SKPaint { Color = selected ? Pksm.IndigoLight : Pksm.Paper, IsAntialias = true };
        using var edge = new SKPaint { Color = selected ? Pksm.Indigo : Pksm.Chrome, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = selected ? 3 : 1.5f };
        canvas.DrawRoundRect(r, 4, 4, fill);
        canvas.DrawRoundRect(r, 4, 4, edge);
        if (selected)
        {
            using var bar = new SKPaint { Color = Pksm.Indigo };
            canvas.DrawRect(new SKRect(0, 3, 4, info.Height - 3), bar);
        }
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
/// A PKSM icon card: white tile, warm-grey border, the bundled pixel icon as hero,
/// pixel label beneath. The home/menu tile. Selection = gold ring + gentle scale.
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

    public DsCard(DsIcon icon, string label, double height = 74)
        : this((DsIcon?)icon, null, label, height) { }

    public DsCard(string pksmAssetIcon, string label, double height = 74)
        : this(null, pksmAssetIcon, label, height) { }

    private DsCard(DsIcon? drawn, string? asset, string label, double height)
    {
        _drawnIcon = drawn;
        _assetIcon = asset;
        HeightRequest = height;

        _bg = new SKCanvasView { InputTransparent = true };
        _bg.PaintSurface += (_, a) => DrawTile(a.Surface.Canvas, a.Info, _selected);

        _iconCanvas = new SKCanvasView { InputTransparent = true, WidthRequest = 32, HeightRequest = 32 };
        _iconCanvas.PaintSurface += (_, a) =>
        {
            if (_drawnIcon is { } di) DsIcons.Draw(a.Surface.Canvas, a.Info, di);
        };
        View iconView = _iconCanvas;
        if (asset is not null)
            iconView = PksmIcons.Icon(asset, 32);

        _label = new Label
        {
            Text = label, FontFamily = DsChrome.PixelFont, FontSize = 15, TextColor = UiTokens.Ink0,
            VerticalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.WordWrap, MaxLines = 2,
            HorizontalTextAlignment = TextAlignment.Center,
        };

        var row = new VerticalStackLayout
        {
            Padding = new Thickness(10, 6, 10, 8), Spacing = 5, VerticalOptions = LayoutOptions.Center,
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
        }
    }

    /// <summary>White tile with warm border; gold focus ring when selected.</summary>
    internal static void DrawTile(SKCanvas canvas, SKImageInfo info, bool selected)
    {
        var r = new SKRect(1, 1, info.Width - 1, info.Height - 1);
        using var fill = new SKPaint { Color = Pksm.Paper, IsAntialias = true };
        using var edge = new SKPaint { Color = Pksm.Chrome, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };
        canvas.DrawRoundRect(r, 6, 6, fill);
        canvas.DrawRoundRect(r, 6, 6, edge);
        if (selected)
        {
            using var gold = new SKPaint { Color = Pksm.FocusGold, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
            canvas.DrawRoundRect(SKRect.Inflate(r, 3, 3), 8, 8, gold);
        }
    }
}

/// <summary>Reusable screen chrome: maroon title strip, dark status strip, hint footer, lattice field.</summary>
public static class DsChrome
{
    public const string PixelFont = "PixelUI";

    /// <summary>The Gen-5 maroon title strip with the centered white wordmark.</summary>
    public static View TitleBar(string title = "PKForge")
    {
        var bar = new Grid
        {
            HeightRequest = 28,
            BackgroundColor = UiTokens.Maroon,
            Padding = new Thickness(14, 0),
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Star)],
            Children =
            {
                new BoxView { Color = UiTokens.MaroonDeep, HeightRequest = 2, VerticalOptions = LayoutOptions.End, InputTransparent = true },
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
        row.Children.Add(new Label { Text = left, FontFamily = PixelFont, FontSize = 14, TextColor = Color.FromArgb("#EEF2F6"), VerticalTextAlignment = TextAlignment.Center });
        foreach (var f in flags)
        {
            row.Children.Add(new BoxView { WidthRequest = 6, HeightRequest = 6, CornerRadius = 1, Color = UiTokens.Green, VerticalOptions = LayoutOptions.Center });
            row.Children.Add(new Label { Text = f, FontFamily = PixelFont, FontSize = 13, TextColor = UiTokens.Ink1, VerticalTextAlignment = TextAlignment.Center });
        }

        var batt = new Border
        {
            WidthRequest = 22, HeightRequest = 12, StrokeThickness = 2, Stroke = Color.FromArgb("#6A7682"),
            StrokeShape = new RoundRectangle { CornerRadius = 2 }, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Center,
            Content = new BoxView { Color = UiTokens.Green, Margin = new Thickness(1, 1, 6, 1) },
        };
        var grid = new Grid
        {
            BackgroundColor = Color.FromArgb("#10131A"), Padding = new Thickness(12, 4),
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto)],
            Children = { row, batt },
        };
        Grid.SetColumn(batt, 1);
        return grid;
    }

    /// <summary>Hint footer — the shared PKSM hint bar (blue key discs + pixel labels).</summary>
    public static View Footer(params (string Button, string Label, Action? OnTap)[] hints)
        => Kit.HintBar(hints);

    /// <summary>The faint indigo dot-lattice field behind menu bodies (the PC-box paper).</summary>
    public static SKCanvasView GridBackground()
    {
        var view = new SKCanvasView { InputTransparent = true };
        view.PaintSurface += (_, a) =>
        {
            var c = a.Surface.Canvas;
            c.Clear(new SKColor(0xE9, 0xEC, 0xF6));
            using var dot = new SKPaint { Color = new SKColor(0x1A, 0x23, 0x7E, 0x12) };
            for (var y = 6f; y < a.Info.Height; y += 12)
                for (var x = 6f; x < a.Info.Width; x += 12)
                    c.DrawRect(x, y, x + 2, y + 2, dot);
        };
        return view;
    }
}
