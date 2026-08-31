using System.ComponentModel;
using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The Thor's second screen: a Pokémon summary that follows the box cursor - modern
/// HOME render (tap it: it hops), name, gender, dex number, level, type badges,
/// legality verdict. Hero art of the highlighted game on the home shelf; idle branding otherwise.
/// </summary>
public sealed class SecondScreenBoxPage : ContentPage
{
    /// <summary>English type names by PKHeX type id (stable across generations).</summary>
    private static readonly string[] TypeNames =
    [
        "Normal", "Fighting", "Flying", "Poison", "Ground", "Rock", "Bug", "Ghost", "Steel",
        "Fire", "Water", "Grass", "Electric", "Psychic", "Ice", "Dragon", "Dark", "Fairy",
    ];

    private readonly BoxBrowserViewModel _viewModel;
    private readonly ISpriteService _sprites;
    private readonly SKCanvasView _sprite;
    private readonly PropertyChangedEventHandler _viewModelHandler;

    private readonly Label _name = null!; // the maroon header strip's label, captured in the ctor
    private readonly Image _gender = new() { WidthRequest = 24, HeightRequest = 24, VerticalOptions = LayoutOptions.Center, IsVisible = false };
    private readonly Label _facts = new() { TextColor = UiTokens.Ink0, FontSize = 15 };
    private readonly HorizontalStackLayout _typeBadges = new() { Spacing = 8 };
    private readonly Image _shinyMark = new()
    {
        Source = PksmIcons.Source("shiny", PksmIcons.Indigo),
        WidthRequest = 18,
        HeightRequest = 18,
        VerticalOptions = LayoutOptions.Center,
        IsVisible = false,
    };
    private readonly Label _badge = new() { FontFamily = DsChrome.PixelFont, FontSize = 15, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.End, VerticalTextAlignment = TextAlignment.Center };

    private SKCanvasView _statRadar = null!;
    private float _bounce;        // current hop offset in fractions of sprite height
    private int _bounceTicks = -1; // -1 = not bouncing
    private long _animElapsedMs;
    private IDispatcherTimer? _animTimer;

