using System.ComponentModel;
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
/// Bank cursor or the compare screen's focused side, with its INFO / STATS / MOVES / ORIGIN / LEGAL pages turned by the tabs
/// here or SELECT in the Bank; the box overview while the full-screen summary shows the
/// details on top; the Pokédex picker's species; the Poképark journal; hero art of the
/// home shelf's highlighted game; idle branding when nobody claims it.
/// </summary>
public sealed class SecondScreenBoxPage : ContentPage
{

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
        _inspector = new MonSummaryView(sprites)
        {
            FixHint = "Press + > Legalize this one to find the closest legal version. Its PID, nature or IVs may change.",
        };
        if (state is not null)
        {
            _inspector.SetPage(state.InspectorPage);
            _inspector.PageChanged += page => state.InspectorPage = page;
        }
        // The summary is drawn for this screen's full 1240×1080 and runs to its edges.
        var summary = new Grid { Children = { _inspector } };

        // A purpose-built game banner replaces inconsistent third-party hero art and covers every title.
        var hero = new GameHeroBackdrop { IsVisible = false };

        var idle = new VerticalStackLayout
        {
            Spacing = 6,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Image { Source = ThemeLogo.Source(), WidthRequest = 96, HeightRequest = 96, HorizontalOptions = LayoutOptions.Center },
                new Label { Text = "PKFORGE", TextColor = UiTokens.Ink0, FontSize = 22, FontAttributes = FontAttributes.Bold, CharacterSpacing = 4, HorizontalTextAlignment = TextAlignment.Center },
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
                        (_viewModel.LegalityText ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries), _viewModel.LegalityChecks);
                default:
                    pending = session.SupportsLegalityAnalysis;
                    return built;
            }
        }
        // The lower screen is a companion: whatever goes wrong there turns it off for the
        // session (logged) and never takes the app down with it.
        async void ShowBoxSummary(Domain.EntityDetail detail)
        {
            try { await ShowBoxSummaryCore(detail); }
            catch (Exception error) { LowerScreenFailed("box summary", error); }
        }

        async Task ShowBoxSummaryCore(Domain.EntityDetail detail)
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
            try { await SwapCoreAsync(); }
            catch (Exception error) { LowerScreenFailed("swap", error); }
        }

        async Task SwapCoreAsync()
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
            var compareDriven = owner == SecondScreenOwner.Compare && state?.Compared is not null;
            var showSummary = bankDriven || compareDriven || (owner == SecondScreenOwner.Box && detail is { IsEmpty: false });
            var showHero = owner == SecondScreenOwner.Home && preview is not null;
            var showRoute = owner == SecondScreenOwner.Autopilot && state?.AutopilotRoute is not null;

            boxSequence++; // any in-flight box decode is now stale
            if (showDex) UpdateDex(species!.Value);
            if (showOverview) { _overview = state!.Overview; _overviewCanvas.InvalidateSurface(); }
            if (showSummary)
            {
                if (bankDriven)
                    _inspector.Show(state!.Inspected?.Summary, state.Inspected?.LegalityPending == true, state.Inspected?.Caption, "EMPTY BANK SLOT");
                else if (compareDriven)
                    _inspector.Show(state!.Compared!.Summary, state.Compared.LegalityPending, state.Compared.Caption);
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

    private static void LowerScreenFailed(string what, Exception error)
    {
        SecondScreenMode.DisableForSession($"the lower screen's {what} failed", error);
        var host = IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>();
        try { _ = host?.DismissAsync(); }
        catch (Exception dismiss) when (dismiss is InvalidOperationException or Java.Lang.Exception)
        {
            AppLog.Error("second", "Dismissing the lower screen failed", dismiss);
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
        var view = new PokeparkJournalView();
        _journalView = view;
        // The view repaints itself; the presentation only needs to know the content changed.
        handler = state is null ? null : (_, _) => MainThread.BeginInvokeOnMainThread(() => _swapAsync?.Invoke());
        if (handler is not null) state!.PropertyChanged += handler;
        return view;
    }

    private PokeparkJournalView? _journalView;

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
        _journalView?.Detach();
        _swapAsync = null;
    }


    // ── The box overview (while the full-screen summary owns the details) ──

    private SKCanvasView _overviewCanvas = null!;
    private SummaryOverview? _overview;

    /// <summary>The walked box as the PKSM grid, the viewed mon under the red hand: where you
    /// are in the box, not the details the top screen already carries.</summary>
    private View BuildOverview()
    {
        _overviewCanvas = new SKCanvasView { IsVisible = true };
        _overviewCanvas.PaintSurface += PaintGuard.Wrap("SecondScreenBoxPage.PaintOverview", PaintOverview);
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

        // The Thor's top panel; on panels Android cannot tell apart (Gamma OS), the main one.
        var arrangement = IPlatformApplication.Current?.Services.GetService<ISecondaryDisplayHost>()?.Arrangement ?? ScreenArrangement.MainOnTop;
        PksmPaint.CenterText(canvas, $"SUMMARY ON {DualScreenLayout.MainScreenCaps(arrangement)}  ·  L / R  NEXT POKéMON  ·  B  CLOSE",
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
        var image = Services.SpriteImages.Of(bitmap);
        canvas.DrawImage(image, new SKRect(rect.MidX - w / 2, rect.MidY - h / 2, rect.MidX + w / 2, rect.MidY + h / 2), BoxGridRenderer.SpriteSampling);
    }

    // ── The logo-deck Pokédex view (species preview while the picker is open) ──

    private DexEntryView _dexEntry = null!;

    /// <summary>The species' Pokédex page, in the summary's layout.</summary>
    private View BuildDexView()
    {
        _dexEntry = new DexEntryView(_sprites);
        return new Grid { IsVisible = false, Children = { _dexEntry } };
    }

    private void UpdateDex(int species) => _dexEntry.Show(species);

}
