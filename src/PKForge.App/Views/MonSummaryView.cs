using System.Diagnostics;
using System.Runtime.CompilerServices;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The read-only Pokémon summary, modelled on the games' own summary screens: a hero
/// card (render, ball, name, gender, level, types, verdict) beside five pages -
/// INFO (trainer, nature, ability, item, ball, care), STATS (hexagon plus the
/// base · IV · EV · value table with the nature's arrows), MOVES (type, category,
/// numbers, PP, effect), ORIGIN (game, met data, ribbons and marks) and LEGAL (the
/// full verdict). One component serves the Bank's second-screen inspector, the
/// full-screen summary and the box browser's Summary entry; it lays itself out as two
/// columns on wide surfaces and stacks on a phone.
/// <para>
/// Performance: the view is built once and never rebuilt. Everything but the empty-slot
/// label is four Skia canvases (sprite, facts, tabs, page) painted with the app's PKSM
/// chrome, so a new mon or a page turn is a repaint - no view-tree churn, no layout storm.
/// </para>
/// </summary>
public sealed class MonSummaryView : ContentView
{
    private const double WideBreakpoint = 560;
    private const float FactsHeight = 132;
    private static readonly string[] StatCaps = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];
    private static readonly string[] StatNames = ["HP", "Attack", "Defense", "Sp. Atk", "Sp. Def", "Speed"];
    private static readonly string[] MarkingGlyphs = ["●", "▲", "■", "♥", "★", "◆"];

    private static readonly SKColor Raised = new(0xFF, 0x8C, 0x7C);
    private static readonly SKColor Lowered = new(0x7C, 0xB8, 0xFF);
    private static readonly SKColor NoteInk = new(0x9C, 0xCC, 0xF4);

    private readonly ISpriteService _sprites;
    private readonly Grid _layout = new();
    private readonly Grid _hero = new();
    // The portrait is two layers: the stage (panel, light, floor, ball) repaints once per mon;
    // only the small sprite layer on top repaints on GIF ticks.
    private readonly Grid _portrait = new();
    private readonly SKCanvasView _stage = new() { InputTransparent = true };
    private readonly SKCanvasView _sprite = new()
    {
        InputTransparent = true, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.Start,
    };
    private readonly SKCanvasView _facts = new() { InputTransparent = true, HeightRequest = FactsHeight };
    private readonly SKCanvasView _tabs = new() { HeightRequest = 34, EnableTouchEvents = true };
    // The page body scrolls inside its own canvas: one viewport-sized surface painted at
    // _scrollY and driven by its own touch stream (drag, fling, sideways swipe to turn).
    private readonly SKCanvasView _page = new() { EnableTouchEvents = true };
    private double _scrollY, _contentHeight;
    private readonly Label _empty = new()
    {
        FontFamily = DsChrome.PixelFont, FontSize = 15, TextColor = UiTokens.InkSoft,
        HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center,
        HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, IsVisible = false,
    };

    private MonSummary? _summary;
    private bool _legalityPending;
    private string? _caption;
    private SummaryPage _pageKind = SummaryPage.Info;
    private bool? _wide;
    private IDispatcherTimer? _timer;
    private long _elapsedMs;
    private bool _spriteAnimated;

    /// <summary>Raised when a tab tap or swipe turns the page (hosts keep their own page state in sync).</summary>
    public event Action<SummaryPage>? PageChanged;

    public MonSummaryView(ISpriteService sprites)
    {
        _sprites = sprites;
        _stage.PaintSurface += PaintStage;
        _sprite.PaintSurface += PaintSprite;
        _portrait.Children.Add(_stage);
        _portrait.Children.Add(_sprite);
        _portrait.SizeChanged += (_, _) => PlaceSprite();
        _facts.PaintSurface += PaintFacts;
        _tabs.PaintSurface += PaintTabs;
        _tabs.Touch += OnTabTouch;
        _page.PaintSurface += PaintPage;
        // No ScrollView + SwipeGestureRecognizer any more: the recognizer's Android touch
        // listener consumed the stream, and the InputTransparent canvas inside could not take
        // it back, so nothing ever scrolled by hand. The canvas reads the finger itself.
        _page.Touch += OnPageTouch;

        _hero.Children.Add(_portrait);
        _hero.Children.Add(_facts);
        _layout.Children.Add(_hero);
        _layout.Children.Add(_tabs);
        _layout.Children.Add(_page);
        _layout.Children.Add(_empty);
        Content = _layout;
        ApplyLayout(false);

        SizeChanged += (_, _) => { if (Width > 0) ApplyLayout(Width >= WideBreakpoint); };
        _page.SizeChanged += (_, _) => RefreshPage(resetScroll: false);
        Loaded += (_, _) => UpdateTimer();
        Unloaded += (_, _) => StopTimer();
        if (!SummaryInk.FaceReady) _ = WarmFontsAsync();
    }

    public SummaryPage Page => _pageKind;
    public MonSummary? Summary => _summary;

    /// <summary>Shows a mon (null = the empty-slot card). <paramref name="legalityPending"/>
    /// marks a verdict that is still being computed; <paramref name="caption"/> is the
    /// host's context line ("BANK · BOX 03 · SLOT 04").</summary>
    public void Show(MonSummary? summary, bool legalityPending = false, string? caption = null, string? emptyText = null)
    {
        var watch = Stopwatch.StartNew();
        var sameMon = SameMon(summary, _summary);
        _summary = summary;
        _legalityPending = legalityPending;
        _caption = caption;
        var has = summary is not null;
        if (_hero.IsVisible != has) _hero.IsVisible = _tabs.IsVisible = _page.IsVisible = has;
        if (_empty.IsVisible == has) _empty.IsVisible = !has;
        if (!has) _empty.Text = emptyText ?? "Empty slot";
        _facts.InvalidateSurface();
        // The same mon with its verdict arriving keeps its sprite frame and scroll: no flicker.
        if (!sameMon)
        {
            _stage.InvalidateSurface();
            _sprite.InvalidateSurface();
        }
        // Only the verdict changed (the slow second half of a load): the hero repaints, the page
        // only when it is the one showing the verdict.
        if (!sameMon || _pageKind == SummaryPage.Legality) RefreshPage(resetScroll: !sameMon);
        UpdateTimer();
        PerfTrace.Log($"summary.show[{_pageKind}]", watch);
        PerfTrace.UntilIdle($"summary.show[{_pageKind}]", Dispatcher);
    }

    /// <summary>The same entity decoded twice (quick, then with its verdict), not merely the same species.</summary>
    private static bool SameMon(MonSummary? a, MonSummary? b) =>
        a is not null && b is not null && a.Species == b.Species && a.Form == b.Form && a.IsShiny == b.IsShiny
        && a.Ball == b.Ball && a.IsEgg == b.IsEgg && a.Nickname == b.Nickname && a.Level == b.Level
        && a.OriginalTrainer == b.OriginalTrainer && a.TrainerId == b.TrainerId && a.Nature == b.Nature
        && a.Friendship == b.Friendship && a.HeldItem == b.HeldItem && a.RibbonCount == b.RibbonCount
        && a.Stats.SequenceEqual(b.Stats) && a.IVs.SequenceEqual(b.IVs) && a.EVs.SequenceEqual(b.EVs)
        && a.Moves.Select(m => m.Id).SequenceEqual(b.Moves.Select(m => m.Id))
        && a.Met?.MetDate == b.Met?.MetDate && a.Met?.MetLocation == b.Met?.MetLocation && a.Met?.Version == b.Met?.Version;

    public void SetPage(SummaryPage page, bool raise = false)
    {
        if (_pageKind == page) return;
        var watch = Stopwatch.StartNew();
        _pageKind = page;
        _tabs.InvalidateSurface();
        RefreshPage(resetScroll: true);
        PerfTrace.Log($"summary.page[{page}]", watch);
        PerfTrace.UntilIdle($"summary.page[{page}]", Dispatcher);
        if (raise) PageChanged?.Invoke(page);
    }

    public void TurnPage(int direction, bool raise = false) => SetPage(SummaryNavigation.Turn(_pageKind, direction), raise);

    /// <summary>D-pad up/down on the page body.</summary>
    public void ScrollBy(int direction)
    {
        StopFling();
        var target = Math.Clamp(_scrollY + direction * Math.Max(60, _page.Height * 0.6), 0, MaxScroll);
        this.AbortAnimation(ScrollAnimation);
        new Animation(ScrollTo, _scrollY, target).Commit(this, ScrollAnimation, length: 180, easing: Easing.CubicOut);
    }

    // ── Touch: drag to scroll, fling, swipe to turn ──────────────────────────

    private const float TouchSlopDp = 8;
    private const float SwipeTurnDp = 56;
    private const double FlingFriction = 0.94; // velocity kept per 16 ms frame
    private SKPoint _touchStart;
    private double _touchStartScroll;
    private bool _dragging, _swiping;
    private readonly List<(long Ms, float Y)> _touchTrail = new();
    private IDispatcherTimer? _fling;
    private double _flingVelocity; // dp per ms

    private const string ScrollAnimation = "summary-scroll";

    private double MaxScroll => Math.Max(0, _contentHeight - _page.Height);

    private void OnPageTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        var widthPx = _page.CanvasSize.Width;
        var density = widthPx > 0 && _page.Width > 0 ? (float)(widthPx / _page.Width) : 1f;
        var point = new SKPoint(args.Location.X / density, args.Location.Y / density);
        var now = Environment.TickCount64;
        switch (args.ActionType)
        {
            case SKTouchAction.Pressed:
                StopFling();
                this.AbortAnimation(ScrollAnimation);
                _touchStart = point;
                _touchStartScroll = _scrollY;
                _dragging = _swiping = false;
                _touchTrail.Clear();
                _touchTrail.Add((now, point.Y));
                break;
            case SKTouchAction.Moved:
                var dx = point.X - _touchStart.X;
                var dy = point.Y - _touchStart.Y;
                if (!_dragging && !_swiping && Math.Max(Math.Abs(dx), Math.Abs(dy)) > TouchSlopDp)
                {
                    // The first decisive direction wins for the whole gesture: no diagonal fight.
                    if (Math.Abs(dy) >= Math.Abs(dx)) _dragging = true;
                    else _swiping = true;
                }
                if (_dragging)
                {
                    ScrollTo(_touchStartScroll - dy);
                    _touchTrail.Add((now, point.Y));
                    if (_touchTrail.Count > 6) _touchTrail.RemoveAt(0);
                }
                break;
            case SKTouchAction.Released:
                if (_dragging) StartFling(now);
                else if (_swiping && Math.Abs(point.X - _touchStart.X) > SwipeTurnDp)
                    TurnPage(point.X < _touchStart.X ? 1 : -1, raise: true);
                _dragging = _swiping = false;
                break;
            case SKTouchAction.Cancelled:
                _dragging = _swiping = false;
                break;
        }
    }

    private void ScrollTo(double y)
    {
        var clamped = Math.Clamp(y, 0, MaxScroll);
        if (Math.Abs(clamped - _scrollY) < 0.25) return;
        _scrollY = clamped;
        _page.InvalidateSurface();
    }

    private void StartFling(long now)
    {
        // Velocity over the last ~100 ms of the trail; a finger that stopped before lifting does not fling.
        var recent = _touchTrail.Where(t => now - t.Ms <= 100).ToList();
        if (recent.Count < 2) return;
        var span = recent[^1].Ms - recent[0].Ms;
        if (span <= 0) return;
        _flingVelocity = -(recent[^1].Y - recent[0].Y) / span;
        if (Math.Abs(_flingVelocity) < 0.1) return;
        _fling ??= CreateFlingTimer();
        _fling.Start();
    }

    private IDispatcherTimer CreateFlingTimer()
    {
        var timer = Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.Tick += (_, _) =>
        {
            var target = _scrollY + _flingVelocity * 16;
            _flingVelocity *= FlingFriction;
            if (target <= 0 || target >= MaxScroll || Math.Abs(_flingVelocity) < 0.02) StopFling();
            ScrollTo(target);
        };
        return timer;
    }

    private void StopFling() => _fling?.Stop();

    public static string PageTitle(SummaryPage page) => page switch
    {
        SummaryPage.Info => "INFO",
        SummaryPage.Stats => "STATS",
        SummaryPage.Moves => "MOVES",
        SummaryPage.Origin => "ORIGIN",
        _ => "LEGAL",
    };

    private static string TabTitle(SummaryPage page) => page switch
    {
        SummaryPage.Info => "Info",
        SummaryPage.Stats => "Stats",
        SummaryPage.Moves => "Moves",
        SummaryPage.Origin => "Origin",
        _ => "Legal",
    };

    private async Task WarmFontsAsync()
    {
        await Task.WhenAll(PixelFont.WarmAsync(), PixelFont.WarmFallbackAsync());
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _facts.InvalidateSurface();
            _tabs.InvalidateSurface();
            RefreshPage(resetScroll: false);
        });
    }

    // ── Layout (set once per orientation; never per mon) ─────────────────────

    private void ApplyLayout(bool wide)
    {
        if (_wide == wide) return;
        _wide = wide;
        _layout.RowDefinitions.Clear();
        _layout.ColumnDefinitions.Clear();
        _layout.RowSpacing = 8;
        _layout.ColumnSpacing = 12;
        _hero.RowDefinitions.Clear();
        _hero.ColumnDefinitions.Clear();
        _hero.RowSpacing = 8;
        _hero.ColumnSpacing = 10;
        if (wide)
        {
            // Hero column on the left, tabs + page on the right.
            _layout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(2, GridUnitType.Star)));
            _layout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(3, GridUnitType.Star)));
            _layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _layout.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Place(_layout, _hero, 0, 0, rowSpan: 2);
            Place(_layout, _tabs, 0, 1);
            Place(_layout, _page, 1, 1);
            _hero.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            _hero.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _portrait.HeightRequest = -1;
            _portrait.MinimumHeightRequest = 120;
            Place(_hero, _portrait, 0, 0);
            Place(_hero, _facts, 1, 0);
        }
        else
        {
            _layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _layout.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _layout.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Place(_layout, _hero, 0, 0);
            Place(_layout, _tabs, 1, 0);
            Place(_layout, _page, 2, 0);
            _hero.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(FactsHeight)));
            _hero.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            _portrait.HeightRequest = FactsHeight;
            Place(_hero, _portrait, 0, 0);
            Place(_hero, _facts, 0, 1);
        }
        Place(_layout, _empty, 0, 0, rowSpan: _layout.RowDefinitions.Count, columnSpan: Math.Max(1, _layout.ColumnDefinitions.Count));
    }

    private static void Place(Grid grid, View view, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        Grid.SetRow(view, row);
        Grid.SetColumn(view, column);
        Grid.SetRowSpan(view, rowSpan);
        Grid.SetColumnSpan(view, columnSpan);
    }

    /// <summary>Measures the page at the scroller's width; only a height change touches layout.</summary>
    private void RefreshPage(bool resetScroll)
    {
        var width = (float)_page.Width;
        if (_summary is null || width <= 0) return;
        _contentHeight = RenderPage(null, _summary, width);
        if (resetScroll)
        {
            StopFling();
            this.AbortAnimation(ScrollAnimation);
            _scrollY = 0;
        }
        else _scrollY = Math.Clamp(_scrollY, 0, MaxScroll);
        _page.InvalidateSurface();
    }

    /// <summary>Scales a canvas so everything below paints in device-independent units; returns the width in dp.</summary>
    private static float BeginDp(SKCanvas canvas, SKImageInfo info, View view)
    {
        canvas.Clear(SKColors.Transparent);
        var scale = view.Width > 0 ? (float)(info.Width / view.Width) : (float)Math.Max(1, DeviceDisplay.MainDisplayInfo.Density);
        canvas.Scale(scale);
        return info.Width / scale;
    }

    // ── Tabs ─────────────────────────────────────────────────────────────────

    private void PaintTabs(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        var w = BeginDp(c, args.Info, _tabs);
        var h = args.Info.Height * (w / args.Info.Width);
        const float gap = 5;
        var cell = (w - gap * 4) / 5;
        for (var i = 0; i < 5; i++)
        {
            var page = (SummaryPage)i;
            var r = new SKRect(i * (cell + gap), 1, i * (cell + gap) + cell, h - 1);
            SummaryChrome.Tab(c, r, TabTitle(page), page == _pageKind, SummaryChrome.Accent(page));
        }
    }

    private void OnTabTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Pressed) return;
        var widthPx = _tabs.CanvasSize.Width;
        if (widthPx <= 0) return;
        var index = Math.Clamp((int)(args.Location.X / widthPx * 5), 0, 4);
        SetPage((SummaryPage)index, raise: true);
    }

    // ── Hero ─────────────────────────────────────────────────────────────────

    private void PaintFacts(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        var w = BeginDp(c, args.Info, _facts);
        if (_summary is not { } s) return;

        // The name bar: the app's cobalt header strip, gender and shiny at its end.
        var bar = new SKRect(0, 0, w, 34);
        var hasGender = s.Gender is 0 or 1 && !s.IsEgg;
        var marks = (s.IsShiny ? 22 : 0) + (hasGender ? 22 : 0);
        var name = SummaryInk.Fit(s.IsEgg ? "Egg" : s.DisplayName, 17, w - 24 - marks, bold: true);
        SummaryChrome.Strip(c, bar, name, size: 17);
        var x = w - 12;
        if (hasGender)
        {
            var (glyph, tone) = s.Gender == 0 ? ("♂", new SKColor(0x8C, 0xC8, 0xFF)) : ("♀", new SKColor(0xFF, 0x9C, 0xC0));
            SummaryInk.Draw(c, glyph, x, SummaryInk.Center(bar.MidY, 17), 17, tone, bold: true, align: SKTextAlign.Right);
            x -= 22;
        }
        if (s.IsShiny) SummaryInk.Draw(c, "★", x, SummaryInk.Center(bar.MidY, 16), 16, Pksm.ShinyGold, align: SKTextAlign.Right);

        var species = s.FormName.Length > 0 ? $"{s.SpeciesName} ({s.FormName})" : s.SpeciesName;
        var identity = s.IsEgg ? $"No. {s.Species:000}  {species}" : $"No. {s.Species:000}  {species}   Lv. {s.Level}";
        SummaryInk.Draw(c, SummaryInk.Fit(identity, 14.5f, w - 4), 2, SummaryInk.Center(52, 14.5f), 14.5f, Pksm.Ink);

        var tx = 2f;
        foreach (var type in s.Types)
        {
            SummaryChrome.TypeBadge(c, new SKRect(tx, 66, tx + 70, 86), type);
            tx += 76;
        }

        // Status as coloured text, the way the games print it - no chips.
        var sx = 2f;
        var sy = SummaryInk.Center(103, 13.5f);
        void Run(string text, SKColor color)
        {
            if (sx > w) return;
            sx += SummaryInk.Draw(c, text, sx, sy, 13.5f, color) + 12;
        }
        Run(Kit.ConsoleCode(s.Generation), Sk(Kit.EraColor(s.Generation)));
        Run(s.Format.ToUpperInvariant(), Pksm.InkSoft);
        if (Verdict() is { } verdict) Run(verdict.Text, verdict.Color);
        if (s.Pokerus is { Status: not PokerusStatus.Susceptible } rus)
            Run(rus.Status == PokerusStatus.Infectious ? "Pokérus" : "Pokérus ✓", new SKColor(0xE0, 0x7A, 0xD0));
        if (s.RibbonCount + s.MarkCount > 0) Run($"✦ {s.RibbonCount + s.MarkCount}", Pksm.RibbonGold);

        if (!string.IsNullOrEmpty(_caption))
            SummaryInk.Draw(c, SummaryInk.Fit(_caption, 12.5f, w - 4), 2, SummaryInk.Center(123, 12.5f), 12.5f, Pksm.InkSoft);
    }

    private static SKColor Sk(Color color) => new((byte)(color.Red * 255), (byte)(color.Green * 255), (byte)(color.Blue * 255));

    private (string Text, SKColor Color)? Verdict()
    {
        if (_legalityPending) return ("Checking…", Pksm.InkSoft);
        return _summary?.Legal switch
        {
            true => ("✓ Legal", Pksm.Legal),
            false => ("✗ Not legal", Pksm.Illegal),
            _ => null,
        };
    }

    // ── Pages ────────────────────────────────────────────────────────────────

    private static readonly SKPaint ThumbPaint = new() { IsAntialias = true, Color = SKColors.White.WithAlpha(0x70) };

    /// <summary>A slim thumb on the right edge while the page is taller than its viewport.</summary>
    private void PaintScrollThumb(SKCanvas c, float w, float h)
    {
        var max = MaxScroll;
        if (max <= 0 || h <= 0) return;
        var length = Math.Max(24, h * h / (float)_contentHeight);
        var top = (float)(_scrollY / max) * (h - length);
        c.DrawRoundRect(new SKRect(w - 4, top, w - 1, top + length), 1.5f, 1.5f, ThumbPaint);
    }

    private void PaintPage(object? sender, SKPaintSurfaceEventArgs args)
    {
        var watch = Stopwatch.StartNew();
        var c = args.Surface.Canvas;
        var w = BeginDp(c, args.Info, _page);
        if (_summary is { } s)
        {
            c.Save();
            c.Translate(0, -(float)_scrollY);
            RenderPage(c, s, w);
            c.Restore();
            PaintScrollThumb(c, w, (float)_page.Height);
        }
        PerfTrace.Log("summary.paint-page", watch);
    }

    /// <summary>One pass that both measures (canvas null) and paints; returns the page height.</summary>
    private float RenderPage(SKCanvas? c, MonSummary s, float w)
    {
        var y = _pageKind switch
        {
            SummaryPage.Info => InfoPage(c, s, w),
            SummaryPage.Stats => StatsPage(c, s, w),
            SummaryPage.Moves => MovesPage(c, s, w),
            SummaryPage.Origin => OriginPage(c, s, w),
            _ => LegalityPage(c, s, w),
        };
        return y + 8;
    }

    /// <summary>A row of a section: paints at (x, y) within width w (or only measures when c is null); returns its height.</summary>
    private delegate float Row(SKCanvas? c, float x, float y, float w);

    private const float SectionGap = 10;
    private const float Pad = 6;
    private const float StripHeight = 28;
    private const float ShadowInset = 3;

    /// <summary>A page section: a device panel carrying its accent header strip and rows split by hairlines.</summary>
    private static float Section(SKCanvas? c, float y, float w, string title, SKColor accent, IReadOnlyList<Row> rows, string? trailing = null)
    {
        var panelWidth = w - ShadowInset;
        var inner = panelWidth - Pad * 2;
        var heights = new float[rows.Count];
        var total = Pad + StripHeight + 2;
        for (var i = 0; i < rows.Count; i++) total += heights[i] = rows[i](null, Pad, 0, inner);
        total += Pad;
        if (c is not null)
        {
            SummaryChrome.Panel(c, new SKRect(0, y, panelWidth, y + total));
            SummaryChrome.Strip(c, new SKRect(Pad, y + Pad, panelWidth - Pad, y + Pad + StripHeight), title, accent, trailing: trailing);
            var ry = y + Pad + StripHeight + 2;
            for (var i = 0; i < rows.Count; i++)
            {
                if (i > 0) SummaryChrome.Divider(c, Pad + 4, panelWidth - Pad - 4, ry);
                rows[i](c, Pad, ry, inner);
                ry += heights[i];
            }
        }
        return y + total + SectionGap;
    }

    private const float ValueSize = 14.5f;
    private const float CaptionSize = 13f;
    private const float DetailSize = 12.5f;
    private const float CaptionWidth = 104;

    /// <summary>A fact: the caption in soft ink on the left, the value (and its quiet detail) beside it.</summary>
    private static Row Fact(string caption, string value, string? detail = null) => (c, x, y, w) =>
    {
        var valueWidth = w - CaptionWidth - 12;
        var lines = SummaryInk.Wrap(value, ValueSize, valueWidth);
        var details = string.IsNullOrWhiteSpace(detail) ? [] : SummaryInk.Wrap(detail, DetailSize, valueWidth);
        var height = 8 + lines.Count * SummaryInk.Leading(ValueSize) + details.Count * SummaryInk.Leading(DetailSize) + 7;
        if (c is null) return height;
        var firstMid = y + 8 + SummaryInk.Leading(ValueSize) / 2;
        SummaryInk.Draw(c, caption, x + 8, SummaryInk.Center(firstMid, CaptionSize), CaptionSize, Pksm.InkSoft);
        var ly = SummaryInk.Center(firstMid, ValueSize);
        foreach (var line in lines) { SummaryInk.Draw(c, line, x + CaptionWidth, ly, ValueSize, Pksm.Ink); ly += SummaryInk.Leading(ValueSize); }
        ly += SummaryInk.Leading(DetailSize) - SummaryInk.Leading(ValueSize);
        foreach (var line in details) { SummaryInk.Draw(c, line, x + CaptionWidth, ly, DetailSize, Pksm.InkSoft); ly += SummaryInk.Leading(DetailSize); }
        return height;
    };

    /// <summary>A fact whose value is drawn by the caller (type badges, marking glyphs).</summary>
    private static Row FactArt(string caption, Action<SKCanvas, float, float> paint, float height = 34) => (c, x, y, w) =>
    {
        if (c is null) return height;
        SummaryInk.Draw(c, caption, x + 8, SummaryInk.Center(y + height / 2, CaptionSize), CaptionSize, Pksm.InkSoft);
        paint(c, x + CaptionWidth, y + height / 2);
        return height;
    };

    /// <summary>Wrapped free text (notes, lists) with the section's insets.</summary>
    private static Row Text(string text, SKColor color, float size = 13.5f) => (c, x, y, w) =>
    {
        var lines = SummaryInk.Wrap(text, size, w - 16);
        var height = 8 + lines.Count * SummaryInk.Leading(size) + 7;
        if (c is null) return height;
        var ly = SummaryInk.Center(y + 8 + SummaryInk.Leading(size) / 2, size);
        foreach (var line in lines) { SummaryInk.Draw(c, line, x + 8, ly, size, color); ly += SummaryInk.Leading(size); }
        return height;
    };

    private static Row Note(string text, SKColor? tone = null) => Text(text, tone ?? NoteInk);

    private static float InfoPage(SKCanvas? c, MonSummary s, float w)
    {
        var accent = SummaryChrome.Accent(SummaryPage.Info);
        var y = 0f;
        if (s.IsEgg)
            y = Section(c, y, w, "Egg", accent, [Note("This Pokémon is still an egg: its stats and moves appear once it hatches.")]);

        var f = s.Fields;
        var trainer = new List<Row> { Fact("OT", s.OriginalTrainer.Length > 0 ? s.OriginalTrainer : "—", f?.OtGender is { } otGender ? GenderWord(otGender) : null) };
        if (s.TrainerId is { } tid)
            trainer.Add(Fact("ID No.", s.SecretId is { } sid ? $"{tid:00000}   SID {sid:00000}" : $"{tid:00000}"));
        if (f?.HandlerName is { } handler)
        {
            var about = new[] { f.HandlerGender is { } hg ? GenderWord(hg) : null, f.HandlerLanguage, f.HandlerFriendship is { } hf ? $"friendship {hf}" : null };
            trainer.Add(Fact("Handler", handler, string.Join(" · ", about.Where(x => x is not null))));
            trainer.Add(Fact("Now with", f.WithHandler == true ? handler : "Its original trainer",
                f.WithHandler == true && f.OtFriendship is { } otf ? $"OT friendship {otf}" : null));
        }
        y = Section(c, y, w, "Trainer", accent, trainer);

        var character = new List<Row>();
        if (s.NatureName is not null && s.Nature is { } nature)
            character.Add(Fact("Nature", s.NatureName, NatureFacts.IsNeutral(nature) ? "Neutral: no stat is raised or lowered." : NatureFacts.EffectLabel(nature)));
        if (f?.StatNature is { } mint)
            character.Add(Fact("Mint", f.StatNatureName ?? $"#{mint}", $"Stats follow it: {(NatureFacts.IsNeutral(mint) ? "neutral" : NatureFacts.EffectLabel(mint))}"));
        if (f?.FormArgument is { } formArgument) character.Add(Fact("Form", s.FormName.Length > 0 ? s.FormName : s.SpeciesName, formArgument));
        if (s.IsShiny && f?.Shiny is ShinyKind.Square or ShinyKind.Star)
            character.Add(Fact("Shiny", f.Shiny == ShinyKind.Square ? "Square sparkles" : "Star sparkles"));
        if (s.Characteristic is not null) character.Add(Fact("Trait", s.Characteristic));
        if (s.AbilityName is not null) character.Add(Fact("Ability", s.AbilityName, s.AbilityEffect));
        if (character.Count == 0) character.Add(Note("Gen 1 and 2 Pokémon have no nature or ability."));
        y = Section(c, y, w, "Character", accent, character);

        var items = new List<Row>
        {
            Fact("Held item", s.HeldItemName ?? "None", s.HeldItemEffect),
            Fact("Ball", s.BallName),
        };
        if (!s.IsEgg) items.Add(Fact("Friendship", $"{s.Friendship} / 255", FriendshipLine(s.Friendship)));
        if (s.Pokerus is { } rus)
            items.Add(Fact("Pokérus", rus.Status switch
            {
                PokerusStatus.Infectious => $"Infected (strain {rus.Strain}, {rus.Days} day(s) left)",
                PokerusStatus.Cured => "Cured: EV gains stay doubled",
                _ => "Never infected",
            }));
        if (s.Markings.Count > 0) items.Add(FactArt("Markings", (canvas, x, cy) => Markings(canvas, x, cy, s.Markings)));
        return Section(c, y, w, "Items & care", accent, items);
    }

    private static string GenderWord(int gender) => gender == 1 ? "Female" : "Male";

    private static string FriendshipLine(int friendship) => friendship switch
    {
        >= 255 => "It loves you deeply.",
        >= 220 => "It is very friendly toward you.",
        >= 150 => "It is quite friendly.",
        >= 70 => "It is warming up to you.",
        _ => "It is still getting used to you.",
    };

    private static void Markings(SKCanvas c, float x, float cy, IReadOnlyList<CosmeticMarking> markings)
    {
        for (var i = 0; i < markings.Count; i++)
        {
            var value = markings[i].Value;
            // Gen 7+ markings are two-colour (1 blue, 2 red); older games only have "on".
            var color = value == 0 ? Pksm.PaperEdge : value == 2 ? Pksm.Illegal : Pksm.SelectBorder;
            x += SummaryInk.Draw(c, i < MarkingGlyphs.Length ? MarkingGlyphs[i] : "•", x, SummaryInk.Center(cy, 15), 15, color) + 8;
        }
    }

    private static float StatsPage(SKCanvas? c, MonSummary s, float w)
    {
        var accent = SummaryChrome.Accent(SummaryPage.Stats);
        var y = 0f;
        if (s.Stats.Count == 6)
        {
            const float radar = 192;
            if (c is not null) PaintRadar(c, new SKRect(0, y, w - ShadowInset, y + radar), s, accent);
            y += radar + 6;
        }
        y = Section(c, y, w, "Stats", accent, StatRows(s));

        var extras = new List<Row>();
        if (s.HiddenPowerType is { } hp)
            extras.Add(FactArt("Hidden Power", (canvas, x, cy) => SummaryChrome.TypeBadge(canvas, new SKRect(x, cy - 10, x + 76, cy + 10), hp)));
        if (s.TeraType is { } tera && TypeFacts.IsValid(tera))
            extras.Add(FactArt("Tera Type", (canvas, x, cy) => SummaryChrome.TypeBadge(canvas, new SKRect(x, cy - 10, x + 76, cy + 10), tera)));
        else if (s.TeraTypeName is not null) extras.Add(Fact("Tera Type", s.TeraTypeName));
        if (s.Characteristic is not null) extras.Add(Fact("Trait", s.Characteristic));
        if (extras.Count > 0) y = Section(c, y, w, "Potential", accent, extras);
        return y;
    }

    /// <summary>The base · IV · EV · stat table, ruled like the games' own, nature tints on the names.</summary>
    private static List<Row> StatRows(MonSummary s)
    {
        const float rowHeight = 25;
        float ColBase(float x) => x + 112;
        float ColStat(float x, float w) => x + w - 8;
        float ColEv(float x, float w) => ColStat(x, w) - 50;
        float ColIv(float x, float w) => ColEv(x, w) - (s.ClassicTraining ? 64 : 46);
        // A mint moves the stat changes off the nature: tint what the stats actually follow.
        var statNature = s.Fields?.StatNature ?? s.Nature;
        var up = statNature is { } sn ? NatureFacts.Raised(sn) : null;
        var down = statNature is { } dn ? NatureFacts.Lowered(dn) : null;
        var rows = new List<Row>
        {
            (c, x, y, w) =>
            {
                const float h = 22;
                if (c is null) return h;
                var baseline = SummaryInk.Center(y + h / 2 + 1, 12.5f);
                SummaryInk.Draw(c, "Base", ColBase(x), baseline, 12.5f, Pksm.InkSoft, align: SKTextAlign.Right);
                SummaryInk.Draw(c, s.ClassicTraining ? "DV" : "IV", ColIv(x, w), baseline, 12.5f, Pksm.InkSoft, align: SKTextAlign.Right);
                SummaryInk.Draw(c, s.ClassicTraining ? "Exp" : "EV", ColEv(x, w), baseline, 12.5f, Pksm.InkSoft, align: SKTextAlign.Right);
                SummaryInk.Draw(c, "Stat", ColStat(x, w), baseline, 12.5f, Pksm.InkSoft, align: SKTextAlign.Right);
                return h;
            },
            (c, x, y, w) =>
            {
                const float h = rowHeight * 7 + 2;
                if (c is null) return h;
                for (var i = 0; i <= 6; i++)
                {
                    var top = y + i * rowHeight;
                    var mid = top + rowHeight / 2;
                    // Alternating soft stripes, the StripeRow rhythm of the app's lists.
                    if (i % 2 == 0 && i < 6) SummaryChrome.Band(c, new SKRect(x + 2, top, x + w - 2, top + rowHeight), Pksm.PaperShade.WithAlpha(0x80));
                    var baseline = SummaryInk.Center(mid, 14);
                    if (i == 6)
                    {
                        SummaryChrome.Divider(c, x + 4, x + w - 4, top + 1);
                        SummaryInk.Draw(c, "Total", x + 8, baseline, 13.5f, Pksm.InkSoft);
                        SummaryInk.Draw(c, s.BaseTotal.ToString(), ColBase(x), baseline, 14, Pksm.InkSoft, align: SKTextAlign.Right);
                        SummaryInk.Draw(c, s.IvTotal.ToString(), ColIv(x, w), baseline, 14, Pksm.InkSoft, align: SKTextAlign.Right);
                        if (!s.ClassicTraining) SummaryInk.Draw(c, s.EvTotal.ToString(), ColEv(x, w), baseline, 14, Pksm.InkSoft, align: SKTextAlign.Right);
                        if (s.Stats.Count == 6) SummaryInk.Draw(c, s.Stats.Sum().ToString(), ColStat(x, w), baseline, 14, Pksm.InkSoft, align: SKTextAlign.Right);
                        continue;
                    }
                    var tone = up == i ? Raised : down == i ? Lowered : Pksm.Ink;
                    SummaryInk.Draw(c, StatNames[i], x + 8, baseline, 14, tone);
                    var baseValue = i < s.BaseStats.Count ? s.BaseStats[i] : 0;
                    SummaryInk.Draw(c, baseValue.ToString(), ColBase(x), baseline, 14, Pksm.InkSoft, align: SKTextAlign.Right);
                    var barLeft = ColBase(x) + 10;
                    var barRight = ColIv(x, w) - 34;
                    if (barRight > barLeft + 10)
                        SummaryChrome.Gauge(c, new SKRect(barLeft, mid - 4, barRight, mid + 4), baseValue / 180f, Sk(InfoKit.BaseStatColor(baseValue)));
                    var iv = i < s.IVs.Count ? s.IVs[i] : 0;
                    SummaryInk.Draw(c, iv.ToString(), ColIv(x, w), baseline, 14, iv >= s.Caps.IvMax ? Pksm.Legal : Pksm.Ink, align: SKTextAlign.Right);
                    SummaryInk.Draw(c, i < s.EVs.Count ? s.EVs[i].ToString() : "0", ColEv(x, w), baseline, 14, Pksm.Ink, align: SKTextAlign.Right);
                    SummaryInk.Draw(c, i < s.Stats.Count ? s.Stats[i].ToString() : "—", ColStat(x, w), baseline, 14, tone, bold: true, align: SKTextAlign.Right);
                }
                return h;
            },
        };
        var legend = s.Nature is null
            ? s.ClassicTraining ? "DVs run 0-15; stat experience up to 65535 per stat." : null
            : s.Fields?.StatNatureName is { } mintName ? up is null ? $"Minted {mintName}: neutral stats." : $"Red raised · blue lowered by the {mintName} mint."
            : s.NatureUp is null ? $"{s.NatureName}: a neutral nature." : $"Red raised · blue lowered by {s.NatureName}.";
        if (legend is not null) rows.Add(Text(legend, Pksm.InkSoft, 12.5f));
        return rows;
    }

    private static float MovesPage(SKCanvas? c, MonSummary s, float w)
    {
        var accent = SummaryChrome.Accent(SummaryPage.Moves);
        var rows = new List<Row>();
        if (s.Moves.Count == 0) rows.Add(Note("No moves recorded."));
        // Verdicts cover the four slots; the page lists only the filled ones, in slot order.
        var verdicts = s.MoveVerdicts?.Moves.Where(v => v.Move > 0).ToList() ?? [];
        for (var i = 0; i < s.Moves.Count; i++)
            rows.Add(MoveRow(s.Moves[i], i < verdicts.Count && verdicts[i].Move == s.Moves[i].Id ? verdicts[i] : null));
        var y = Section(c, 0, w, "Moves", accent, rows);
        if (s.RelearnMoves.Count > 0)
        {
            var relearn = new List<Row> { Text(string.Join(" · ", s.RelearnMoves), Pksm.Ink) };
            foreach (var bad in s.MoveVerdicts?.Relearn.Where(v => !v.Valid) ?? [])
                relearn.Add(Text($"✗ Not legal · {bad.Reason}", Pksm.Illegal, 12.5f));
            y = Section(c, y, w, "Relearnable", accent, relearn);
        }
        if (s.Fields?.TechRecordCount is { } records)
            y = Section(c, y, w, "Technical Records", accent,
                [records > 0 ? Text(string.Join(" · ", s.Fields.TechRecordNames), Pksm.Ink) : Text("No records learned.", Pksm.InkSoft)],
                trailing: records > 0 ? $"{records} learned" : null);
        return y;
    }

    private static Row MoveRow(SummaryMove move, MoveVerdict? verdict) => (c, x, y, w) =>
    {
        var effect = string.IsNullOrWhiteSpace(move.Effect) ? [] : SummaryInk.Wrap(move.Effect, 13f, w - 16);
        var verdictHeight = verdict is null ? 0 : 18;
        var height = 8 + 24 + 20 + verdictHeight + effect.Count * SummaryInk.Leading(13f) + 8;
        if (c is null) return height;
        var top = y + 8;
        SummaryChrome.TypeBadge(c, new SKRect(x + 6, top + 2, x + 74, top + 22), move.Type);
        InfoKit.CategoryIcon.DrawCategory(c, new SKRect(x + 80, top + 3, x + 108, top + 21), move.Category);
        var low = move.MaxPP > 0 && move.PP * 4 <= move.MaxPP;
        var pp = move.PPUps > 0 ? $"PP {move.PP}/{move.MaxPP} +{move.PPUps}" : $"PP {move.PP}/{move.MaxPP}";
        var ppWidth = SummaryInk.Draw(c, pp, x + w - 8, SummaryInk.Center(top + 12, 14), 14, low ? new SKColor(0xFF, 0x9A, 0x5A) : Pksm.Ink, align: SKTextAlign.Right);
        SummaryInk.Draw(c, SummaryInk.Fit(move.Name, 15, w - 124 - ppWidth - 16, bold: true), x + 116, SummaryInk.Center(top + 12, 15), 15, Pksm.Ink, bold: true);
        SummaryInk.Draw(c, $"{TypeFacts.CategoryName(move.Category)} · Power {InfoKit.Power(move.Power)} · Accuracy {InfoKit.Accuracy(move.Accuracy)}",
            x + 8, SummaryInk.Center(top + 34, 12.5f), 12.5f, Pksm.InkSoft);
        if (verdict is not null)
            SummaryInk.Draw(c, SummaryInk.Fit(verdict.Valid ? $"✓ Legal · {verdict.Reason}" : $"✗ Not legal · {verdict.Reason}", 12.5f, w - 16),
                x + 8, SummaryInk.Center(top + 52, 12.5f), 12.5f, verdict.Valid ? Pksm.Legal : Pksm.Illegal, bold: !verdict.Valid);
        var ly = SummaryInk.Center(top + 44 + verdictHeight + SummaryInk.Leading(13f) / 2, 13f);
        foreach (var line in effect) { SummaryInk.Draw(c, line, x + 8, ly, 13f, Pksm.Ink); ly += SummaryInk.Leading(13f); }
        return height;
    };

    private static float OriginPage(SKCanvas? c, MonSummary s, float w)
    {
        var accent = SummaryChrome.Accent(SummaryPage.Origin);
        var origin = new List<Row>();
        if (s.Met is { } met)
        {
            origin.Add(Fact("Game", met.VersionName.Length > 0 ? met.VersionName : "Unknown"));
            if (met.EggLocation > 0 && met.EggLocationName.Length > 0)
                origin.Add(Fact("Egg", met.EggLocationName, met.EggDate.Length > 0 ? $"Received {met.EggDate}" : null));
            origin.Add(Fact(met.EggLocation > 0 ? "Hatched" : "Met", met.MetLocationName.Length > 0 ? met.MetLocationName : "—",
                string.Join(" · ", new[]
                {
                    met.EggLocation > 0 ? null : met.MetLevel > 0 ? $"at Lv. {met.MetLevel}" : null,
                    met.MetDate.Length > 0 ? met.MetDate : null,
                }.Where(x => x is not null))));
            if (met.LanguageName.Length > 0) origin.Add(Fact("Language", met.LanguageName));
            if (met.Fateful) origin.Add(Note("Fateful encounter: an event or gift Pokémon.", Pksm.Legal));
        }
        else
        {
            origin.Add(Note("This game's met data isn't readable yet."));
        }
        origin.Add(Fact("Format", $"{s.Format.ToUpperInvariant()} · Generation {s.Generation}"));
        if (s.Fields?.Pid is { } pid)
            origin.Add(Fact("PID", pid.ToString("X8"), s.Fields.EncryptionConstant is { } ec ? $"Encryption constant {ec:X8}" : null));
        var y = Section(c, 0, w, "Origin", accent, origin);

        var memories = new List<Row>();
        if (s.Fields?.OtMemory is { } otMemory) memories.Add(Text(otMemory, Pksm.Ink));
        if (s.Fields?.HandlerMemory is { } htMemory) memories.Add(Text(htMemory, Pksm.Ink));
        if (memories.Count > 0) y = Section(c, y, w, "Memories", accent, memories);

        var total = s.RibbonCount + s.MarkCount;
        var ribbons = new List<Row>
        {
            total > 0 ? Text(string.Join("  ·  ", s.RibbonNames), Pksm.RibbonGold) : Text("No ribbons or marks yet.", Pksm.InkSoft),
        };
        return Section(c, y, w, "Ribbons & marks", accent, ribbons,
            trailing: total > 0 ? $"{s.RibbonCount} ribbon(s) · {s.MarkCount} mark(s)" : null);
    }

    private float LegalityPage(SKCanvas? c, MonSummary s, float w)
    {
        var (title, tone) = _legalityPending ? ("Checking legality…", Pksm.InkSoft)
            : s.Legal switch
            {
                true => ("✓ Legal", Pksm.Legal),
                false => ("✗ Not legal", Pksm.Illegal),
                _ => ("Not checked", Pksm.InkSoft),
            };
        var rows = new List<Row> { Text(title, tone, 18) };
        if (!_legalityPending && s.Legal is null)
            rows.Add(Text("Offline legality analysis isn't available for this game.", Pksm.Ink));
        foreach (var line in s.LegalityLines.Where(l => !string.IsNullOrWhiteSpace(l)))
            rows.Add(Text(line, Pksm.Ink, 13f));
        return Section(c, 0, w, "Legality", SummaryChrome.Accent(SummaryPage.Legality), rows);
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Final stats as the ORAS hexagon: HP on top, clockwise. The largest stat reaches the
    /// outer ring so the build's shape reads at a glance; the numbers sit at each vertex.
    /// </summary>
    private static void PaintRadar(SKCanvas canvas, SKRect r, MonSummary s, SKColor accent)
    {
        var stats = s.Stats;
        var cx = r.MidX;
        var cy = r.MidY + 2;
        var radius = Math.Min(r.Width, r.Height) * 0.29f;
        var max = Math.Max(1, stats.Max());

        SKPoint Vertex(int i, float rr)
        {
            var angle = (float)(-Math.PI / 2 + i * Math.PI / 3);
            return new SKPoint(cx + rr * (float)Math.Cos(angle), cy + rr * (float)Math.Sin(angle));
        }
        SKPath Hexagon(float rr)
        {
            var path = new SKPath();
            for (var i = 0; i < 6; i++)
            {
                var p = Vertex(i, rr);
                if (i == 0) path.MoveTo(p); else path.LineTo(p);
            }
            path.Close();
            return path;
        }

        // The stage: a navy hexagon with a soft radial light, cobalt rings, the build in the page accent.
        using (var outer = Hexagon(radius))
        {
            using var shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), radius,
                [SummaryChrome.Lighter(Pksm.Paper, 0.1f), Pksm.PaperShade], SKShaderTileMode.Clamp);
            using var stage = new SKPaint { IsAntialias = true, Shader = shader };
            canvas.DrawPath(outer, stage);
            using var rim = new SKPaint { Color = Pksm.PaperEdge, Style = SKPaintStyle.Stroke, StrokeWidth = 2, IsAntialias = true };
            canvas.DrawPath(outer, rim);
        }
        using var grid = new SKPaint { Color = Pksm.PaperEdge.WithAlpha(0x90), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };
        for (var ring = 1; ring <= 3; ring++)
        {
            using var ringPath = Hexagon(radius * ring / 4f);
            canvas.DrawPath(ringPath, grid);
        }
        for (var i = 0; i < 6; i++)
            canvas.DrawLine(cx, cy, Vertex(i, radius).X, Vertex(i, radius).Y, grid);

        using var shape = new SKPath();
        for (var i = 0; i < 6; i++)
        {
            var p = Vertex(i, radius * Math.Max(0.06f, stats[i] / (float)max));
            if (i == 0) shape.MoveTo(p); else shape.LineTo(p);
        }
        shape.Close();
        using var fill = new SKPaint { Color = SummaryChrome.Lighter(accent, 0.25f).WithAlpha(0xB8), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var edge = new SKPaint { Color = SummaryChrome.Lighter(accent, 0.55f), Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
        canvas.DrawPath(shape, fill);
        canvas.DrawPath(shape, edge);

        for (var i = 0; i < 6; i++)
        {
            var label = Vertex(i, radius + 14);
            var align = Math.Abs(label.X - cx) < 4 ? SKTextAlign.Center : label.X < cx ? SKTextAlign.Right : SKTextAlign.Left;
            var capTone = s.NatureUp == i ? Raised : s.NatureDown == i ? Lowered : Pksm.InkSoft;
            // Caption over value; the top vertex stacks upward, the bottom one downward.
            var top = i == 0 ? label.Y - 12 : i == 3 ? label.Y + 4 : label.Y - 5;
            SummaryInk.Draw(canvas, StatCaps[i], label.X, top, 12.5f, capTone, align: align);
            SummaryInk.Draw(canvas, stats[i].ToString(), label.X, top + 15, 14.5f, Pksm.Ink, bold: true, align: align);
        }
    }

    /// <summary>SKImage wrappers per bitmap: never copy a 512px render on every GIF tick.</summary>
    private static readonly ConditionalWeakTable<SKBitmap, SKImage> Images = new();

    private static SKImage ImageOf(SKBitmap bitmap)
    {
        if (Images.TryGetValue(bitmap, out var image)) return image;
        // Decoded sprites are never written again: immutable lets the image share the pixels.
        bitmap.SetImmutable();
        image = SKImage.FromBitmap(bitmap);
        Images.AddOrUpdate(bitmap, image);
        return image;
    }

    private static readonly SKPaint StageFill = new() { IsAntialias = true };

    /// <summary>The stage geometry in dp: the inner stage rect, the floor line and the sprite box.</summary>
    private static (SKRect Stage, float Floor, SKRect Box) PortraitGeometry(float w, float h)
    {
        var panel = new SKRect(0, 0, w - ShadowInset, h - ShadowInset);
        var stage = SKRect.Inflate(panel, -5, -5);
        var size = Math.Max(1, Math.Min(stage.Width, stage.Height));
        var floor = stage.MidY + size * 0.36f;
        var box = size * 0.8f;
        return (stage, floor, new SKRect(stage.MidX - box / 2, floor - box, stage.MidX + box / 2, floor));
    }

    /// <summary>Sizes the sprite layer to the sprite box only: a GIF tick uploads that, not the whole portrait.</summary>
    private void PlaceSprite()
    {
        if (_portrait.Width <= 0 || _portrait.Height <= 0) return;
        var (_, _, box) = PortraitGeometry((float)_portrait.Width, (float)_portrait.Height);
        // A few dp of slack under the floor for pixel sprites' transparent margins.
        _sprite.Margin = new Thickness(box.Left, box.Top, 0, 0);
        _sprite.WidthRequest = box.Width;
        _sprite.HeightRequest = box.Height + box.Height * 0.08f;
    }

    /// <summary>
    /// The portrait stage: a device panel holding a soft 3DS light (cobalt falling to navy),
    /// the floor shadow the mon stands on, and the ball in the corner. Repaints once per mon.
    /// </summary>
    private void PaintStage(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var w = BeginDp(canvas, args.Info, _stage);
        var h = args.Info.Height * (w / args.Info.Width);
        var (stage, floorY, box) = PortraitGeometry(w, h);
        SummaryChrome.Panel(canvas, new SKRect(0, 0, w - ShadowInset, h - ShadowInset));
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, stage.Top), new SKPoint(0, stage.Bottom),
                   [SummaryChrome.Mix(Pksm.LogoGrid, Pksm.Paper, 0.3f), Pksm.Paper, Pksm.PaperShade], [0, 0.6f, 1], SKShaderTileMode.Clamp))
        {
            StageFill.Shader = shader;
            canvas.DrawRoundRect(stage, 3, 3, StageFill);
            StageFill.Shader = null;
        }
        if (_summary is not { } s) return;
        using (var floor = SKShader.CreateRadialGradient(new SKPoint(0, 0), 1, [Pksm.LogoVoid.WithAlpha(0x80), Pksm.LogoVoid.WithAlpha(0)], SKShaderTileMode.Clamp))
        {
            canvas.Save();
            canvas.Translate(stage.MidX, floorY);
            canvas.Scale(box.Width * 0.42f, box.Width * 0.075f);
            StageFill.Shader = floor;
            canvas.DrawCircle(0, 0, 1, StageFill);
            StageFill.Shader = null;
            canvas.Restore();
        }
        var ball = _sprites.GetBall(s.Ball);
        if (ball is null)
        {
            _sprites.WarmBall(s.Ball, () => MainThread.BeginInvokeOnMainThread(_stage.InvalidateSurface));
            return;
        }
        var ballSize = Math.Clamp(Math.Min(stage.Width, stage.Height) * 0.13f, 18, 30);
        canvas.DrawImage(ImageOf(ball), new SKRect(stage.Left + 6, stage.Top + 6, stage.Left + 6 + ballSize, stage.Top + 6 + ballSize),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
    }

    /// <summary>
    /// The mon on its stage: animated Showdown sprite → HOME render → pixel sprite, feet on
    /// the floor. While animation availability is still unknown nothing is drawn: a calm
    /// empty beat beats a 2D sprite flashing into a GIF. Animated sprites paint at dp
    /// resolution (they are low-res art anyway), which keeps a GIF tick cheap.
    /// </summary>
    private void PaintSprite(object? sender, SKPaintSurfaceEventArgs args)
    {
        var canvas = args.Surface.Canvas;
        var w = BeginDp(canvas, args.Info, _sprite);
        var h = args.Info.Height * (w / args.Info.Width);
        _spriteAnimated = false;
        if (_summary is not { } s) return;
        void Redraw() => MainThread.BeginInvokeOnMainThread(_sprite.InvalidateSurface);
        var floorY = w; // the box is square; the extra height hangs under the floor
        if (s.IsEgg)
        {
            // No egg art ships with the sprite set: a plain speckled egg keeps the species a surprise.
            var eh = w * 0.62f;
            var egg = new SKRect(w / 2 - eh * 0.38f, floorY - eh, w / 2 + eh * 0.38f, floorY);
            using var shell = new SKPaint { Color = new SKColor(0xF4, 0xEE, 0xD8), IsAntialias = true };
            using var spot = new SKPaint { Color = new SKColor(0x7F, 0xC4, 0x6A), IsAntialias = true };
            canvas.DrawOval(egg, shell);
            canvas.DrawCircle(egg.MidX - eh * 0.12f, egg.MidY - eh * 0.12f, eh * 0.08f, spot);
            canvas.DrawCircle(egg.MidX + eh * 0.14f, egg.MidY + eh * 0.08f, eh * 0.1f, spot);
            return;
        }
        // One look, one fallback order (SpriteCatalog): animated Showdown for the exact form →
        // HOME render for the exact form → bundled pixel sprite / artwork. A source without
        // art for this form answers "none" at once rather than showing the base form.
        var look = s.Look;
        if (!_sprites.TryGetShowdown(look, out var animated))
        {
            _sprites.WarmShowdown(look, Redraw);
            return;
        }
        _spriteAnimated = animated is not null;
        if (_sprite.IgnorePixelScaling != _spriteAnimated)
        {
            // Switching resolution re-sizes the surface; it repaints itself right after.
            _sprite.IgnorePixelScaling = _spriteAnimated;
            Redraw();
            return;
        }
        var home = animated is null ? _sprites.GetHome(look) : null;
        if (animated is null && home is null) _sprites.WarmHome(look, Redraw);
        var bitmap = animated?.FrameAt(_elapsedMs) ?? home ?? _sprites.GetSprite(look);
        if (bitmap is null)
        {
            _sprites.Warm(look, Redraw);
            return;
        }
        var smooth = home is not null;
        var fit = w * (animated is not null ? 0.9f : 1f);
        var scale = Math.Min(fit / bitmap.Width, fit / bitmap.Height);
        var bw = bitmap.Width * scale;
        var bh = bitmap.Height * scale;
        // Feet on the floor: pixel sprites carry transparent margins, so they sit a touch lower.
        var bottom = Math.Min(h, floorY + (smooth ? 0 : bh * 0.06f));
        var dest = new SKRect(w / 2 - bw / 2, bottom - bh, w / 2 + bw / 2, bottom);
        canvas.DrawImage(ImageOf(bitmap), dest, smooth || animated is not null
            ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)
            : new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
    }

    /// <summary>The GIF loop only ticks while this view is on screen with a mon to show.</summary>
    private void UpdateTimer()
    {
        if (_summary is null || !IsLoaded) { StopTimer(); return; }
        if (_timer is not null) return;
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(40);
        _timer.Tick += (_, _) =>
        {
            _elapsedMs += 40;
            if (_spriteAnimated && _hero.IsVisible) _sprite.InvalidateSurface();
        };
        _timer.Start();
    }

    private void StopTimer()
    {
        StopFling();
        _timer?.Stop();
        _timer = null;
    }
}
