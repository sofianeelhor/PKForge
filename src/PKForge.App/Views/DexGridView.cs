using PKForge.App.Services;
using PKForge.Chrome;
using PKForge.Domain;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;

namespace PKForge.App.Views;

/// <summary>
/// A Pokédex you scroll like a real one: every species in national order, one section per
/// generation under a slanted chip, Showdown icons standing in rows on the storage well.
/// Unseen species are silhouettes; the cursor is the box's light pool and pointer. Touch
/// drags and flings the list, a tap picks a species; the pad moves the cursor (the list
/// follows it) and <see cref="JumpSection"/> hops a generation. Sizes are design pixels of
/// the 1920×1080 mockup, scaled with the display.
/// </summary>
public sealed class DexGridView : SKCanvasView
{
    /// <summary>
    /// How one entry shows: seen (else a silhouette), caught (a ball), dimmed or ticked while
    /// picking, drawn shiny, and a gold star for a shiny owned.
    /// </summary>
    public readonly record struct Look(bool Seen, bool Caught, bool Dimmed = false, bool Ticked = false, bool Shiny = false, bool ShinyMark = false);

    private const float CellWidth = 150, CellHeight = 150, HeaderHeight = 96, Inset = 40, IconScale = 3.4f;

    private readonly ISpriteService _sprites;
    private IReadOnlyList<int> _ids = [];
    private Func<int, Look> _lookOf = _ => default;
    private Func<DexRegions.Region, string?> _sectionNote = _ => null;
    private Func<int, (int Species, int Form)> _identify = key => (key, 0);
    private int _cursor;

    // Layout, in canvas pixels: each species' cell and each section's header band.
    private readonly List<SKRect> _cells = [];
    private readonly List<(SKRect Band, DexRegions.Region Region, int FirstIndex)> _sections = [];
    private float _contentHeight, _unit = 1, _laidOutWidth = -1;

    private float _scroll;
    private readonly TouchScroller _scroller;

    /// <summary>A species was tapped (its index in the list).</summary>
    public event Action<int>? Tapped;

    /// <summary>The cursor moved (by pad or by tap).</summary>
    public event Action<int>? CursorChanged;

    public DexGridView(ISpriteService sprites)
    {
        _sprites = sprites;
        EnableTouchEvents = true;
        PaintSurface += PaintGuard.Wrap("DexGridView.OnPaint", OnPaint);
        Touch += OnTouch;
        // Drag, flick with momentum, and a thumb you can grab, in canvas pixels.
        _scroller = new TouchScroller(Dispatcher, () => _scroll, ScrollTo, () => MaxScroll, () => CanvasSize.Height, Track,
            slop: 10 * _unit, grabWidth: 40 * _unit);
    }

    /// <summary>The scrollbar's track along the right edge, in canvas pixels.</summary>
    private SKRect Track() => new(CanvasSize.Width - 16 * _unit, 10 * _unit, CanvasSize.Width - 6 * _unit, CanvasSize.Height - 10 * _unit);

    public int Cursor => _cursor;
    public int Count => _ids.Count;

    /// <summary>
    /// Shows a list of entries: species ids, or any keys <paramref name="identify"/> turns
    /// into a species and form (the forms dex). <paramref name="sectionNote"/> adds a count to
    /// each generation chip.
    /// </summary>
    public void Show(IReadOnlyList<int> ids, Func<int, Look> lookOf, Func<DexRegions.Region, string?>? sectionNote = null,
        Func<int, (int Species, int Form)>? identify = null)
    {
        _ids = ids;
        _lookOf = lookOf;
        _sectionNote = sectionNote ?? (_ => null);
        _identify = identify ?? (key => (key, 0));
        _cursor = Math.Clamp(_cursor, 0, Math.Max(0, ids.Count - 1));
        _laidOutWidth = -1;
        _scroll = 0;
        InvalidateSurface();
    }

    /// <summary>Repaints after the looks changed (a species cycled, a tick toggled).</summary>
    public void Refresh() => InvalidateSurface();

