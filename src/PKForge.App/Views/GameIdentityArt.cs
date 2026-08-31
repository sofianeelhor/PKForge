using PKForge.App.Theme;
using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// Stable PKForge identity art for every detected game. Unlike third-party cover images,
/// these marks have one scale, one language, and work for official games, ROM hacks, and
/// unknown saves alike.
/// </summary>
public sealed class GameCartridgeMark : SKCanvasView
{
    public static readonly BindableProperty GenerationProperty = BindableProperty.Create(
        nameof(Generation), typeof(int), typeof(GameCartridgeMark), 0,
        propertyChanged: (view, _, _) => ((GameCartridgeMark)view).InvalidateSurface());

    public int Generation
    {
        get => (int)GetValue(GenerationProperty);
        set => SetValue(GenerationProperty, value);
    }

    public GameCartridgeMark()
    {
        WidthRequest = 68;
        HeightRequest = 68;
        PaintSurface += Paint;
    }

    private void Paint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        var w = args.Info.Width;
        var h = args.Info.Height;
        c.Clear(SKColors.Transparent);
        var r = new SKRect(5, 4, w - 5, h - 4);
        using var shadow = new SKPaint { Color = Pksm.LogoVoid.WithAlpha(0xCC), IsAntialias = false };
        using var deck = new SKPaint { Color = Pksm.LogoDeck, IsAntialias = false };
        using var edge = new SKPaint { Color = Pksm.LogoGrid, IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        c.DrawRoundRect(new SKRect(r.Left + 3, r.Top + 3, r.Right + 3, r.Bottom + 3), 6, 6, shadow);
        c.DrawRoundRect(r, 6, 6, deck);
        c.DrawRoundRect(r, 6, 6, edge);
        using var top = new SKPaint { Color = Pksm.LogoCyan, IsAntialias = false };
        c.DrawRect(new SKRect(r.Left + 8, r.Top + 5, r.Right - 8, r.Top + 8), top);
        DrawBall(c, new SKPoint(r.MidX, r.MidY + 3), Math.Min(r.Width, r.Height) * 0.27f, Pksm.LogoBlue, Pksm.LogoCyan);
        DrawGeneration(c, Generation, r);
    }

    internal static void DrawBall(SKCanvas c, SKPoint center, float radius, SKColor body, SKColor detail)
    {
        using var fill = new SKPaint { Color = body, IsAntialias = true };
        using var line = new SKPaint { Color = detail, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2, radius * 0.22f) };
        c.DrawCircle(center, radius, fill);
        c.DrawLine(center.X - radius, center.Y, center.X + radius, center.Y, line);
        c.DrawCircle(center, radius * 0.27f, line);
    }

    private static void DrawGeneration(SKCanvas c, int generation, SKRect r)
    {
        var text = generation is >= 1 and <= 9 ? $"G{generation}" : "PK";
        using var font = new SKFont { Size = Math.Max(10, r.Height * 0.17f), Embolden = true };
        using var paint = new SKPaint { Color = Pksm.Ink, IsAntialias = false };
        c.DrawText(text, r.Right - 8, r.Bottom - 8, SKTextAlign.Right, font, paint);
    }
}

/// <summary>Second-screen game banner that guarantees every detected title has a polished hero.</summary>
public sealed class GameHeroBackdrop : Grid
{
    private readonly Image _logo;
    private readonly Label _title;
    private readonly Label _meta;

    public GameHeroBackdrop()
    {
        // Transparent SteamGridDB game logos: they sit perfectly on the square PKForge
        // grid where panoramic heroes would inevitably crop characters and backgrounds.
        _logo = new Image
        {
            Aspect = Aspect.AspectFit,
            InputTransparent = true,
            Margin = new Thickness(28, 12),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
        };
        _title = new Label
        {
            TextColor = UiTokens.Ink0,
            FontFamily = DsChrome.PixelFont,
            FontSize = 24,
            FontAttributes = FontAttributes.Bold,
            CharacterSpacing = 1,
            LineBreakMode = LineBreakMode.TailTruncation,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        _meta = new Label
        {
            TextColor = UiTokens.InkSoft,
            FontFamily = DsChrome.PixelFont,
            FontSize = 11,
            CharacterSpacing = 2,
            HorizontalTextAlignment = TextAlignment.Center,
        };
        var copy = new VerticalStackLayout
        {
            Spacing = 6,
            Padding = new Thickness(24, 0, 24, 22),
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.End,
            Children = { _title, _meta },
        };
        Children.Add(_logo);
        Children.Add(copy);
    }

    public void SetGame(DetectedSave game)
    {
        _title.Text = game.GameLabel.ToUpperInvariant();
        _meta.Text = "PKFORGE GAME LIBRARY";
        _ = LoadLogoAsync(game);
    }

    private async Task LoadLogoAsync(DetectedSave game)
    {
        try
        {
            var path = await GameArt.GetLogoAsync(game.GameLabel);
            // The selection can move while the package asset is copied to cache.
            if (_title.Text != game.GameLabel.ToUpperInvariant()) return;
            _logo.Source = path;
            _logo.IsVisible = path is not null;
        }
        catch
        {
            // The canvas still provides a coherent logo-grid scene if a package asset fails.
        }
    }

}
