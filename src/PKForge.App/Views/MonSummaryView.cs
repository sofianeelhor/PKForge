using System.Diagnostics;
using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// The read-only Pokémon summary in the designer's art direction, laid out like the
/// 1240×1080 second screen it was drawn for: on the left the name banner, the Pokémon
/// (Showdown's static front sprite, or the stats hexagon on the Stats page) and the species
/// card (legality, types, ability, item); on the right five icon tabs over a panel of
/// sections - INFO (trainer, character, items and care), STATS (the stat table and
/// potential), MOVES (type, category, numbers, PP, verdict, effect), ORIGIN (met data,
/// memories, ribbons) and LEGALITY (the verdict and every check by topic). One component
/// serves the second screen, the Bank's inspector and the full-screen summary; it scales
/// with its host, one design pixel being <see cref="_unit"/> device-independent units.
/// <para>
/// Performance: the view is built once and never rebuilt. It is three Skia canvases (the
/// left column, the tabs, the page) and a label, so a new Pokémon or a page turn is a
/// repaint: no view-tree churn, no layout storm.
/// </para>
/// </summary>
public sealed class MonSummaryView : ContentView
{
    // The design: a 1240×1080 screen split at 648 design pixels.
    private const float DesignWidth = 1240, DesignHeight = 1080, LeftWidth = 648, TabsHeight = 118;
    // The panel's top rim runs through the middle of the tab strip, so the tabs sit across it.
    private const float PanelTop = 73, ContentTop = 150, PX0 = 20;
    private const float LabelX = 42, ValueX = 282, LabelColumnRight = 252;