    /// <summary>Moves the cursor by pad direction: left/right one species, up/down one row.</summary>
    public void Move(int dx, int dy)
    {
        if (_ids.Count == 0) return;
        EnsureLayout();
        var target = _cursor;
        if (dx != 0) target = Math.Clamp(_cursor + dx, 0, _ids.Count - 1);
        else if (dy != 0 && _cells.Count == _ids.Count)
        {
            // The nearest cell in the row above or below, across section breaks.
            var here = _cells[_cursor];
            var rowY = dy > 0
                ? _cells.Where(c => c.Top > here.Top + 1).Select(c => c.Top).DefaultIfEmpty(float.NaN).Min()
                : _cells.Where(c => c.Top < here.Top - 1).Select(c => c.Top).DefaultIfEmpty(float.NaN).Max();
            if (!float.IsNaN(rowY))
            {
                var best = double.MaxValue;
                for (var i = 0; i < _cells.Count; i++)
                {
                    if (Math.Abs(_cells[i].Top - rowY) > 1) continue;
                    var distance = Math.Abs(_cells[i].MidX - here.MidX);
                    if (distance < best) { best = distance; target = i; }
                }
            }
        }
        SetCursor(target);
    }

    /// <summary>Jumps to the first species of the next (1) or previous (-1) generation.</summary>
    public void JumpSection(int direction)
    {
        EnsureLayout();
        if (_sections.Count == 0) return;
        var current = _sections.FindLastIndex(s => s.FirstIndex <= _cursor);
        var next = Math.Clamp(current + direction, 0, _sections.Count - 1);
        // Back from inside a section goes to its own start first.
        if (direction < 0 && current >= 0 && _sections[current].FirstIndex < _cursor) next = current;
        SetCursor(_sections[next].FirstIndex, alignSection: true);
    }

    public void SetCursor(int index, bool alignSection = false)
    {
        if (_ids.Count == 0) return;
        _cursor = Math.Clamp(index, 0, _ids.Count - 1);
        EnsureLayout();
        if (alignSection && _sections.FindLast(s => s.FirstIndex <= _cursor) is { Band.Height: > 0 } section) ScrollTo(section.Band.Top);
        else EnsureVisible();
        CursorChanged?.Invoke(_cursor);
        InvalidateSurface();
    }

    // ── Layout ───────────────────────────────────────────────────────────────

    private void EnsureLayout()
    {
        var width = CanvasSize.Width;
        if (width <= 0 || Math.Abs(width - _laidOutWidth) < 0.5f) return;
        _laidOutWidth = width;
        var display = DeviceDisplay.MainDisplayInfo;
        _unit = (float)(Math.Max(display.Width, display.Height) / 1920.0);
        if (_unit <= 0) _unit = 1;
        var cellW = CellWidth * _unit;
        var cellH = CellHeight * _unit;
        var inset = Inset * _unit;
        var columns = Math.Max(1, (int)((width - inset * 2) / cellW));
        var left = (width - columns * cellW) / 2;

        _cells.Clear();
        _sections.Clear();
        var y = inset * 0.5f;
        DexRegions.Region? region = null;
        var column = 0;
        for (var i = 0; i < _ids.Count; i++)
        {
            var here = DexRegions.Of(_identify(_ids[i]).Species);
            if (here is not null && here != region)
            {
                if (column > 0) { y += cellH; column = 0; }
                region = here;
                _sections.Add((new SKRect(0, y, width, y + HeaderHeight * _unit), here, i));
                y += HeaderHeight * _unit;
            }
            _cells.Add(SKRect.Create(left + column * cellW, y, cellW, cellH));
            if (++column == columns) { column = 0; y += cellH; }
        }
        if (column > 0) y += cellH;
        _contentHeight = y + inset;
    }

    private float MaxScroll => Math.Max(0, _contentHeight - CanvasSize.Height);

    private void ScrollTo(float y)
    {
        _scroll = Math.Clamp(y, 0, MaxScroll);
        InvalidateSurface();
    }

    private void EnsureVisible()
    {
        if (_cursor >= _cells.Count) return;
        var cell = _cells[_cursor];
        var margin = 20 * _unit;
        // Keep the pointer above the icon in view too.
        if (cell.Top - margin < _scroll) ScrollTo(cell.Top - margin - 30 * _unit);
        else if (cell.Bottom + margin > _scroll + CanvasSize.Height) ScrollTo(cell.Bottom + margin - CanvasSize.Height);
    }