    public SecondScreenBoxPage(BoxBrowserViewModel viewModel, ISpriteService sprites, ThemeService theme)
    {
        _viewModel = viewModel;
        _sprites = sprites;
        BackgroundColor = UiTokens.Housing;
        _sprite = new SKCanvasView { EnableTouchEvents = true };

        _sprite.PaintSurface += PaintSprite;
        _sprite.Touch += (_, args) =>
        {
            if (args.ActionType == SKTouchAction.Pressed) { args.Handled = true; return; }
            if (args.ActionType != SKTouchAction.Released) return;
            args.Handled = true;
            StartBounce();
        };
        // The mon name rides the maroon Gen-5 header strip; gender icon and the
        // legality verdict sit beside it - the verdict must never scroll or clip away.
        var nameHeader = (Border)Kit.HeaderBar("Pokémon");
        _name = (Label)nameHeader.Content!;

        var header = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Auto), new(GridLength.Auto)],
            Children = { nameHeader, _gender, _badge },
        };
        Grid.SetColumn(_gender, 1);
        Grid.SetColumn(_badge, 2);

        _statRadar = new SKCanvasView { HeightRequest = 160, HorizontalOptions = LayoutOptions.Fill };
        _statRadar.PaintSurface += PaintRadar;

        // The stat radar sits on its own inset white panel (paper + chrome border).
        var radarPanel = new Border
        {
            BackgroundColor = UiTokens.Paper,
            Stroke = UiTokens.ShellEdge,
            StrokeThickness = 2,
            StrokeShape = new RoundRectangle { CornerRadius = 6 },
            Padding = new Thickness(8, 8, 8, 2),
            Content = _statRadar,
        };

        var factsRow = new HorizontalStackLayout { Spacing = 8, Children = { _facts, _shinyMark } };

        var factsPanel = Kit.DevicePanel(new VerticalStackLayout
        {
            Spacing = 8,
            VerticalOptions = LayoutOptions.Center,
            Children = { header, factsRow, _typeBadges, radarPanel },
        }, padding: 16);
        factsPanel.VerticalOptions = LayoutOptions.Center;

        // The Gen-6 summary surface: a light-blue world carrying white panels.
        var summary = new Grid
        {
            BackgroundColor = UiTokens.SummaryBg,
            Padding = 20,
            ColumnSpacing = 16,
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
            Children = { Kit.LcdPanel(_sprite, padding: 8), factsPanel },
        };
        Grid.SetColumn(factsPanel, 1);

        // A purpose-built game banner replaces inconsistent third-party hero art and covers every title.
        var hero = new GameHeroBackdrop { IsVisible = false };

        var idle = new VerticalStackLayout
        {
            Spacing = 6,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                PksmIcons.Icon("storage", 64),
                new Label { Text = "PKFORGE", TextColor = UiTokens.Ink0, FontSize = 22, FontAttributes = FontAttributes.Bold, CharacterSpacing = 4, HorizontalTextAlignment = TextAlignment.Center },
                new Label { Text = "POKéMON STORAGE SYSTEM", TextColor = UiTokens.Ink1, FontSize = 11, CharacterSpacing = 2, HorizontalTextAlignment = TextAlignment.Center },
            },
        };

        var state = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        var dex = BuildDexView();

        async void SwapAsync()
        {
            var detail = _viewModel.Selected;
            // The Pokédex preview outranks everything while the picker is open.
            var showDex = state?.PreviewSpecies is not null;
            var showSummary = !showDex && detail is { IsEmpty: false };
            if (showDex) UpdateDex(state!.PreviewSpecies!.Value);
            if (showSummary) UpdateSummary();

            var preview = state?.PreviewGame;
            var showHero = !showDex && !showSummary && preview is not null;

            dex.IsVisible = showDex;
            summary.IsVisible = showSummary;
            _summaryVisible = showSummary;
            _dexVisible = showDex;
            SetAnimating(showSummary || showDex);
            hero.IsVisible = showHero;
            if (showHero) hero.SetGame(preview!);
            idle.IsVisible = !dex.IsVisible && !summary.IsVisible && !hero.IsVisible;
        }

        Content = new Grid { Children = { DsChrome.GridBackground(), hero, summary, dex, idle } };
        SwapAsync();

        _viewModelHandler = (_, args) =>
        {
            if (args.PropertyName is nameof(BoxBrowserViewModel.Selected) or nameof(BoxBrowserViewModel.LegalityBadge))
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    SwapAsync();
                    _sprite.InvalidateSurface();
                    _statRadar.InvalidateSurface();
                });
        };
        _viewModel.PropertyChanged += _viewModelHandler;
        if (state is not null)
            state.PropertyChanged += (_, _) => MainThread.BeginInvokeOnMainThread(SwapAsync);
    }

    /// <summary>Detach from the shared view model before the presentation is discarded.</summary>
    public void Cleanup()
    {
        _viewModel.PropertyChanged -= _viewModelHandler;
        SetAnimating(false);
    }

    // Which view is showing, and whether the sprite it shows is an animated GIF (vs a
    // static HOME/pixel render). The timer only repaints a canvas that is both visible AND
    // animated - a still sprite was being redrawn 25x/sec for nothing.
    private bool _summaryVisible, _dexVisible, _spriteAnimated, _dexAnimated;

    /// <summary>The GIF loop only ticks while an animated Pokémon is actually on screen.</summary>
    private void SetAnimating(bool on)
    {
        if (!on)
        {
            _animTimer?.Stop();
            _animTimer = null;
            return;
        }
        if (_animTimer is not null) return;
        _animTimer = Dispatcher.CreateTimer();
        _animTimer.Interval = TimeSpan.FromMilliseconds(40);
        _animTimer.Tick += (_, _) =>
        {
            _animElapsedMs += 40;
            if (_summaryVisible && _spriteAnimated) _sprite.InvalidateSurface();
            if (_dexVisible && _dexAnimated) _dexSprite.InvalidateSurface();
        };
        _animTimer.Start();
    }

    /// <summary>All facts set directly from the model - no fragile nested bindings, no ghost labels.</summary>
    private void UpdateSummary()
    {
        var detail = _viewModel.Selected;
        if (detail is null || detail.IsEmpty) return;

        _name.Text = detail.Nickname is { Length: > 0 } nick ? nick : $"#{detail.Species}";
        _gender.Source = detail.Gender switch
        {
            0 => PksmIcons.Source("male", PksmIcons.Indigo),
            1 => PksmIcons.Source("female", PksmIcons.Indigo),
            _ => null,
        };
        _gender.IsVisible = detail.Gender is 0 or 1;
        _facts.Text = $"No. {detail.Species:000}   Lv. {detail.Level}";
        _shinyMark.IsVisible = detail.IsShiny;

        _typeBadges.Children.Clear();
        foreach (var type in detail.Types ?? [])
        {
            var typeName = (uint)type < (uint)TypeNames.Length ? TypeNames[type] : $"?{type}";
            _typeBadges.Children.Add(new Border
            {
                BackgroundColor = TypePalette.ForType(type),
                StrokeThickness = 0,
                StrokeShape = new RoundRectangle { CornerRadius = 6 },
                Padding = new Thickness(12, 3),
                Content = new Label
                {
                    Text = typeName.ToUpperInvariant(),
                    TextColor = Colors.White,
                    FontSize = 12,
                    FontAttributes = FontAttributes.Bold,
                    CharacterSpacing = 1,
                },
            });
        }

        _badge.Text = _viewModel.LegalityBadge switch
        {
            "✓" => "✓ LEGAL",
            "✗" => "✗ NOT LEGAL",
            _ => "",
        };
        _badge.TextColor = _viewModel.LegalityBadge == "✓" ? UiTokens.Ok : UiTokens.Bad;
    }

    private static readonly string[] StatAxes = ["HP", "ATK", "DEF", "SPA", "SPD", "SPE"];

    /// <summary>
    /// The mon's final battle stats as a six-point radar (spider) chart - HP top, then
    /// clockwise. The largest stat reaches the outer ring so the build's shape reads at a
    /// glance; the exact numbers sit at each vertex. Base-stat outline shows the raw frame.
    /// </summary>
    private void PaintRadar(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var detail = _viewModel.Selected;
        if (detail?.Stats is not { Count: 6 } stats) return;

        var info = args.Info;
        var cx = info.Width / 2f;
        var cy = info.Height / 2f;
        var radius = Math.Min(info.Width, info.Height) * 0.26f;
        var max = Math.Max(1, stats.Max());

        SKPoint Vertex(int i, float r)
        {
            var angle = (float)(-Math.PI / 2 + i * Math.PI / 3); // -90° + 60°·i, clockwise
            return new SKPoint(cx + r * (float)Math.Cos(angle), cy + r * (float)Math.Sin(angle));
        }

        // Grid rings + spokes.
        using var grid = new SKPaint { Color = UiTokens.SkLcdTileEdge.WithAlpha(0x66), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        for (var ring = 1; ring <= 4; ring++)
        {
            using var ringPath = new SKPath();
            for (var i = 0; i < 6; i++)
            {
                var p = Vertex(i, radius * ring / 4f);
                if (i == 0) ringPath.MoveTo(p); else ringPath.LineTo(p);
            }
            ringPath.Close();
            canvas.DrawPath(ringPath, grid);
        }
        for (var i = 0; i < 6; i++)
            canvas.DrawLine(cx, cy, Vertex(i, radius).X, Vertex(i, radius).Y, grid);

        // The stat polygon.
        using var fill = new SKPaint { Color = Pksm.IndigoLight.WithAlpha(0x66), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var edge = new SKPaint { Color = Pksm.Indigo, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, IsAntialias = true };
        using var dot = new SKPaint { Color = Pksm.IndigoDeep, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var shape = new SKPath();
        for (var i = 0; i < 6; i++)
        {
            var p = Vertex(i, radius * stats[i] / max);
            if (i == 0) shape.MoveTo(p); else shape.LineTo(p);
        }
        shape.Close();
        canvas.DrawPath(shape, fill);
        canvas.DrawPath(shape, edge);
        for (var i = 0; i < 6; i++)
        {
            var p = Vertex(i, radius * stats[i] / max);
            canvas.DrawCircle(p.X, p.Y, 3f, dot);
        }

        // Axis captions + values just outside each vertex.
        using var capFont = new SKFont { Size = 13f, Edging = SKFontEdging.Antialias, Embolden = true };
        using var valFont = new SKFont { Size = 15f, Edging = SKFontEdging.Antialias, Embolden = true };
        using var capPaint = new SKPaint { Color = UiTokens.SkLcdText.WithAlpha(0xB0), IsAntialias = true };
        using var valPaint = new SKPaint { Color = UiTokens.SkLcdText, IsAntialias = true };
        for (var i = 0; i < 6; i++)
        {
            var label = Vertex(i, radius + 15f);
            var align = Math.Abs(label.X - cx) < 4 ? SKTextAlign.Center : label.X < cx ? SKTextAlign.Right : SKTextAlign.Left;
            canvas.DrawText(StatAxes[i], label.X, label.Y - 2, align, capFont, capPaint);
            canvas.DrawText(stats[i].ToString(), label.X, label.Y + 12, align, valFont, valPaint);
        }
    }

    // ── The logo-deck Pokédex view (species preview while the picker is open) ──

    private static readonly string[] RomanGens = ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX"];
    private static readonly string[] RegionNames = ["Kanto", "Johto", "Hoenn", "Sinnoh", "Unova", "Kalos", "Alola", "Galar", "Paldea"];
    private static readonly (int First, int Last)[] GenBounds =
        [(1, 151), (152, 251), (252, 386), (387, 493), (494, 649), (650, 721), (722, 809), (810, 905), (906, 1025)];

    private SKCanvasView _dexSprite = null!;
    private int _dexSpecies;
    private readonly Label _dexName = new() { TextColor = UiTokens.Ink0, FontSize = 24, FontAttributes = FontAttributes.Bold, CharacterSpacing = 1 };
    private readonly Label _dexNumber = new() { TextColor = UiTokens.InkSoft, FontSize = 13 };
    private readonly Label _dexOrigin = new() { TextColor = UiTokens.InkSoft, FontSize = 12 };
    private readonly HorizontalStackLayout _dexTypes = new() { Spacing = 6 };
    private readonly ProgressBar[] _dexStatBars = new ProgressBar[6];
    private readonly Label[] _dexStatValues = new Label[6];

    /// <summary>The handheld dex translated into the logo's cobalt hardware language.</summary>
    private View BuildDexView()
    {
        _dexSprite = new SKCanvasView();
        _dexSprite.PaintSurface += PaintDexSprite;

        // Top-left hardware charm: the blue lens and three status LEDs.
        var lens = new Ellipse { WidthRequest = 26, HeightRequest = 26, Fill = new SolidColorBrush(UiTokens.BagCyanEdge), Stroke = new SolidColorBrush(UiTokens.SelectBorder), StrokeThickness = 2 };
        var leds = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children = { lens, Kit.StatusLight(Color.FromArgb("#E4514F"), 8), Kit.StatusLight(UiTokens.Yellow, 8), Kit.StatusLight(UiTokens.Green, 8) },
        };

        var screen = new Border
        {
            BackgroundColor = UiTokens.ShellPress,
            Stroke = UiTokens.SelectBorder,
            StrokeThickness = 3,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Padding = 6,
            Content = _dexSprite,
        };

        var statNames = new[] { "HP", "ATK", "DEF", "SPA", "SPD", "SPE" };
        var statsGrid = new Grid { RowSpacing = 3, ColumnSpacing = 8 };
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(34)));
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        statsGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(30)));
        for (var i = 0; i < 6; i++)
        {
            statsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var caption = new Label { Text = statNames[i], TextColor = UiTokens.InkSoft, FontSize = 10, FontAttributes = FontAttributes.Bold };
            _dexStatBars[i] = new ProgressBar { ProgressColor = UiTokens.BagCyanEdge, BackgroundColor = UiTokens.ShellPress, VerticalOptions = LayoutOptions.Center };
            _dexStatValues[i] = new Label { TextColor = UiTokens.Ink0, FontSize = 10, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.End };
            statsGrid.Add(caption); Grid.SetRow(caption, i);
            statsGrid.Add(_dexStatBars[i]); Grid.SetRow(_dexStatBars[i], i); Grid.SetColumn(_dexStatBars[i], 1);
            statsGrid.Add(_dexStatValues[i]); Grid.SetRow(_dexStatValues[i], i); Grid.SetColumn(_dexStatValues[i], 2);
        }

        var info = new Border
        {
            BackgroundColor = UiTokens.ShellPress,
            Stroke = UiTokens.SelectBorder,
            StrokeThickness = 3,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            Padding = 12,
            Content = new VerticalStackLayout
            {
                Spacing = 7,
                Children = { _dexName, _dexNumber, _dexTypes, _dexOrigin, statsGrid },
            },
        };

        var body = new Grid
        {
            RowSpacing = 8,
            ColumnSpacing = 12,
            RowDefinitions = [new(GridLength.Auto), new(GridLength.Star)],
            ColumnDefinitions = [new(GridLength.Star), new(GridLength.Star)],
            Children = { leds, screen, info },
        };
        Grid.SetRow(screen, 1);
        Grid.SetRow(info, 1);
        Grid.SetColumn(info, 1);

        var shell = new Border
        {
            BackgroundColor = UiTokens.Maroon,
            Stroke = UiTokens.BagCyanEdge,
            StrokeThickness = 3,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            Padding = 14,
            Margin = new Thickness(18, 12),
            Content = body,
        };
        return new Grid { IsVisible = false, Children = { shell } };
    }

    private void UpdateDex(int species)
    {
        _dexSpecies = species;
        var services = IPlatformApplication.Current?.Services;
        var data = services?.GetService<Domain.IGameDataService>();
        var session = services?.GetService<Domain.ISaveSessionService>()?.CurrentSession;

        _dexName.Text = data is not null && species < data.SpeciesNames.Count ? data.SpeciesNames[species] : $"#{species}";
        _dexNumber.Text = $"No. {species:000}";

        var genIndex = Array.FindIndex(GenBounds, b => species >= b.First && species <= b.Last);
        _dexOrigin.Text = genIndex >= 0 ? $"First seen in Generation {RomanGens[genIndex]} · {RegionNames[genIndex]}" : "";

        _dexTypes.Children.Clear();
        if (session is not null)
        {
            foreach (var type in session.GetSpeciesTypes(species))
            {
                var typeName = (uint)type < (uint)TypeNames.Length ? TypeNames[type] : $"?{type}";
                _dexTypes.Children.Add(new Border
                {
                    BackgroundColor = TypePalette.ForType(type),
                    StrokeThickness = 0,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 5 },
                    Padding = new Thickness(9, 2),
                    Content = new Label { Text = typeName.ToUpperInvariant(), TextColor = Colors.White, FontSize = 10, FontAttributes = FontAttributes.Bold },
                });
            }

            var stats = session.GetBaseStats(species);
            var values = new[] { stats.Hp, stats.Atk, stats.Def, stats.SpA, stats.SpD, stats.Spe };
            for (var i = 0; i < 6; i++)
            {
                _dexStatBars[i].Progress = Math.Min(1.0, values[i] / 180.0);
                _dexStatValues[i].Text = values[i].ToString();
            }
        }
        _dexSprite.InvalidateSurface();
    }

    private void PaintDexSprite(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(Pksm.PaperShade);
        _dexAnimated = false;
        if (_dexSpecies <= 0) return;

        if (!_sprites.TryGetShowdown(_dexSpecies, false, out var animated))
        {
            _sprites.WarmShowdown(_dexSpecies, false, () => MainThread.BeginInvokeOnMainThread(_dexSprite.InvalidateSurface));
            return;
        }
        _dexAnimated = animated is not null;
        var bitmap = animated?.FrameAt(_animElapsedMs) ?? _sprites.GetSprite(_dexSpecies, 0, false);
        if (bitmap is null)
        {
            _sprites.Warm(_dexSpecies, 0, false, () => MainThread.BeginInvokeOnMainThread(_dexSprite.InvalidateSurface));
            return;
        }

        var info = args.Info;
        var box = Math.Min(info.Width, info.Height) * 0.8f;
        var scale = Math.Min(box / bitmap.Width, box / bitmap.Height);
        var w = bitmap.Width * scale;
        var h = bitmap.Height * scale;
        var dest = new SKRect(info.Width / 2f - w / 2, info.Height / 2f - h / 2, info.Width / 2f + w / 2, info.Height / 2f + h / 2);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
    }

    /// <summary>Tap response: a happy little hop.</summary>
    private void StartBounce()
    {
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); }
        catch { }
        if (_bounceTicks >= 0) return; // already hopping
        _bounceTicks = 0;
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(30);
        timer.Tick += (_, _) =>
        {
            _bounceTicks++;
            // Two quick decaying hops over ~0.6s.
            var progress = _bounceTicks / 20f;
            _bounce = (float)(Math.Abs(Math.Sin(progress * Math.PI * 2)) * (1 - progress) * 0.12);
            _sprite.InvalidateSurface();
            if (_bounceTicks >= 20)
            {
                _bounce = 0;
                _bounceTicks = -1;
                timer.Stop();
                _sprite.InvalidateSurface();
            }
        };
        timer.Start();
    }

    private void PaintSprite(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(Pksm.SummaryStripe);
        _spriteAnimated = false;
        var detail = _viewModel.Selected;
        if (detail is null || detail.IsEmpty) return;

        // Preference order: animated Showdown sprite → HOME render → crisp pixel sprite.
        // While the animation's availability is UNKNOWN we draw nothing at all -
        // an empty beat is calm; a 2D sprite flashing into a GIF is a bug.
        if (!_sprites.TryGetShowdown(detail.Species, detail.IsShiny, out var animated))
        {
            _sprites.WarmShowdown(detail.Species, detail.IsShiny, () => MainThread.BeginInvokeOnMainThread(_sprite.InvalidateSurface));
            return;
        }
        _spriteAnimated = animated is not null;
        var home = animated is null ? _sprites.GetHome(detail.Species, detail.IsShiny) : null;
        if (animated is null && home is null)
            _sprites.WarmHome(detail.Species, detail.IsShiny, () => MainThread.BeginInvokeOnMainThread(_sprite.InvalidateSurface));

        var bitmap = animated?.FrameAt(_animElapsedMs) ?? home ?? _sprites.GetSprite(detail.Species, detail.Form, detail.IsShiny);
        if (bitmap is null)
        {
            _sprites.Warm(detail.Species, detail.Form, detail.IsShiny, () => MainThread.BeginInvokeOnMainThread(_sprite.InvalidateSurface));
            return;
        }

        var info = args.Info;
        // Animated battle sprites are small pixel-art: cap their upscale so they stay clean.
        var box = Math.Min(info.Width, info.Height) * (animated is not null ? 0.72f : 0.86f);
        var scale = Math.Min(box / bitmap.Width, box / bitmap.Height);
        var w = bitmap.Width * scale;
        var h = bitmap.Height * scale;
        var hop = _bounce * h;
        var dest = new SKRect(info.Width / 2f - w / 2, info.Height / 2f - h / 2 - hop, info.Width / 2f + w / 2, info.Height / 2f + h / 2 - hop);
        using var image = SKImage.FromBitmap(bitmap);
        // HOME renders are smooth art: linear scaling. Animated + pixel stay nearest for crisp pixels.
        var sampling = home is not null
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
            : new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        canvas.DrawImage(image, dest, sampling);

        // The mon's ball, top-left, like the in-game summary page.
        var ballBitmap = _sprites.GetBall(detail.Ball);
        if (ballBitmap is null)
        {
            _sprites.WarmBall(detail.Ball, () => MainThread.BeginInvokeOnMainThread(_sprite.InvalidateSurface));
        }
        else
        {
            var ballSize = Math.Min(info.Width, info.Height) * 0.15f;
            using var ballImage = SKImage.FromBitmap(ballBitmap);
            canvas.DrawImage(ballImage, new SKRect(10, 10, 10 + ballSize, 10 + ballSize),
                new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }
    }
}