    private static readonly string[] StatCaps = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];
    private static readonly string[] MarkingGlyphs = ["●", "▲", "■", "♥", "★", "◆"];
    // The games' hexagon: HP on top, then clockwise Atk, Def, Spe, SpD, SpA.
    private static readonly int[] RadarOrder = [0, 1, 2, 5, 4, 3];
    private static readonly string[] TabIcons = ["tab_info", "tab_stats", "tab_moves", "tab_origin", "tab_legality"];

    private static SKColor PanelFill => ColorTheme.Current.PanelFill;
    private static SKColor LabelColumn => ColorTheme.Current.LabelColumn;
    private static SKColor LabelInk => ColorTheme.Current.LabelInk;
    private static SKColor ValueInk => ColorTheme.Current.ValueInk;
    private static SKColor SubInk => ColorTheme.Current.SubInk;
    private static SKColor Cyan => ColorTheme.Current.Rim;
    private static readonly SKColor Orange = new(0xFF, 0x9A, 0x5A);
    private static readonly SKColor Gold = new(0xE2, 0xB6, 0x4A);
    private static readonly SKColor ReasonInk = new(0xAA, 0x96, 0xA0); // legality reasons: muted rose, a signal
    private static SKColor CardFill => ColorTheme.Current.CardFill;
    private static SKColor CardRow => ColorTheme.Current.CardRow;
    private static SKColor CardEdge => ColorTheme.Current.LabelInk;
    private static SKColor SpeciesFill => ColorTheme.Current.SpeciesFill;
    private static SKColor SpeciesEdge => ColorTheme.Current.SpeciesEdge;
    private static SKColor SpeciesInk => ColorTheme.Current.SpeciesInk;
    private static SKColor NameFrame => ColorTheme.Current.NameFrame;
    private static readonly SKColor Raised = new(0xFA, 0x8C, 0x96);
    private static readonly SKColor Lowered = new(0x82, 0xB4, 0xFA);

    private readonly ISpriteService _sprites;
    private readonly Grid _layout = new();
    private readonly Grid _right = new();
    private readonly SKCanvasView _left = new() { InputTransparent = true };
    private readonly SKCanvasView _tabs = new() { EnableTouchEvents = true };
    // The page body scrolls inside its own canvas: one viewport-sized surface painted at
    // _scrollY (design pixels) and driven by its own touch stream (drag, fling, sideways swipe to turn).
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
    // Device-independent units per design pixel.
    private float _unit = 0.5f;

    /// <summary>Raised when a tab tap or swipe turns the page (hosts keep their own page state in sync).</summary>
    public event Action<SummaryPage>? PageChanged;

    /// <summary>How to repair a Pokémon that is not legal, in the host's own controls.</summary>
    public string FixHint { get; set; } = "Legalize finds the closest legal version. Its PID, nature or IVs may change.";

    public MonSummaryView(ISpriteService sprites)
    {
        _sprites = sprites;
        _left.PaintSurface += PaintGuard.Wrap("MonSummaryView.PaintLeft", PaintLeft);
        _tabs.PaintSurface += PaintGuard.Wrap("MonSummaryView.PaintTabs", PaintTabs);
        _tabs.Touch += OnTabTouch;
        _page.PaintSurface += PaintGuard.Wrap("MonSummaryView.PaintPage", PaintPage);
        _page.Touch += OnPageTouch;

        _right.RowDefinitions.Add(new RowDefinition(new GridLength(TabsHeight * _unit)));
        _right.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        _right.Children.Add(_page);
        Grid.SetRowSpan(_page, 2);
        _right.Children.Add(_tabs);

        _layout.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(LeftWidth * _unit)));
        _layout.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        _layout.Children.Add(_left);
        _layout.Children.Add(_right);
        Grid.SetColumn(_right, 1);
        _layout.Children.Add(_empty);
        Grid.SetColumnSpan(_empty, 2);
        Content = _layout;

        SizeChanged += (_, _) => ApplyScale();
        _page.SizeChanged += (_, _) => RefreshPage(resetScroll: false);
        Unloaded += (_, _) => StopFling();
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
        if (_left.IsVisible != has) _left.IsVisible = _right.IsVisible = has;
        if (_empty.IsVisible == has) _empty.IsVisible = !has;
        if (!has) _empty.Text = emptyText ?? "Empty slot";
        _left.InvalidateSurface();
        // The same mon with its verdict arriving keeps its scroll: only the verdict page repaints.
        if (!sameMon || _pageKind == SummaryPage.Legality) RefreshPage(resetScroll: !sameMon);
        PerfTrace.Log($"summary.show[{_pageKind}]", watch);
        PerfTrace.UntilIdle($"summary.show[{_pageKind}]", Dispatcher);
    }

    /// <summary>The same entity decoded twice (quick, then with its verdict), not merely the same species.</summary>
    private static bool SameMon(MonSummary? a, MonSummary? b) =>
        a is not null && b is not null && a.Species == b.Species && a.Form == b.Form && a.IsShiny == b.IsShiny
        && a.Ball == b.Ball && a.IsEgg == b.IsEgg && a.Nickname == b.Nickname && a.Level == b.Level && a.Gender == b.Gender
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
        // The Stats page swaps the sprite for the hexagon.
        _left.InvalidateSurface();
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
        var viewport = _page.Height / _unit - ContentTop;
        var target = Math.Clamp(_scrollY + direction * Math.Max(120, viewport * 0.6), 0, MaxScroll);
        this.AbortAnimation(ScrollAnimation);
        new Animation(ScrollTo, _scrollY, target).Commit(this, ScrollAnimation, length: 180, easing: Easing.CubicOut);
    }

    // ── Layout: one design pixel is _unit dp, the whole design fitted into the host ──

    private void ApplyScale()
    {
        if (Width <= 0 || Height <= 0) return;
        var unit = (float)Math.Min(Width / DesignWidth, Height / DesignHeight);
        if (Math.Abs(unit - _unit) < 0.0001f) return;
        _unit = unit;
        _layout.ColumnDefinitions[0].Width = new GridLength(LeftWidth * unit);
        _right.RowDefinitions[0].Height = new GridLength(TabsHeight * unit);
        _left.InvalidateSurface();
        _tabs.InvalidateSurface();
        RefreshPage(resetScroll: false);
    }

    /// <summary>Scales a canvas to design pixels; returns the canvas size in design pixels.</summary>
    private SKSize BeginDesign(SKCanvas canvas, SKImageInfo info, View view)
    {
        canvas.Clear(SKColors.Transparent);
        var pxPerDp = view.Width > 0 ? (float)(info.Width / view.Width) : (float)Math.Max(1, DeviceDisplay.MainDisplayInfo.Density);
        var scale = pxPerDp * _unit;
        canvas.Scale(scale);
        return new SKSize(info.Width / scale, info.Height / scale);
    }

    private async Task WarmFontsAsync()
    {
        await Task.WhenAll(PixelFont.WarmAsync(), PixelFont.WarmFallbackAsync());
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _left.InvalidateSurface();
            RefreshPage(resetScroll: false);
        });
    }

    // ── Touch: drag to scroll, fling, swipe to turn ──────────────────────────

    private const float TouchSlopDp = 8;
    private const float SwipeTurnDp = 56;
    private const double FlingFriction = 0.94; // velocity kept per 16 ms frame
    private SKPoint _touchStart;
    private double _touchStartScroll;
    private bool _dragging, _swiping;
    private readonly List<(long Ms, float Y)> _touchTrail = new();
    private VsyncLoop? _fling;
    private double _flingVelocity; // design pixels per ms

    private const string ScrollAnimation = "summary-scroll";

    private double MaxScroll => Math.Max(0, _contentHeight - (_page.Height / _unit - ContentTop));

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
                    ScrollTo(_touchStartScroll - dy / _unit);
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
        _flingVelocity = -(recent[^1].Y - recent[0].Y) / span / _unit;
        if (Math.Abs(_flingVelocity) < 0.1 / _unit) return;
        _fling ??= CreateFlingTimer();
        _fling.Start();
    }

    // Stepped on each screen refresh by the time that really passed (see VsyncLoop).
    private VsyncLoop CreateFlingTimer() => new(elapsed =>
    {
        var target = _scrollY + _flingVelocity * elapsed;
        _flingVelocity *= Math.Pow(FlingFriction, elapsed / 16.0);
        var more = !(target <= 0 || target >= MaxScroll || Math.Abs(_flingVelocity) < 0.02 / _unit);
        ScrollTo(target);
        return more;
    });

    private void StopFling() => _fling?.Stop();

    // ── Tabs ─────────────────────────────────────────────────────────────────

    private const float TabWidth = 132, TabSlant = 28, TabTop = 28, TabLeft = 12, TabStep = TabWidth - TabSlant;

    private static readonly Dictionary<string, SKImage?> Icons = new();

    private static SKImage? Icon(string name)
    {
        if (Icons.TryGetValue(name, out var image)) return image;
        try
        {
            using var stream = FileSystem.OpenAppPackageFileAsync($"ui/design/{name}.png").GetAwaiter().GetResult();
            using var bitmap = SKBitmap.Decode(stream);
            image = bitmap is null ? null : SKImage.FromBitmap(bitmap);
        }
        catch (Exception error) when (error is IOException or FileNotFoundException) { image = null; }
        Icons[name] = image;
        return image;
    }

    private void PaintTabs(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        BeginDesign(c, args.Info, _tabs);
        var bottom = TabsHeight;
        var right = TabLeft + TabStep * 4 + TabWidth;
        using (var strip = Parallelogram(new SKRect(TabLeft, TabTop, right, bottom), TabSlant))
        {
            Fill(c, strip, ColorTheme.Current.TabTop, ColorTheme.Current.TabBottom);
            Outline(c, strip, Cyan, 4);
        }
        using var edge = new SKPaint { Color = Cyan, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 };
        for (var i = 1; i < 5; i++)
        {
            var x = TabLeft + i * TabStep;
            c.DrawLine(x + TabSlant, TabTop, x, bottom, edge);
        }
        var active = (int)_pageKind;
        var ax = TabLeft + active * TabStep;
        using (var tab = Parallelogram(new SKRect(ax - 6, TabTop - 6, ax + TabWidth + 2, bottom + 2), TabSlant))
        {
            Fill(c, tab, ColorTheme.Current.TabActiveTop, ColorTheme.Current.TabActiveBottom);
            Outline(c, tab, Pksm.Ink, 4);
        }
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        using var tint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(ColorTheme.Current.TabIcon, SKBlendMode.SrcIn) };
        for (var i = 0; i < 5; i++)
        {
            if (Icon(TabIcons[i]) is not { } icon) continue;
            var cx = TabLeft + i * TabStep + TabWidth / 2;
            var cy = (TabTop + bottom) / 2;
            c.DrawImage(icon, new SKRect(cx - 30, cy - 30, cx + 30, cy + 30), sampling, tint);
        }
    }

    private void OnTabTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        if (args.ActionType != SKTouchAction.Pressed || _tabs.CanvasSize.Width <= 0) return;
        var designPerPx = (float)(_tabs.Width / _tabs.CanvasSize.Width) / _unit;
        var x = args.Location.X * designPerPx;
        var index = Math.Clamp((int)Math.Floor((x - TabLeft) / TabStep), 0, 4);
        SetPage((SummaryPage)index, raise: true);
    }

    // ── Left column: name banner, the Pokémon (or the hexagon), species card ──

    private void PaintLeft(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        var size = BeginDesign(c, args.Info, _left);
        if (_summary is not { } s) return;

        // The name banner, run off the left edge like the games' own.
        using (var frame = new SKPaint { Color = NameFrame, IsAntialias = true })
            c.DrawRoundRect(new SKRect(-40, 10, 600, 104), 26, 26, frame);
        using (var banner = Tab(new SKRect(-40, 34, 570, 118), 0, 40))
        {
            Fill(c, banner, StoragePaint.BannerTop, StoragePaint.BannerBottom);
            Outline(c, banner, Pksm.Ink, 4);
        }
        // The ball it was caught in sits at the start of the banner, as in the games.
        var name = s.IsEgg ? "Egg" : s.DisplayName;
        var hasBall = !s.IsEgg && DrawBall(c, s.Ball, new SKRect(28, 46, 88, 106));
        var nameLeft = hasBall ? 100f : 0f;
        // The gender sign closes the banner, in the games' blue and pink; eggs keep it a surprise.
        var (sign, signInk) = GenderSign(s);
        var nameRight = 540f;
        if (sign is not null)
        {
            var (signX, signBaseline, signLeft) = BannerSign(sign);
            SummaryInk.Draw(c, sign, signX, signBaseline, BannerSignSize, signInk, bold: true);
            nameRight = signLeft;
        }
        SummaryInk.Draw(c, SummaryInk.Fit(name, 44, nameRight - nameLeft - 20), (nameLeft + nameRight) / 2, SummaryInk.Center(77, 44), 44, Pksm.Ink, align: SKTextAlign.Center);
        // The level sits under the banner's end, the caption (box and slot) keeps the start.
        var level = s.IsEgg || s.Level <= 0 ? null : $"Lv. {s.Level}";
        if (level is not null)
        {
            // On a chip in the banner's frame colour, so it reads on every theme's backdrop.
            var chip = new SKRect(576 - SummaryInk.Width(level, 30, bold: true) - 36, 124, 590, 168);
            using (var frame = new SKPaint { Color = NameFrame, IsAntialias = true }) c.DrawRoundRect(chip, 22, 22, frame);
            SummaryInk.Draw(c, level, 572, SummaryInk.Center(146, 30), 30, Pksm.Ink, bold: true, align: SKTextAlign.Right);
        }
        if (!string.IsNullOrEmpty(_caption))
            SummaryInk.Draw(c, SummaryInk.Fit(_caption, 24, level is null ? 560 : 400), 24, SummaryInk.Center(146, 24), 24, SubInk);

        // The species card sits on the bottom edge; the Pokémon stands in the space above it.
        var cardTop = Math.Max(640, size.Height - 390);
        var middle = (TabsHeight + cardTop) / 2 + 6;
        if (_pageKind == SummaryPage.Stats && s.Stats.Count == 6) PaintRadar(c, 300, middle, s);
        else PaintPokemon(c, 300, middle, s);
        PaintSpeciesCard(c, cardTop, s);
    }

    // "Legal if it hatched from an egg": an amber between legal green and illegal red.
    private static readonly SKColor PossiblyLegal = new(0xE8, 0xB0, 0x4A);

    private const float BannerSignSize = 44;

    /// <summary>
    /// Where the gender sign sits in the name banner (top 34, bottom 118, its right edge slanting
    /// from x 530 to 570): its ink centred on the banner's middle and kept clear of the slanted
    /// edge at the glyph's top, where ♂'s arrow reaches. Returns the left-aligned draw position
    /// and the ink's left end, where the name's room stops.
    /// </summary>
    internal static (float X, float Baseline, float InkLeft) BannerSign(string sign)
    {
        const float top = 34, bottom = 118, edgeTop = 530, slant = 40, clearance = 16;
        var ink = SummaryInk.Bounds(sign, BannerSignSize, bold: true);
        var baseline = (top + bottom) / 2 - (ink.Top + ink.Bottom) / 2;
        var inkTop = baseline + ink.Top;
        var edge = edgeTop + slant * (inkTop - top) / (bottom - top);
        var x = MathF.Round(edge - clearance - ink.Right);
        return (x, MathF.Round(baseline), x + ink.Left);
    }

    /// <summary>♂ in the fixed signal blue, ♀ in pink; none for genderless Pokémon and eggs.</summary>
    internal static (string? Sign, SKColor Ink) GenderSign(MonSummary s) => s.IsEgg ? (null, default) : s.Gender switch
    {
        0 => ("♂", Pksm.SignalBlue),
        1 => ("♀", Pksm.Female),
        _ => (null, default),
    };

    /// <summary>Draws the ball icon (20 px pixel art) into a square; false while it loads or when there is none.</summary>
    private bool DrawBall(SKCanvas c, int ball, SKRect square)
    {
        var bitmap = _sprites.GetBall(ball);
        if (bitmap is null)
        {
            _sprites.WarmBall(ball, () => MainThread.BeginInvokeOnMainThread(() => { _left.InvalidateSurface(); _page.InvalidateSurface(); }));
            return false;
        }
        var image = Services.SpriteImages.Of(bitmap);
        c.DrawImage(image, square, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        return true;
    }

    /// <summary>
    /// Showdown's static front sprite for the exact form (shiny when shiny), trimmed and
    /// scaled by a whole number so its pixels stay square; PKHeX's art when Showdown has none.
    /// </summary>
    private void PaintPokemon(SKCanvas c, float cx, float cy, MonSummary s)
    {
        void Redraw() => MainThread.BeginInvokeOnMainThread(_left.InvalidateSurface);
        if (s.IsEgg)
        {
            // The artist's egg, by whole steps (20× here, 240×280), keeps the species a surprise.
            EggSprite.Draw(c, new SKRect(cx - 130, cy - 140, cx + 130, cy + 140));
            return;
        }
        if (!_sprites.TryGetShowdownFront(s.Look, Redraw, out var sprite)) return;
        if (sprite is null)
        {
            sprite = _sprites.GetSprite(s.Look);
            if (sprite is null) { _sprites.Warm(s.Look, Redraw); return; }
        }
        var k = Math.Clamp(MathF.Floor(Math.Min(520f / sprite.Width, 500f / sprite.Height)), 1, 7);
        var w = sprite.Width * k;
        var h = sprite.Height * k;
        var image = Services.SpriteImages.Of(sprite);
        c.DrawImage(image, new SKRect(cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2),
            new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
    }

    /// <summary>
    /// The stats hexagon: the final stats (the largest near the rim) over the base stats, the
    /// games' order clockwise from HP, the nature's raised and lowered stats tinted.
    /// </summary>
    private static void PaintRadar(SKCanvas c, float cx, float cy, MonSummary s)
    {
        const float radius = 158;
        var top = Math.Max(1, s.Stats.Max()) * 1.08f;
        SKPoint Point(int i, float r)
        {
            var angle = -MathF.PI / 2 + i * MathF.PI / 3;
            return new SKPoint(cx + r * MathF.Cos(angle), cy + r * MathF.Sin(angle));
        }
        SKPath Polygon(Func<int, float> r)
        {
            var path = new SKPath();
            for (var i = 0; i < 6; i++)
            {
                var p = Point(i, r(i));
                if (i == 0) path.MoveTo(p); else path.LineTo(p);
            }
            path.Close();
            return path;
        }

        using (var back = Polygon(_ => radius + 20))
        using (var paint = new SKPaint { Color = StoragePaint.Well.WithAlpha(215), IsAntialias = true })
            c.DrawPath(back, paint);
        using (var ring = new SKPaint { Color = EditorPaint.Label, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 })
        {
            foreach (var fraction in new[] { 0.25f, 0.5f, 0.75f, 1f })
            {
                using var path = Polygon(_ => radius * fraction);
                c.DrawPath(path, ring);
            }
            ring.StrokeWidth = 2;
            ring.Color = ring.Color.WithAlpha(200);
            for (var i = 0; i < 6; i++) c.DrawLine(new SKPoint(cx, cy), Point(i, radius), ring);
        }
        using (var basePath = Polygon(i => radius * Math.Min(1f, (RadarOrder[i] < s.BaseStats.Count ? s.BaseStats[RadarOrder[i]] : 0) / 180f)))
        using (var baseFill = new SKPaint { Color = ColorTheme.Current.RadarFill.WithAlpha(50), IsAntialias = true })
            c.DrawPath(basePath, baseFill);
        using (var shape = Polygon(i => radius * Math.Max(0.04f, s.Stats[RadarOrder[i]] / top)))
        {
            using var fill = new SKPaint { Color = Cyan.WithAlpha(120), IsAntialias = true };
            using var line = new SKPaint { Color = ColorTheme.Current.RadarInk, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4, StrokeJoin = SKStrokeJoin.Round };
            c.DrawPath(shape, fill);
            c.DrawPath(shape, line);
            using var dot = new SKPaint { Color = ColorTheme.Current.RadarInk, IsAntialias = true };
            for (var i = 0; i < 6; i++) c.DrawCircle(Point(i, radius * Math.Max(0.04f, s.Stats[RadarOrder[i]] / top)), 9, dot);
        }
        var nature = s.Fields?.StatNature ?? s.Nature;
        var up = nature is { } n1 ? NatureFacts.Raised(n1) : null;
        var down = nature is { } n2 ? NatureFacts.Lowered(n2) : null;
        for (var i = 0; i < 6; i++)
        {
            var stat = RadarOrder[i];
            var label = Point(i, radius + 58);
            var tone = up == stat ? Raised : down == stat ? Lowered : ValueInk;
            SummaryInk.Draw(c, StatCaps[stat], label.X, SummaryInk.Center(label.Y - 16, 30), 30, tone, align: SKTextAlign.Center);
            SummaryInk.Draw(c, s.Stats[stat].ToString(), label.X, SummaryInk.Center(label.Y + 15, 30), 30, Pksm.Ink, align: SKTextAlign.Center);
        }
    }

    /// <summary>The species card: species tab with the legality status, the type plates, ability and item.</summary>
    private void PaintSpeciesCard(SKCanvas c, float top, MonSummary s)
    {
        var card = new SKRect(24, top, 590, top + 430);
        using (var shadow = new SKPaint { Color = ColorTheme.Current.Shadow.WithAlpha(150), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 7) })
            c.DrawRoundRect(SKRect.Create(card.Left, card.Top + 8, card.Width, card.Height), 28, 28, shadow);
        using (var fill = new SKPaint { Color = CardFill, IsAntialias = true }) c.DrawRoundRect(card, 28, 28, fill);

        // Ability and item rows first, so the card's rim and the tab draw over their ends.
        var ability = s.IsEgg ? "—" : s.AbilityName ?? "—";
        var item = s.IsEgg ? "—" : s.HeldItemName ?? "None";
        (string Label, string Value)[] rows = [("Ability", ability), ("Item", item)];
        for (var i = 0; i < rows.Length; i++)
        {
            var y = top + 194 + i * 96;
            using var band = new SKPaint { Color = i == 0 ? CardRow : CardFill };
            c.DrawRect(new SKRect(27, y, 587, y + 92), band);
            SummaryInk.Draw(c, rows[i].Label, 60, SummaryInk.Center(y + 46, 40), 40, LabelInk);
            SummaryInk.Draw(c, SummaryInk.Fit(rows[i].Value, 40, 360), 560, SummaryInk.Center(y + 46, 40), 40, ValueInk, align: SKTextAlign.Right);
        }
        using (var rim = new SKPaint { Color = CardEdge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 })
            c.DrawRoundRect(SKRect.Inflate(card, -1.5f, -1.5f), 28, 28, rim);

        using (var tab = Tab(new SKRect(24, top, 420, top + 78), 24, 36))
        {
            using var fill = new SKPaint { Color = SpeciesFill, IsAntialias = true };
            c.DrawPath(tab, fill);
            Outline(c, tab, SpeciesEdge, 3);
        }
        var species = s.FormName.Length > 0 ? $"{s.SpeciesName} ({s.FormName})" : s.SpeciesName;
        SummaryInk.Draw(c, SummaryInk.Fit(species, 42, 330), 52, SummaryInk.Center(top + 39, 42), 42, SpeciesInk);
        var (status, tone) = Status(s);
        SummaryInk.Draw(c, SummaryInk.Fit(status, 36, 150), 572, SummaryInk.Center(top + 39, 36), 36, tone, align: SKTextAlign.Right);

        if (s.IsEgg) return;
        var types = s.Types.Where(TypeFacts.IsValid).ToList();
        const float plate = 184, gap = 20;
        var x = 307 - (types.Count * plate + Math.Max(0, types.Count - 1) * gap) / 2;
        foreach (var type in types)
        {
            TypePlates.Paint(c, new SKRect(x, top + 106, x + plate, top + 166), type);
            x += plate + gap;
        }
    }

    private (string Text, SKColor Color) Status(MonSummary s)
    {
        if (_legalityPending) return ("Checking…", ValueInk);
        return s.Legal switch
        {
            true => ("Legal", Pksm.Legal),
            false => ("Illegal", Pksm.Illegal),
            _ => ("Not checked", ValueInk),
        };
    }

    // ── Page ─────────────────────────────────────────────────────────────────

    /// <summary>Measures the page; the scroll keeps within it.</summary>
    private void RefreshPage(bool resetScroll)
    {
        if (_summary is null || _page.Width <= 0) return;
        var width = (float)(_page.Width / _unit);
        _contentHeight = RenderPage(null, _summary, width - PX0) + 30;
        if (resetScroll)
        {
            StopFling();
            this.AbortAnimation(ScrollAnimation);
            _scrollY = 0;
        }
        else _scrollY = Math.Clamp(_scrollY, 0, MaxScroll);
        _page.InvalidateSurface();
    }

    private void PaintPage(object? sender, SKPaintSurfaceEventArgs args)
    {
        var watch = Stopwatch.StartNew();
        var c = args.Surface.Canvas;
        var size = BeginDesign(c, args.Info, _page);
        if (_summary is not { } s) return;

        // The panel the tabs sit on, running off the right and bottom edges.
        var panel = new SKRect(0, PanelTop, size.Width + 40, size.Height + 40);
        using (var shadow = new SKPaint { Color = ColorTheme.Current.Shadow.WithAlpha(150), IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 7) })
            c.DrawRoundRect(SKRect.Create(panel.Left - 6, panel.Top + 8, panel.Width, panel.Height), 40, 40, shadow);
        using (var fill = new SKPaint { Color = PanelFill, IsAntialias = true }) c.DrawRoundRect(panel, 40, 40, fill);
        using (var rim = new SKPaint { Color = Cyan, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4 })
            c.DrawRoundRect(SKRect.Inflate(panel, -2, -2), 38, 38, rim);

        c.Save();
        c.ClipRect(new SKRect(4, TabsHeight + 4, size.Width, size.Height)); // scrolled rows never show between the tabs
        c.Translate(0, ContentTop - (float)_scrollY);
        RenderPage(c, s, size.Width - PX0);
        c.Restore();

        var viewport = size.Height - ContentTop;
        if (MaxScroll > 0 && viewport > 0)
        {
            var length = (float)Math.Max(40, viewport * viewport / _contentHeight);
            var y = ContentTop + (float)(_scrollY / MaxScroll) * (viewport - length);
            using var thumb = new SKPaint { IsAntialias = true, Color = ColorTheme.Current.Bright.WithAlpha(0x70) };
            c.DrawRoundRect(new SKRect(size.Width - 9, y, size.Width - 4, y + length), 2.5f, 2.5f, thumb);
        }
        PerfTrace.Log("summary.paint-page", watch);
    }

    /// <summary>One pass that both measures (canvas null) and paints from y 0; returns the page height.</summary>
    private float RenderPage(SKCanvas? c, MonSummary s, float right)
    {
        var blocks = _pageKind switch
        {
            SummaryPage.Info => InfoPage(s),
            SummaryPage.Stats => StatsPage(s),
            SummaryPage.Moves => MovesPage(s),
            SummaryPage.Origin => OriginPage(s, HistoryOf(s)),
            _ => LegalityPage(s),
        };
        var y = 0f;
        foreach (var block in blocks) y += block(c, y, right);
        return y;
    }

    /// <summary>A block of a page: paints at y (or only measures when c is null) between PX0 and right; returns its height.</summary>
    private delegate float Block(SKCanvas? c, float y, float right);

    /// <summary>A section divider: the title in a slanted chip between fading rails.</summary>
    private static Block Divider(string title) => (c, y, right) =>
    {
        if (c is not null)
            EditorPaint.PaintSection(c, new SKRect(PX0 - 18, y, right + 18, y + 96), title, PixelFont.Face, 34f / 30f);
        return 96;
    };

    /// <summary>
    /// A fact: the caption in its own darker column, the value beside it, and quiet detail
    /// lines under the value. <paramref name="art"/> draws the value instead (badges, glyphs).
    /// </summary>
    private static Block Row(string label, string value, string? detail = null, SKColor? tone = null, Action<SKCanvas, float, float>? art = null) => (c, y, right) =>
    {
        var details = string.IsNullOrWhiteSpace(detail) ? [] : SummaryInk.Wrap(detail, 26, right - ValueX - 8);
        var height = 98 + 34 * details.Count;
        if (c is null) return height;
        using (var column = new SKPaint { Color = LabelColumn }) c.DrawRect(new SKRect(4, y, LabelColumnRight, y + height - 6), column);
        var valueY = details.Count > 0 ? y + 46 : y + height / 2f;
        // A long caption ("Hidden Power") shrinks a little before it is cut.
        var labelSize = 38f;
        while (labelSize > 30 && SummaryInk.Width(label, labelSize) > LabelColumnRight - LabelX - 6) labelSize -= 2;
        SummaryInk.Draw(c, SummaryInk.Fit(label, labelSize, LabelColumnRight - LabelX - 6), LabelX, SummaryInk.Center(valueY, labelSize), labelSize, LabelInk);
        if (art is not null) art(c, ValueX, valueY);
        else SummaryInk.Draw(c, SummaryInk.Fit(value, 38, right - ValueX - 8), ValueX, SummaryInk.Center(valueY, 38), 38, tone ?? ValueInk);
        for (var i = 0; i < details.Count; i++)
            SummaryInk.Draw(c, details[i], ValueX, SummaryInk.Center(y + 92 + 34 * i, 26), 26, SubInk);
        GlowLine(c, PX0, right, y + height - 4);
        return height;
    };

    /// <summary>Centered free text (notes, lists).</summary>
    private static Block Text(string text, SKColor? tone = null) => (c, y, right) =>
    {
        var lines = SummaryInk.Wrap(text, 32, right - PX0 - 60);
        var height = 20 + 44 * lines.Count;
        if (c is null) return height;
        for (var i = 0; i < lines.Count; i++)
            SummaryInk.Draw(c, lines[i], (PX0 + right) / 2, SummaryInk.Center(y + 32 + 44 * i, 32), 32, tone ?? ValueInk, align: SKTextAlign.Center);
        return height;
    };

    /// <summary>The page's headline: a big coloured verdict and one line under it.</summary>
    private static Block Hero(string title, SKColor tone, string detail) => (c, y, right) =>
    {
        if (c is null) return 150;
        var mid = (PX0 + right) / 2;
        SummaryInk.Draw(c, title, mid, SummaryInk.Center(y + 56, 64), 64, tone, align: SKTextAlign.Center);
        SummaryInk.Draw(c, SummaryInk.Fit(detail, 30, right - PX0 - 20), mid, SummaryInk.Center(y + 116, 30), 30, ValueInk, align: SKTextAlign.Center);
        return 150;
    };

    /// <summary>
    /// A separator line under a block: brightest at the middle, fading toward both ends.
    /// </summary>
    private static void GlowLine(SKCanvas c, float left, float right, float y)
    {
        (SKColor Color, byte Edge, byte Middle)[] rows =
        [
            (ColorTheme.Current.GlowHigh, 40, 210),
            (ColorTheme.Current.GlowMid, 30, 150),
            (ColorTheme.Current.GlowLow, 20, 80),
        ];
        for (var i = 0; i < rows.Length; i++)
        {
            var (color, edge, middle) = rows[i];
            using var shader = SKShader.CreateLinearGradient(new SKPoint(left, 0), new SKPoint(right, 0),
                [color.WithAlpha(edge), color.WithAlpha(middle), color.WithAlpha(edge)], SKShaderTileMode.Clamp);
            using var paint = new SKPaint { Shader = shader };
            c.DrawRect(new SKRect(left, y + i, right, y + i + 1), paint);
        }
    }

    // ── Info ─────────────────────────────────────────────────────────────────

    private List<Block> InfoPage(MonSummary s)
    {
        var blocks = new List<Block>();
        if (s.IsEgg)
        {
            blocks.Add(Divider("Egg"));
            blocks.Add(Text("This Pokémon is still an egg: its stats and moves appear once it hatches."));
        }

        var f = s.Fields;
        blocks.Add(Divider("Trainer"));
        blocks.Add(Row("OT", s.OriginalTrainer.Length > 0 ? s.OriginalTrainer : "—", f?.OtGender is { } otGender ? GenderWord(otGender) : null));
        if (s.TrainerId is { } tid)
            blocks.Add(Row("ID No.", $"{tid:00000}", s.SecretId is { } sid ? $"SID {sid:00000}" : null));
        if (f?.HandlerName is { } handler)
        {
            var about = new[] { f.HandlerGender is { } hg ? GenderWord(hg) : null, f.HandlerLanguage, f.HandlerFriendship is { } hf ? $"friendship {hf}" : null };
            blocks.Add(Row("Handler", handler, string.Join(" · ", about.Where(x => x is not null))));
            blocks.Add(Row("Now with", f.WithHandler == true ? handler : "Its original trainer",
                f.WithHandler == true && f.OtFriendship is { } otf ? $"OT friendship {otf}" : null));
        }

        blocks.Add(Divider("Character"));
        var character = blocks.Count;
        if (s.NatureName is not null && s.Nature is { } nature)
            blocks.Add(Row("Nature", s.NatureName, NatureFacts.IsNeutral(nature) ? "Neutral: no stat is raised or lowered." : NatureFacts.EffectLabel(nature)));
        if (f?.StatNature is { } mint)
            blocks.Add(Row("Mint", f.StatNatureName ?? $"#{mint}", $"Stats follow it: {(NatureFacts.IsNeutral(mint) ? "neutral" : NatureFacts.EffectLabel(mint))}"));
        if (f?.FormArgument is { } formArgument) blocks.Add(Row("Form", s.FormName.Length > 0 ? s.FormName : s.SpeciesName, formArgument));
        if (s.IsShiny && f?.Shiny is ShinyKind.Square or ShinyKind.Star)
            blocks.Add(Row("Shiny", f.Shiny == ShinyKind.Square ? "Square sparkles" : "Star sparkles"));
        if (s.Characteristic is not null) blocks.Add(Row("Trait", s.Characteristic));
        if (s.AbilityName is not null) blocks.Add(Row("Ability", s.AbilityName, s.AbilityEffect));
        if (blocks.Count == character) blocks.Add(Text("Gen 1 and 2 Pokémon have no nature or ability."));

        blocks.Add(Divider("Items & care"));
        blocks.Add(Row("Held item", s.HeldItemName ?? "None", s.HeldItemEffect));
        blocks.Add(Row("Ball", s.BallName, art: (canvas, x, cy) =>
        {
            var drawn = DrawBall(canvas, s.Ball, new SKRect(x, cy - 20, x + 40, cy + 20));
            var textX = drawn ? x + 54 : x;
            SummaryInk.Draw(canvas, s.BallName, textX, SummaryInk.Center(cy, 38), 38, ValueInk);
        }));
        if (!s.IsEgg) blocks.Add(Row("Friendship", $"{s.Friendship} / 255", FriendshipLine(s.Friendship)));
        if (s.Pokerus is { } rus)
            blocks.Add(rus.Status switch
            {
                PokerusStatus.Infectious => Row("Pokérus", "Infected", $"Strain {rus.Strain}, {rus.Days} day(s) left"),
                PokerusStatus.Cured => Row("Pokérus", "Cured", "EV gains stay doubled"),
                _ => Row("Pokérus", "Never infected"),
            });
        if (s.Markings.Count > 0) blocks.Add(Row("Markings", "", art: (canvas, x, cy) => Markings(canvas, x, cy, s.Markings)));
        return blocks;
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
            var color = value == 0 ? LabelInk : value == 2 ? Pksm.Illegal : Pksm.SignalBlue;
            x += SummaryInk.Draw(c, i < MarkingGlyphs.Length ? MarkingGlyphs[i] : "•", x, SummaryInk.Center(cy, 38), 38, color) + 10;
        }
    }

    // ── Stats ────────────────────────────────────────────────────────────────

    private const float StatColumn = 192, BaseColumn = 312, IvColumn = 412, EvColumn = 502;

    private static List<Block> StatsPage(MonSummary s)
    {
        var blocks = new List<Block>();
        if (s.Stats.Count == 6)
        {
            blocks.Add((c, y, _) =>
            {
                if (c is null) return 60;
                (float X, string Text)[] heads = [(StatColumn, "Stat"), (BaseColumn, "Base"), (IvColumn, s.ClassicTraining ? "DV" : "IV"), (EvColumn, s.ClassicTraining ? "Exp" : "EV")];
                foreach (var (x, text) in heads) SummaryInk.Draw(c, text, x, SummaryInk.Center(y + 30, 30), 30, LabelInk, align: SKTextAlign.Center);
                return 60;
            });
            // A mint moves the stat changes off the nature: tint what the stats actually follow.
            var statNature = s.Fields?.StatNature ?? s.Nature;
            var up = statNature is { } sn ? NatureFacts.Raised(sn) : null;
            var down = statNature is { } dn ? NatureFacts.Lowered(dn) : null;
            for (var i = 0; i < 6; i++)
            {
                var stat = i;
                blocks.Add((c, y, right) =>
                {
                    if (c is null) return 84;
                    var mid = y + 42;
                    var tone = up == stat ? Raised : down == stat ? Lowered : LabelInk;
                    SummaryInk.Draw(c, StatCaps[stat], LabelX, SummaryInk.Center(mid, 40), 40, tone);
                    SummaryInk.Draw(c, s.Stats[stat].ToString(), StatColumn, SummaryInk.Center(mid, 40), 40, ValueInk, align: SKTextAlign.Center);
                    var baseValue = stat < s.BaseStats.Count ? s.BaseStats[stat] : 0;
                    var iv = stat < s.IVs.Count ? s.IVs[stat] : 0;
                    var ev = stat < s.EVs.Count ? s.EVs[stat] : 0;
                    SummaryInk.Draw(c, baseValue.ToString(), BaseColumn, SummaryInk.Center(mid, 36), 36, ValueInk, align: SKTextAlign.Center);
                    SummaryInk.Draw(c, iv.ToString(), IvColumn, SummaryInk.Center(mid, 36), 36, iv >= s.Caps.IvMax ? Gold : ValueInk, align: SKTextAlign.Center);
                    SummaryInk.Draw(c, ev.ToString(), EvColumn, SummaryInk.Center(mid, 36), 36, ValueInk, align: SKTextAlign.Center);
                    GlowLine(c, PX0, right, y + 80);
                    return 84;
                });
            }
        }
        else blocks.Add(Text("No stats recorded."));

        var potential = new List<Block>();
        if (s.HiddenPowerType is { } hp)
            potential.Add(Row("Hidden Power", "", art: (canvas, x, cy) => TypePlates.Paint(canvas, new SKRect(x, cy - 24, x + 150, cy + 24), hp)));
        if (s.TeraType is { } tera && TypeFacts.IsValid(tera))
            potential.Add(Row("Tera Type", "", art: (canvas, x, cy) => TypePlates.Paint(canvas, new SKRect(x, cy - 24, x + 150, cy + 24), tera)));
        else if (s.TeraTypeName is not null) potential.Add(Row("Tera Type", s.TeraTypeName));
        if (s.Characteristic is not null) potential.Add(Row("Trait", s.Characteristic));
        if (potential.Count > 0)
        {
            blocks.Add(Divider("Potential"));
            blocks.AddRange(potential);
        }
        return blocks;
    }

    // ── Moves ────────────────────────────────────────────────────────────────

    private static List<Block> MovesPage(MonSummary s)
    {
        var blocks = new List<Block> { Divider("Moves") };
        if (s.Moves.Count == 0) blocks.Add(Text("No moves recorded."));
        // Verdicts cover the four slots; the page lists only the filled ones, in slot order.
        var verdicts = s.MoveVerdicts?.Moves.Where(v => v.Move > 0).ToList() ?? [];
        for (var i = 0; i < s.Moves.Count; i++)
            blocks.Add(MoveBlock(s.Moves[i], i < verdicts.Count && verdicts[i].Move == s.Moves[i].Id ? verdicts[i] : null));
        if (s.RelearnMoves.Count > 0)
        {
            blocks.Add(Divider("Relearnable"));
            blocks.Add(Text(string.Join("\u00A0· ", s.RelearnMoves)));
            foreach (var bad in s.MoveVerdicts?.Relearn.Where(v => !v.Valid) ?? [])
                blocks.Add(Text($"Illegal · {bad.Reason}", Pksm.Illegal));
        }
        if (s.Fields?.TechRecordCount is { } records)
        {
            blocks.Add(Divider("Technical Records"));
            blocks.Add(records > 0 ? Text(string.Join("\u00A0· ", s.Fields.TechRecordNames)) : Text("No records learned.", SubInk));
        }
        return blocks;
    }

    /// <summary>One move: type and category plates, name, numbers and PP, its verdict, its effect.</summary>
    private static Block MoveBlock(SummaryMove move, MoveVerdict? verdict) => (c, y, right) =>
    {
        var width = right - LabelX;
        var verdictLines = verdict is null ? [] : SummaryInk.Wrap($"{(verdict.Valid ? "Legal" : verdict.LegalIfHatched ? "Possibly legal" : "Illegal")} · {verdict.Reason}", 28, width);
        var effect = string.IsNullOrWhiteSpace(move.Effect) ? [] : SummaryInk.Wrap(move.Effect, 28, width);
        var height = 150 + 34 * (verdictLines.Count + effect.Count);
        if (c is null) return height;
        TypePlates.Paint(c, new SKRect(36, y + 18, 186, y + 66), move.Type);
        CategoryPlate(c, new SKRect(198, y + 18, 298, y + 66), move.Category);
        SummaryInk.Draw(c, SummaryInk.Fit(move.Name, 40, right - 316 - 8), 316, SummaryInk.Center(y + 42, 40), 40, Pksm.Ink);
        var low = move.MaxPP > 0 && move.PP * 4 <= move.MaxPP;
        var pp = move.PPUps > 0 ? $"PP {move.PP}/{move.MaxPP} +{move.PPUps}" : $"PP {move.PP}/{move.MaxPP}";
        var ppWidth = SummaryInk.Draw(c, pp, right - 12, SummaryInk.Center(y + 100, 32), 32, low ? Orange : ValueInk, align: SKTextAlign.Right);
        var numbers = $"Power {InfoKit.Power(move.Power)} · Accuracy {InfoKit.Accuracy(move.Accuracy)}";
        SummaryInk.Draw(c, SummaryInk.Fit(numbers, 28, right - 12 - ppWidth - 24 - LabelX), LabelX, SummaryInk.Center(y + 100, 28), 28, SubInk);
        var ly = y + 136;
        foreach (var line in verdictLines)
        {
            SummaryInk.Draw(c, line, LabelX, SummaryInk.Center(ly, 28), 28, verdict!.Valid ? Pksm.Legal : verdict.LegalIfHatched ? PossiblyLegal : Pksm.Illegal);
            ly += 34;
        }
        foreach (var line in effect)
        {
            SummaryInk.Draw(c, line, LabelX, SummaryInk.Center(ly, 28), 28, ValueInk);
            ly += 34;
        }
        GlowLine(c, PX0, right, y + height - 4);
        return height;
    };

    // ── Origin ───────────────────────────────────────────────────────────────

    // Pokémon History records read for this view, by identity: one disk read per Pokémon shown.
    private readonly Dictionary<string, PokemonHistoryRecord?> _histories = new(StringComparer.Ordinal);

    private PokemonHistoryRecord? HistoryOf(MonSummary s)
    {
        if (s.HistoryKey is not { } key) return null;
        if (!_histories.TryGetValue(key, out var record))
        {
            try { record = PokemonHistorySetting.Store.Find([key]); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { record = null; }
            _histories[key] = record;
        }
        return record;
    }

    /// <summary>The games a Pokémon has travelled between (Pokémon History), newest trip first.</summary>
    private static IEnumerable<Block> HistoryBlocks(PokemonHistoryRecord history)
    {
        yield return Divider("History");
        foreach (var trip in history.Trips.OrderByDescending(t => t.At).Take(8))
            yield return Row(trip.At.LocalDateTime.ToString("yyyy-MM-dd"), $"{trip.FromGame} → {trip.ToGame}",
                trip.Restored ? $"Came back to its {trip.ToGame} data, with its progress kept." : null,
                null);
        if (history.Trips.Count > 8) yield return Text($"And {history.Trips.Count - 8} earlier trip(s).", SubInk);
        var kept = history.Departures.Select(d => d.Game).Distinct(StringComparer.Ordinal).ToList();
        if (kept.Count > 0)
            yield return Text($"Saved copies: {string.Join("\u00A0· ", kept)}. Each comes back when it returns there.", SubInk);
    }

    private static List<Block> OriginPage(MonSummary s, PokemonHistoryRecord? history)
    {
        var blocks = new List<Block> { Divider("Origin") };
        if (s.Met is { } met)
        {
            blocks.Add(Row("Game", met.VersionName.Length > 0 ? met.VersionName : "Unknown"));
            if (met.WasEgg && met.EggLocationName.Length > 0)
                blocks.Add(Row("Egg", met.EggLocationName, met.EggDate.Length > 0 ? $"Received {met.EggDate}" : null));
            blocks.Add(Row(met.WasEgg ? "Hatched" : "Met", met.MetLocationName.Length > 0 ? met.MetLocationName : "—",
                string.Join("  ·  ", new[]
                {
                    met.WasEgg ? null : met.MetLevel > 0 ? $"At Lv. {met.MetLevel}" : null,
                    met.MetDate.Length > 0 ? met.MetDate : null,
                }.Where(x => x is not null))));
            if (met.LanguageName.Length > 0) blocks.Add(Row("Language", met.LanguageName));
        }
        else blocks.Add(Text("This game's met data isn't readable yet."));
        blocks.Add(Row("Format", $"{s.Format.ToUpperInvariant()}  ·  Gen {s.Generation}"));
        if (s.Fields?.Pid is { } pid)
            blocks.Add(Row("PID", pid.ToString("X8"), s.Fields.EncryptionConstant is { } ec ? $"Encryption constant {ec:X8}" : null));
        if (s.Met is { Fateful: true }) blocks.Add(Text("Fateful encounter: an event or gift Pokémon.", Pksm.Legal));

        var memories = new List<string>();
        if (s.Fields?.OtMemory is { } otMemory) memories.Add(otMemory);
        if (s.Fields?.HandlerMemory is { } htMemory) memories.Add(htMemory);
        if (memories.Count > 0)
        {
            blocks.Add(Divider("Memories"));
            blocks.AddRange(memories.Select(m => Text(m)));
        }

        blocks.Add(Divider("Ribbons & marks"));
        if (s.RibbonCount + s.MarkCount > 0)
        {
            // Non-breaking before each dot, so a wrapped line never starts with one.
            blocks.Add(Text(string.Join("\u00A0· ", s.RibbonNames), Gold));
            blocks.Add(Text($"{s.RibbonCount} ribbon(s)  ·  {s.MarkCount} mark(s)", SubInk));
        }
        else blocks.Add(Text("No ribbons or marks yet.", SubInk));
        if (history is { Trips.Count: > 0 }) blocks.AddRange(HistoryBlocks(history));
        return blocks;
    }

    // ── Legality ─────────────────────────────────────────────────────────────

    private List<Block> LegalityPage(MonSummary s)
    {
        if (_legalityPending) return [Hero("Checking…", ValueInk, "The legality analysis is running.")];
        if (s.Legal is null)
            return
            [
                Hero("Not checked", ValueInk, "No legality data for this game."),
                Divider("Why"),
                Text("Legality data only exists for the official games, so ROM hack Pokémon are neither legal nor illegal."),
            ];

        var checks = s.LegalityChecks;
        var problems = checks?.Count(c => c.Judgement == LegalityJudgement.Invalid)
            ?? s.LegalityLines.Count(l => !string.IsNullOrWhiteSpace(l));
        var blocks = new List<Block>
        {
            s.Legal == true
                ? Hero("Legal", Pksm.Legal, "Every check passed.")
                : Hero("Illegal", Pksm.Illegal, problems == 1 ? "1 problem found." : $"{problems} problems found."),
            Divider("Checks"),
        };
        if (checks is not null) blocks.AddRange(checks.Select(Check));
        else blocks.AddRange(s.LegalityLines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => Text(l)));
        if (s.Legal == false)
        {
            blocks.Add(Divider("Fix it"));
            blocks.Add(Text(FixHint));
        }
        return blocks;
    }

    /// <summary>One topic of the analysis: its dot, name and outcome, and why when it is not valid.</summary>
    private static Block Check(LegalityCheck check) => (c, y, right) =>
    {
        var reasons = check.Judgement == LegalityJudgement.Valid ? [] : check.Reasons.SelectMany(r => SummaryInk.Wrap(r, 26, right - 82 - 10)).ToList();
        var height = 76 + 36 * reasons.Count;
        if (c is null) return height;
        var (word, tone) = check.Judgement switch
        {
            LegalityJudgement.Invalid => ("Invalid", Pksm.Illegal),
            LegalityJudgement.Fishy => ("Fishy", Orange),
            _ => ("Valid", Pksm.Legal),
        };
        using (var dot = new SKPaint { Color = tone, IsAntialias = true }) c.DrawCircle(53, y + 39, 11, dot);
        SummaryInk.Draw(c, check.Name, 82, SummaryInk.Center(y + 38, 36), 36, check.Judgement == LegalityJudgement.Valid ? ValueInk : Pksm.Ink);
        SummaryInk.Draw(c, word, right - 12, SummaryInk.Center(y + 38, 32), 32, tone, align: SKTextAlign.Right);
        for (var i = 0; i < reasons.Count; i++)
            SummaryInk.Draw(c, reasons[i], 82, SummaryInk.Center(y + 76 + 36 * i, 26), 26, check.Judgement == LegalityJudgement.Invalid ? ReasonInk : SubInk);
        GlowLine(c, PX0, right, y + height - 4);
        return height;
    };

    // ── Plates and shapes ────────────────────────────────────────────────────

    // The category plates share one blue, the app's own, so they never read as a type.
    private static SKColor CategoryLight => ColorTheme.Current.ToolEdge;
    private static SKColor CategoryDark => ColorTheme.Current.CategoryDark;

    private static void CategoryPlate(SKCanvas c, SKRect r, MoveCategory category)
    {
        var label = category switch
        {
            MoveCategory.Physical => "PHYS",
            MoveCategory.Special => "SPEC",
            _ => "STAT",
        };
        TypePlates.Paint(c, r, CategoryLight, CategoryDark, label);
    }

    /// <summary>A tab: rounded on the left, its right edge slanting out toward the bottom.</summary>
    private static SKPath Tab(SKRect r, float radius, float slant)
    {
        var path = new SKPath();
        path.MoveTo(r.Left + radius, r.Top);
        path.LineTo(r.Right - slant, r.Top);
        path.LineTo(r.Right, r.Bottom);
        path.LineTo(r.Left + radius, r.Bottom);
        if (radius > 0)
        {
            path.ArcTo(new SKRect(r.Left, r.Bottom - 2 * radius, r.Left + 2 * radius, r.Bottom), 90, 90, false);
            path.LineTo(r.Left, r.Top + radius);
            path.ArcTo(new SKRect(r.Left, r.Top, r.Left + 2 * radius, r.Top + 2 * radius), 180, 90, false);
        }
        else path.LineTo(r.Left, r.Top);
        path.Close();
        return path;
    }

    /// <summary>A parallelogram leaning right: the tab strip and the active tab.</summary>
    private static SKPath Parallelogram(SKRect r, float slant)
    {
        var path = new SKPath();
        path.MoveTo(r.Left + slant, r.Top);
        path.LineTo(r.Right, r.Top);
        path.LineTo(r.Right - slant, r.Bottom);
        path.LineTo(r.Left, r.Bottom);
        path.Close();
        return path;
    }

    private static void Fill(SKCanvas c, SKPath path, SKColor top, SKColor bottom)
    {
        var bounds = path.Bounds;
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, bounds.Top), new SKPoint(0, bounds.Bottom), [top, bottom], SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        c.DrawPath(path, paint);
    }

    private static void Outline(SKCanvas c, SKPath path, SKColor color, float width)
    {
        // Inside the shape only, as the mockup's inset outline.
        c.Save();
        c.ClipPath(path, antialias: true);
        using var paint = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = width * 2, StrokeJoin = SKStrokeJoin.Miter };
        c.DrawPath(path, paint);
        c.Restore();
    }
}