    // ── Paint ────────────────────────────────────────────────────────────────

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs args)
    {
        var c = args.Surface.Canvas;
        c.Clear(SKColors.Transparent);
        var bounds = new SKRect(0, 0, args.Info.Width, args.Info.Height);
        EnsureLayout();
        StoragePaint.WellPanel(c, bounds, _unit);
        if (_ids.Count == 0) return;

        c.Save();
        c.ClipRect(SKRect.Inflate(bounds, -3 * _unit, -3 * _unit));
        c.Translate(0, -_scroll);
        var top = _scroll - CellHeight * _unit;
        var bottom = _scroll + args.Info.Height + CellHeight * _unit;

        foreach (var (band, region, _) in _sections)
        {
            if (band.Bottom < top || band.Top > bottom) continue;
            var note = _sectionNote(region);
            var title = note is null ? $"{region.Roman} · {region.Name}" : $"{region.Roman} · {region.Name} · {note}";
            EditorPaint.PaintSection(c, band, title, PixelFont.Face, _unit * 34f / 30f);
        }

        void Redraw() => MainThread.BeginInvokeOnMainThread(InvalidateSurface);
        using var number = new SKFont(PixelFont.Face, 24 * _unit);
        using var numberInk = new SKPaint { Color = ColorTheme.Current.DexNumber, IsAntialias = true };
        using var silhouette = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(ColorTheme.Current.Silhouette, SKBlendMode.SrcIn) };
        using var dim = new SKPaint { Color = SKColors.White.WithAlpha(0x60) };
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        using var star = new SKFont(PixelFont.Face, 28 * _unit);
        using var gold = new SKPaint { Color = Pksm.ShinyGold, IsAntialias = true };

        for (var i = 0; i < _cells.Count; i++)
        {
            var cell = _cells[i];
            if (cell.Bottom < top || cell.Top > bottom) continue;
            var (species, form) = _identify(_ids[i]);
            var look = _lookOf(_ids[i]);
            var selected = i == _cursor;
            var ground = cell.Top + 112 * _unit;

            var lift = selected ? StoragePaint.CursorLift * _unit : 0;
            var spriteLook = new SpriteLook(species, form, look.Shiny);
            SKImage? art = null;
            SKRect src = default, dest = default;
            if (!_sprites.TryGetShowdownIcon(spriteLook, Redraw, out var sheet, out var source)) { }
            else if (sheet is not null)
            {
                var w = 40 * IconScale * _unit;
                var h = 30 * IconScale * _unit;
                dest = new SKRect(cell.MidX - w / 2, ground - h - lift, cell.MidX + w / 2, ground - lift);
                // The sheet's image is made once: copying the whole 480x4110 sheet for every
                // visible cell on every frame is what made the grid drag.
                art = Services.SpriteImages.Of(sheet);
                src = SKRect.Create(source.Left, source.Top, source.Width, source.Height);
            }
            else if (_sprites.GetSprite(spriteLook) is { } sprite)
            {
                // A form Showdown does not draw: PKHeX's art in the icon's space.
                var box = 96 * _unit;
                var fit = Math.Min(box / sprite.Width, box / sprite.Height);
                dest = new SKRect(cell.MidX - sprite.Width * fit / 2, ground - sprite.Height * fit - lift, cell.MidX + sprite.Width * fit / 2, ground - lift);
                art = Services.SpriteImages.Of(sprite);
                src = SKRect.Create(0, 0, sprite.Width, sprite.Height);
            }
            else _sprites.Warm(spriteLook, Redraw);
            if (art is not null)
            {
                // Unseen: the Pokédex's silhouette. Dimmed: not picked while choosing.
                if (!look.Seen) c.DrawImage(art, src, dest, sampling, silhouette);
                else if (look.Dimmed)
                {
                    c.SaveLayer(dim);
                    c.DrawImage(art, src, dest, sampling, null);
                    c.Restore();
                }
                else c.DrawImage(art, src, dest, sampling, null);
            }
            c.DrawTextFallback($"{species:000}", cell.MidX, ground + 24 * _unit, SKTextAlign.Center, number, numberInk);

            if (look.Caught) PokeBall(c, cell.Right - 30 * _unit, ground - 6 * _unit, 11 * _unit);
            if (look.ShinyMark) c.DrawTextFallback("★", cell.Right - 30 * _unit, cell.Top + 34 * _unit, SKTextAlign.Center, star, gold);
            if (look.Ticked) Tick(c, cell.Left + 30 * _unit, cell.Top + 26 * _unit, 14 * _unit);
        }

        if (_cursor < _cells.Count)
        {
            var cell = _cells[_cursor];
            StoragePaint.Pointer(c, SKRect.Create(cell.Left, cell.Top + 6 * _unit, cell.Width, cell.Height), _unit * 0.9f);
        }
        c.Restore();

        if (MaxScroll > 0 && _scroller.Visibility is var shown and > 0)
        {
            var held = _scroller.HoldingThumb;
            var thumbRect = _scroller.Thumb();
            if (held) thumbRect = new SKRect(thumbRect.Left - 6 * _unit, thumbRect.Top, thumbRect.Right, thumbRect.Bottom);
            using var track = new SKPaint { Color = StoragePaint.Well.WithAlpha((byte)(0xB0 * shown)), IsAntialias = true };
            using var thumb = new SKPaint { Color = (held ? EditorPaint.Cyan : ColorTheme.Current.Bright).WithAlpha((byte)((held ? 0xFF : 0x80) * shown)), IsAntialias = true };
            c.DrawRoundRect(Track(), 5 * _unit, 5 * _unit, track);
            c.DrawRoundRect(thumbRect, 5 * _unit, 5 * _unit, thumb);
        }
    }

    /// <summary>The caught mark: a small Poké Ball, red top and white base.</summary>
    private static void PokeBall(SKCanvas c, float cx, float cy, float r)
    {
        using var red = new SKPaint { Color = new SKColor(0xE8, 0x48, 0x3C), IsAntialias = true };
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var rim = new SKPaint { Color = new SKColor(0x10, 0x14, 0x24), Style = SKPaintStyle.Stroke, StrokeWidth = r * 0.22f, IsAntialias = true };
        c.DrawCircle(cx, cy, r, white);
        c.Save();
        c.ClipRect(new SKRect(cx - r, cy - r, cx + r, cy));
        c.DrawCircle(cx, cy, r, red);
        c.Restore();
        c.DrawLine(cx - r, cy, cx + r, cy, rim);
        c.DrawCircle(cx, cy, r, rim);
        c.DrawCircle(cx, cy, r * 0.32f, white);
        c.DrawCircle(cx, cy, r * 0.32f, rim);
    }

    /// <summary>The picked mark while choosing species: a cyan disc with a check.</summary>
    private static void Tick(SKCanvas c, float cx, float cy, float r)
    {
        using var disc = new SKPaint { Color = EditorPaint.Cyan, IsAntialias = true };
        using var check = new SKPaint { Color = Pksm.Ink, Style = SKPaintStyle.Stroke, StrokeWidth = r * 0.3f, IsAntialias = true, StrokeCap = SKStrokeCap.Round };
        c.DrawCircle(cx, cy, r, disc);
        c.DrawLine(cx - r * 0.45f, cy, cx - r * 0.1f, cy + r * 0.4f, check);
        c.DrawLine(cx - r * 0.1f, cy + r * 0.4f, cx + r * 0.5f, cy - r * 0.35f, check);
    }

    // ── Touch: drag and fling scroll, the thumb, tap to pick ────────────────

    private void OnTouch(object? sender, SKTouchEventArgs args)
    {
        args.Handled = true;
        EnsureLayout();
        if (_scroller.Handle(args.ActionType, args.Location) is { } tap) TapAt(tap);
        else if (args.ActionType is SKTouchAction.Pressed or SKTouchAction.Released) InvalidateSurface();
    }

    private void TapAt(SKPoint location)
    {
        var point = new SKPoint(location.X, location.Y + _scroll);
        var index = _cells.FindIndex(cell => cell.Contains(point));
        if (index < 0) return;
        _cursor = index;
        CursorChanged?.Invoke(index);
        InvalidateSurface();
        Tapped?.Invoke(index);
    }
}
