using System.ComponentModel;
using Microsoft.Maui.Controls.Shapes;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The Thor's second screen, a pure function of <see cref="SecondScreenState.Owner"/>:
/// the Pokémon inspector (the shared <see cref="MonSummaryView"/>) following the box or
/// Bank cursor, with its INFO / STATS / MOVES / ORIGIN / LEGAL pages turned by the tabs
/// here or SELECT in the Bank; the box overview while the full-screen summary shows the
/// details on top; the Pokédex picker's species; the Poképark journal; hero art of the
/// home shelf's highlighted game; idle branding when nobody claims it.
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
    private readonly PropertyChangedEventHandler _viewModelHandler;
    private readonly SecondScreenState? _secondScreenState;
    private readonly PokeparkJournalState? _journalState;
    private readonly PropertyChangedEventHandler? _secondScreenHandler;
    private readonly PropertyChangedEventHandler? _journalHandler;
    private readonly MonSummaryView _inspector;
    private Func<Task>? _swapAsync;
    private bool _cleanedUp;

    private long _animElapsedMs;
    private IDispatcherTimer? _animTimer;

    public SecondScreenBoxPage(BoxBrowserViewModel viewModel, ISpriteService sprites, ThemeService theme)
    {
        _viewModel = viewModel;
        _sprites = sprites;
        BackgroundColor = UiTokens.Housing;

        var state = _secondScreenState = IPlatformApplication.Current?.Services.GetService<SecondScreenState>();
        var services = IPlatformApplication.Current?.Services;
        var summaries = services?.GetService<Domain.IMonSummaryService>();
        var sessions = services?.GetService<Domain.ISaveSessionService>();

        // The inspector: the Gen-6 summary surface, a light-blue world carrying white panels.
        _inspector = new MonSummaryView(sprites);
        if (state is not null)
        {
            _inspector.SetPage(state.InspectorPage);
            _inspector.PageChanged += page => state.InspectorPage = page;
        }
        var summary = new Grid
        {
            Padding = new Thickness(14, 12),
            Children = { _inspector },
        };

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

        var journalState = _journalState = services?.GetService<PokeparkJournalState>();
        var journal = BuildPokeparkJournal(journalState, out _journalHandler);
        var dex = BuildDexView();
        var overview = BuildOverview();
        // The Living Dex Autopilot's route map: cartridges and the Pokémon travelling between them.
        var routeMap = new LivingDexRouteMap(sprites, compact: false) { Margin = new Thickness(14, 12) };
        routeMap.IsVisible = false;

        // The box cursor's mon, decoded from the live session OFF the UI thread (latest
        // request wins) and cached per (session, edit generation, slot), so a cursor sweep
        // never decodes on the UI thread and a revisit is one repaint. Legality comes from
        // the view model's own verdict (sweep cache or one-slot analysis), never recomputed.
        var boxCache = new Dictionary<(Domain.ISaveEngineSession, long, int, int), Domain.MonSummary?>();
        var boxSequence = 0;
        Domain.MonSummary? WithVerdict(Domain.MonSummary? built, Domain.ISaveEngineSession session, out bool pending)
        {
            pending = false;
            if (built is null) return null;
            switch (_viewModel.LegalityBadge)
            {
                case "✓" or "✗":
                    return built.WithLegality(_viewModel.LegalityBadge == "✓",
                        (_viewModel.LegalityText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries));
                default:
                    pending = session.SupportsLegalityAnalysis;
                    return built;
            }
        }
        async void ShowBoxSummary(Domain.EntityDetail detail)
        {
            var session = sessions?.CurrentSession;
            var box = detail.Box == -1 ? "PARTY" : $"BOX {detail.Box + 1:00}";
            var caption = $"{box} · SLOT {detail.Slot + 1:00}";
            if (session is null || summaries is null) { _inspector.Show(null, caption: caption); return; }
            var key = (session, _viewModel.MutationGeneration, detail.Box, detail.Slot);
            var sequence = ++boxSequence;
            if (!boxCache.TryGetValue(key, out var built))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                try { built = await Task.Run(() => summaries.Build(session, detail.Box, detail.Slot)); }
                catch (Exception) { built = null; }
                PerfTrace.Log("second.box-build", watch);
                if (boxCache.Count > 64) boxCache.Clear();
                boxCache[key] = built;
                // A newer cursor position (or another surface) took over meanwhile.
                if (sequence != boxSequence || _viewModel.Selected != detail) return;
            }
            var shown = WithVerdict(built, session, out var pending);
            _inspector.Show(shown, pending, caption);
        }

        // The lower screen is a pure function of the top claim (SecondScreenState.Routes):
        // each owner reads only its own payload, so leaving a surface can never leave its
        // content behind.
        async void SwapAsync()
        {
            var swapWatch = System.Diagnostics.Stopwatch.StartNew();
            var owner = state?.Owner ?? SecondScreenOwner.Box;
            var detail = _viewModel.Selected;
            var species = state?.PreviewSpecies;
            var preview = state?.PreviewGame;
            var showJournal = owner == SecondScreenOwner.Pokepark;
            var showDex = owner == SecondScreenOwner.Pokedex && species is not null;
            var showOverview = owner == SecondScreenOwner.Summary && state?.Overview is not null;
            var bankDriven = owner == SecondScreenOwner.Bank;
            var showSummary = bankDriven || (owner == SecondScreenOwner.Box && detail is { IsEmpty: false });
            var showHero = owner == SecondScreenOwner.Home && preview is not null;
            var showRoute = owner == SecondScreenOwner.Autopilot && state?.AutopilotRoute is not null;

            boxSequence++; // any in-flight box decode is now stale
            if (showDex) UpdateDex(species!.Value);
            if (showOverview) { _overview = state!.Overview; _overviewCanvas.InvalidateSurface(); }
            if (showSummary)
            {
                if (bankDriven)
                    _inspector.Show(state!.Inspected?.Summary, state.Inspected?.LegalityPending == true, state.Inspected?.Caption, "EMPTY BANK SLOT");
                else
                    ShowBoxSummary(detail!);
            }
            if (showHero) hero.SetGame(preview!);

            journal.IsVisible = showJournal;
            dex.IsVisible = showDex;
            overview.IsVisible = showOverview;
            summary.IsVisible = showSummary;
            hero.IsVisible = showHero;
            if (showRoute) routeMap.Show(state!.AutopilotRoute);
            routeMap.IsVisible = showRoute;
            idle.IsVisible = !showJournal && !showDex && !showOverview && !showSummary && !showHero && !showRoute;
            _dexVisible = showDex;
            SetAnimating(showDex);
            PerfTrace.Log("second.swap", swapWatch);
        }

        _swapAsync = () =>
        {
            SwapAsync();
            return Task.CompletedTask;
        };
        Content = new Grid { Children = { DsChrome.GridBackground(), hero, summary, overview, journal, dex, routeMap, idle } };
        SwapAsync();

        // Selected, LegalityBadge and friends change together on a cursor move: coalesce
        // them into one swap on the next dispatcher turn instead of one per property.
        var swapQueued = false;
        void QueueSwap()
        {
            if (swapQueued) return;
            swapQueued = true;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                swapQueued = false;
                if (!_cleanedUp) SwapAsync();
            });
        }
        _viewModelHandler = (_, args) =>
        {
            if (state is not null && state.Owner != SecondScreenOwner.Box) return; // the cursor is not in front
            if (args.PropertyName is nameof(BoxBrowserViewModel.Selected) or nameof(BoxBrowserViewModel.LegalityBadge))
                QueueSwap();
        };
        _viewModel.PropertyChanged += _viewModelHandler;
        if (state is not null)
        {
            _secondScreenHandler = (_, args) =>
            {
                if (args.PropertyName == nameof(SecondScreenState.AutopilotRoute) && state.Owner == SecondScreenOwner.Autopilot && routeMap.IsVisible)
                    // Every dry-run step publishes a route: feed the map directly, no full swap.
                    MainThread.BeginInvokeOnMainThread(() => routeMap.Show(state.AutopilotRoute));
                else if (args.PropertyName == nameof(SecondScreenState.InspectorPage))
                    MainThread.BeginInvokeOnMainThread(() => _inspector.SetPage(state.InspectorPage));
                else QueueSwap();
            };
            state.PropertyChanged += _secondScreenHandler;
        }
    }

    public ValueTask RefreshPokeparkJournalAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_cleanedUp) return ValueTask.CompletedTask;
        MainThread.BeginInvokeOnMainThread(() => _swapAsync?.Invoke());
        return ValueTask.CompletedTask;
    }

    private View BuildPokeparkJournal(PokeparkJournalState? state, out PropertyChangedEventHandler? handler)
    {
        var title = new Label { Text = "POKÉPARK  /  FIELD JOURNAL", FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = UiTokens.Ink0 };
        var name = new Label { FontSize = 28, FontAttributes = FontAttributes.Bold, TextColor = UiTokens.IndigoInk };
        var mood = new Label { FontSize = 15, TextColor = UiTokens.Ink1 };
        var activity = new Label { FontSize = 18, TextColor = UiTokens.Ink0 };
        var journal = new Label { FontSize = 16, TextColor = UiTokens.Ink0, LineBreakMode = LineBreakMode.WordWrap };
        var likes = new Label { FontSize = 16, TextColor = UiTokens.Ink0, LineBreakMode = LineBreakMode.WordWrap };
        var card = new Border { BackgroundColor = UiTokens.Paper, Stroke = UiTokens.ShellEdge, StrokeThickness = 2, StrokeShape = new RoundRectangle { CornerRadius = 12 }, Padding = 18, Margin = 14,
            Content = new VerticalStackLayout { Spacing = 10, Children = { title, name, mood, new BoxView { HeightRequest = 2, Color = UiTokens.SelectBorder }, activity, journal, likes, new Label { Text = "This journal is a playful Poképark story. Game data stays unchanged.", FontSize = 12, TextColor = UiTokens.InkSoft } } } };
        void Update() { var m = state?.Resident; name.Text = m is null ? "Poképark" : (m.Name + (m.Shiny ? " ★" : "")); mood.Text = m is null ? "" : $"Mood: {state!.Mood}"; activity.Text = m is null ? "" : $"Right now: {state!.Activity}"; journal.Text = m is null ? "" : $"PERSONALITY  {state!.Trait}\n\nMEADOW MEMORY  {state!.Story}"; likes.Text = m is null ? "" : $"FAVORITE LITTLE THINGS  {state!.Likes}"; }
        handler = state is null ? null : (_, _) => MainThread.BeginInvokeOnMainThread(() =>
        {
            Update();
            _swapAsync?.Invoke();
        });
        if (handler is not null) state!.PropertyChanged += handler;
        Update();
        return card;
    }

    /// <summary>Detach from the shared view model before the presentation is discarded.</summary>
    public void Cleanup()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        _viewModel.PropertyChanged -= _viewModelHandler;
        if (_secondScreenState is not null && _secondScreenHandler is not null)
            _secondScreenState.PropertyChanged -= _secondScreenHandler;
        if (_journalState is not null && _journalHandler is not null)
            _journalState.PropertyChanged -= _journalHandler;
        _swapAsync = null;
        SetAnimating(false);
    }


    // Whether the dex view is showing, and whether its sprite is an animated GIF (vs a
    // static render): the timer only repaints a canvas that is both visible AND animated.
    // The inspector runs its own loop.
    private bool _dexVisible, _dexAnimated;

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
            if (_dexVisible && _dexAnimated) _dexSprite.InvalidateSurface();
        };
        _animTimer.Start();
    }

    // ── The box overview (while the full-screen summary owns the details) ──

    private SKCanvasView _overviewCanvas = null!;
    private SummaryOverview? _overview;

    /// <summary>The walked box as the PKSM grid, the viewed mon under the red hand: where you
    /// are in the box, not the details the top screen already carries.</summary>
    private View BuildOverview()
    {
        _overviewCanvas = new SKCanvasView { IsVisible = true };
        _overviewCanvas.PaintSurface += PaintOverview;
        return new Grid { IsVisible = false, Padding = new Thickness(14, 12), Children = { _overviewCanvas } };
    }

    private void PaintOverview(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_overview is not { } o) return;
        var info = args.Info;
        var unit = info.Width / 360f;
        using var font = new SKFont(PixelFont.Face, 13 * unit) { Edging = SKFontEdging.Antialias, Embolden = true };
        using var small = new SKFont(PixelFont.Face, 11 * unit) { Edging = SKFontEdging.Antialias };

        var bar = new SKRect(0, 0, info.Width, 30 * unit);
        var label = o.Total > 0 ? $"{o.Context} · {o.Position} / {o.Total}" : o.Context;
        PksmPaint.BoxNameBar(canvas, bar, label, font, false, false);

        var hintHeight = 24 * unit;
        var gridArea = new SKRect(0, bar.Bottom + 8 * unit, info.Width, info.Height - hintHeight - 6 * unit);
        var size = new SKSize(gridArea.Width, gridArea.Height);
        var wallpaper = BoxGridRenderer.WallpaperAt(0);
        var shadow = Pksm.WallpaperShade(wallpaper);
        canvas.Save();
        canvas.Translate(gridArea.Left, gridArea.Top);
        var bounds = BoxGridRenderer.GridBounds(size);
        PksmPaint.Wallpaper(canvas, SKRect.Inflate(bounds, 6 * unit, 6 * unit), wallpaper);
        var slots = Math.Min(o.Count, BoxGridRenderer.Columns * BoxGridRenderer.Rows);
        for (var i = 0; i < slots; i++)
        {
            var rect = BoxGridRenderer.SlotRect(size, i);
            var icon = i < o.Icons.Count ? o.Icons[i] : null;
            PksmPaint.Slot(canvas, rect, wallpaper, empty: icon is null);
            if (icon is not null) DrawOverviewSprite(canvas, rect, icon);
            if (icon is { Shiny: true })
                BoxGridRenderer.DrawSparkle(canvas, rect.Right - rect.Width * 0.14f, rect.Top + rect.Height * 0.16f,
                    Math.Min(rect.Width, rect.Height) * 0.09f, BoxGridRenderer.SparklePaint);
            if (icon is { HasItem: true }) BoxGridRenderer.DrawHeldItemBadge(canvas, rect);
            if (i == o.Slot) PksmPaint.Selection(canvas, rect);
        }
        canvas.Restore();

        PksmPaint.CenterText(canvas, "SUMMARY ON THE TOP SCREEN  ·  L / R  NEXT POKéMON  ·  B  CLOSE",
            info.Width / 2f, info.Height - hintHeight / 2, small, SKColors.White, shadow, SKTextAlign.Center);
    }

    private void DrawOverviewSprite(SKCanvas canvas, SKRect rect, SlotIcon icon)
    {
        var bitmap = _sprites.GetSprite(icon.Look);
        if (bitmap is null)
        {
            _sprites.Warm(icon.Look, () => MainThread.BeginInvokeOnMainThread(_overviewCanvas.InvalidateSurface));
            return;
        }
        var inset = Math.Min(rect.Width, rect.Height) * 0.03f;
        var box = SKRect.Inflate(rect, -inset, -inset);
        var scale = Math.Min(box.Width / bitmap.Width, box.Height / bitmap.Height);
        var w = bitmap.Width * scale;
        var h = bitmap.Height * scale;
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, new SKRect(rect.MidX - w / 2, rect.MidY - h / 2, rect.MidX + w / 2, rect.MidY + h / 2), BoxGridRenderer.SpriteSampling);
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
                    Content = new Label { Text = typeName, TextColor = Colors.White, FontSize = 10, FontAttributes = FontAttributes.Bold },
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

        if (!_sprites.TryGetShowdown(new SpriteLook(_dexSpecies, 0, false), out var animated))
        {
            _sprites.WarmShowdown(new SpriteLook(_dexSpecies, 0, false), () => MainThread.BeginInvokeOnMainThread(_dexSprite.InvalidateSurface));
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
}
