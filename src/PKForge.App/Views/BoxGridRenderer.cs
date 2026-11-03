using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// The save box painter (<see cref="Paint"/>, in the designer's direction, see
/// <see cref="StoragePaint"/>), plus the square-cell grid helpers and badges the other grids
/// (Bank, Bank search, Pokédex, second screen) share.
/// </summary>
public static class BoxGridRenderer
{
    public const int Columns = 6;
    public const int Rows = 5;

    // Nearest keeps the pixel-art crisp when scaling up ("a little pixel, not a lot").
    // Shared with the Bank grid; UI-thread only, never mutated.
    internal static readonly SKSamplingOptions SpriteSampling = new(SKFilterMode.Nearest, SKMipmapMode.None);
    internal static readonly SKPaint SparklePaint = new() { Color = UiTokens.SkShinyGold, IsAntialias = true };
    internal static readonly SKPaint GhostPaint = new() { Color = SKColors.White.WithAlpha(0x60) };

    /// <summary>The box's wallpaper flat, cycling through the storage palette.</summary>
    public static SKColor WallpaperAt(int boxIndex) =>
        Pksm.BoxWallpapers[((boxIndex % Pksm.BoxWallpapers.Length) + Pksm.BoxWallpapers.Length) % Pksm.BoxWallpapers.Length];

    /// <summary>The square-cell layout every grid consumer shares: paint and hit-test agree.</summary>
    public static (float Cell, float OffsetX, float OffsetY) GridMetrics(SKSize canvasSize)
    {
        var cell = Math.Min(canvasSize.Width / (float)Columns, canvasSize.Height / (float)Rows);
        return (cell, (canvasSize.Width - cell * Columns) / 2f, (canvasSize.Height - cell * Rows) / 2f);
    }

    public static (float Cell, float OffsetX, float OffsetY) GridMetrics(SKImageInfo info)
        => GridMetrics(new SKSize(info.Width, info.Height));

    /// <summary>The slot rect for a flat index under the shared layout.</summary>
    public static SKRect SlotRect(SKSize canvasSize, int index)
    {
        var (cell, offsetX, offsetY) = GridMetrics(canvasSize);
        var gap = cell * 0.06f;
        var col = index % Columns;
        var row = index / Columns;
        return new SKRect(
            offsetX + col * cell + gap, offsetY + row * cell + gap,
            offsetX + (col + 1) * cell - gap, offsetY + (row + 1) * cell - gap);
    }

    public static SKRect SlotRect(SKImageInfo info, int index) => SlotRect(new SKSize(info.Width, info.Height), index);

    /// <summary>The grid's outer bounds (what the crosshair brackets frame).</summary>
    public static SKRect GridBounds(SKSize canvasSize)
    {
        var (cell, offsetX, offsetY) = GridMetrics(canvasSize);
        return new SKRect(offsetX, offsetY, offsetX + cell * Columns, offsetY + cell * Rows);
    }

    public static SKRect GridBounds(SKImageInfo info) => GridBounds(new SKSize(info.Width, info.Height));

    /// <summary>
    /// The save box's layout: the canvas is the well, and the cells fill it inside a side
    /// margin, wider than tall like the designer's box. Unit 1 is the 1920×1080 mockup.
    /// </summary>
    public readonly record struct StorageLayout(SKRect Area, float CellWidth, float CellHeight, float Unit)
    {
        public SKRect Cell(int index)
        {
            var col = index % Columns;
            var row = index / Columns;
            return new SKRect(Area.Left + col * CellWidth, Area.Top + row * CellHeight,
                Area.Left + (col + 1) * CellWidth, Area.Top + (row + 1) * CellHeight);
        }
    }

    // The mockup's well is 1072×702 design pixels; its insets scale with the canvas.
    private const float DesignWellWidth = 1072f;
    private const float DesignWellHeight = 702f;

    public static StorageLayout StorageMetrics(SKSize canvasSize)
    {
        var insetX = canvasSize.Width * StoragePaint.WellInsetX / DesignWellWidth;
        var insetY = canvasSize.Height * StoragePaint.WellInsetY / DesignWellHeight;
        var area = new SKRect(insetX, insetY, canvasSize.Width - insetX, canvasSize.Height - insetY);
        var cellWidth = area.Width / Columns;
        var cellHeight = area.Height / Rows;
        var unit = Math.Min(cellWidth / StoragePaint.DesignCellWidth, cellHeight / StoragePaint.DesignCellHeight);
        return new StorageLayout(area, cellWidth, cellHeight, unit);
    }

    /// <summary>Maps a touch on the save box to a slot; -1 outside the cells.</summary>
    public static int StorageSlotFromTouch(SKSize canvasSize, SKPoint location)
    {
        var layout = StorageMetrics(canvasSize);
        if (!layout.Area.Contains(location)) return -1;
        var col = Math.Min(Columns - 1, (int)((location.X - layout.Area.Left) / layout.CellWidth));
        var row = Math.Min(Rows - 1, (int)((location.Y - layout.Area.Top) / layout.CellHeight));
        return row * Columns + col;
    }

    /// <summary>
    /// One box as the storage grid draws it, whoever owns it (a save's box, a Bank box): the
    /// slots, the cursor, the Pokémon in hand and the marks painted on the slots.
    /// </summary>
    /// <param name="Slots">The 30 slots; a null species is an empty slot.</param>
    /// <param name="CarryOrigin">The slot the Pokémon in hand was lifted from, when it is in this box.</param>
    /// <param name="Carried">The Pokémon in hand, whichever box it came from.</param>
    /// <param name="Marking">Multi-select is on: the cursor turns green.</param>
    /// <param name="RangeMark">A slot inside the rectangle being swept: whether it marks (true) or unmarks.</param>
    /// <param name="Box">The box's index, so the hand ends a landing when the box changes.</param>
    internal sealed record StorageScene(
        IReadOnlyList<Domain.SlotSummary> Slots,
        int Selected,
        int? CarryOrigin,
        Domain.SlotSummary? Carried,
        bool Marking,
        Func<int, bool> IsMarked,
        Func<int, bool?> RangeMark,
        int Box,
        IReadOnlySet<int>? Locked = null,
        IReadOnlyDictionary<int, bool>? Verdicts = null,
        bool BdspStyle = false);

    /// <summary>
    /// Paints the save box: the well, each Pokémon standing on its row's ground line (the
    /// Showdown icon; PKHeX's shiny art sized to that icon for shinies), and the cursor as a
    /// light pool under the selected Pokémon, lifted, with the pixel pointer above it.
    /// </summary>
    public static void Paint(
        SKCanvas canvas,
        SKImageInfo info,
        BoxBrowserViewModel viewModel,
        ISpriteService sprites,
        ThemeService theme,
        Action invalidate,
        IReadOnlySet<int>? lockedSlots = null,
        CarryHand? hand = null,
        (SKImage Art, SKColor Average)? wallpaper = null)
    {
        var scene = new StorageScene(
            viewModel.VisibleSlots,
            viewModel.SelectedSlot,
            viewModel.CarrySource is { } source && source.Box == viewModel.BoxIndex ? source.Slot : null,
            viewModel.CarrySource is not null ? viewModel.CarriedSummary : null,
            viewModel.SelectMode,
            // Marks show only while multi-select is on, as before.
            index => viewModel.SelectMode && viewModel.IsMarked(viewModel.BoxIndex, index),
            index => viewModel.PendingRectangle is { } range && viewModel.InPendingRectangle(index) ? range.Mark : null,
            viewModel.BoxIndex,
            lockedSlots,
            viewModel.CurrentBoxLegality,
            // BDSP and Luminescent Platinum boxes wear the BDSP-style icons once downloaded.
            Domain.BdspIcons.AppliesTo(viewModel.Save?.Format));
        PaintScene(canvas, info, scene, sprites, invalidate, hand, (c, well, unit) =>
        {
            // The box's own wallpaper from the game, toned down in the player's chosen style.
            if (wallpaper is { } art) StoragePaint.Wallpaper(c, well, art.Art, art.Average, BoxBackground.Style, unit);
        });
    }

    /// <summary>Paints any box in the storage look; <paramref name="backdrop"/> draws its wallpaper inside the well.</summary>
    internal static void PaintScene(SKCanvas canvas, SKImageInfo info, StorageScene scene, ISpriteService sprites,
        Action invalidate, CarryHand? hand, Action<SKCanvas, SKRect, float>? backdrop = null)
    {
        var layout = StorageMetrics(new SKSize(info.Width, info.Height));
        var unit = layout.Unit;
        canvas.Clear(SKColors.Transparent);
        var well = new SKRect(0, 0, info.Width, info.Height);
        StoragePaint.WellPanel(canvas, well, unit);
        backdrop?.Invoke(canvas, well, unit);
        var bdspStyle = scene.BdspStyle;
        var marking = scene.Marking;
        var lockedSlots = scene.Locked;

        using var font = new SKFont { Size = layout.CellHeight * 0.15f, Edging = SKFontEdging.Antialias };
        using var star = new SKFont(BoxBrowserPage.PixelTypeface(), 28f * unit);
        var ink = new Ink(font, StoragePaint.Well);

        var slots = scene.Slots;
        var verdicts = scene.Verdicts;

        for (var index = 0; index < Columns * Rows; index++)
        {
            var cell = layout.Cell(index);
            // A capacity-limited storage save can end partway through a grid page.
            if (index >= slots.Count)
            {
                using var unavailable = new SKPaint { Color = SKColors.Gray.WithAlpha(45), Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
                canvas.DrawLine(cell.Left, cell.Top, cell.Right, cell.Bottom, unavailable);
                canvas.DrawLine(cell.Right, cell.Top, cell.Left, cell.Bottom, unavailable);
                continue;
            }
            // Badges keep the square tile they were designed for, centered in the wide cell.
            var tile = SKRect.Create(cell.MidX - layout.CellHeight / 2, cell.Top, layout.CellHeight, layout.CellHeight);

            var occupied = index < slots.Count && slots[index].Species is not null;
            var isCarryOrigin = scene.CarryOrigin == index;
            var selected = index == scene.Selected;

            if (selected) StoragePaint.CursorPool(canvas, cell, unit, marking ? Pksm.CursorGreen : null);
            var lift = selected ? StoragePaint.CursorLift * unit : 0f;

            var settling = hand is not null && hand.IsLandingOn(index, scene.Box);
            if (occupied && !settling)
            {
                if (isCarryOrigin)
                {
                    // The lifted Pokémon leaves a faded ghost behind.
                    canvas.SaveLayer(GhostPaint);
                    DrawStanding(canvas, cell, unit, 0f, slots[index], sprites, invalidate, ink, bdspStyle);
                    canvas.Restore();
                }
                else
                {
                    DrawStanding(canvas, cell, unit, lift, slots[index], sprites, invalidate, ink, bdspStyle);
                }
            }

            if (scene.RangeMark(index) is { } mark)
                PksmPaint.RangeWash(canvas, SKRect.Inflate(cell, -4f * unit, -4f * unit), mark);

            if (selected && hand is null && scene.Carried is { } carried)
                DrawStanding(canvas, cell, unit, layout.CellHeight * 0.18f, carried, sprites, invalidate, ink, bdspStyle);

            if (occupied && !isCarryOrigin && slots[index].IsShiny)
                StoragePaint.ShinyStar(canvas, cell, star, unit);

            if (occupied && lockedSlots is not null && lockedSlots.Contains(index))
                DrawLockBadge(canvas, tile, layout.CellHeight);

            if (occupied && !isCarryOrigin && slots[index].HasItem)
                DrawHeldItemBadge(canvas, tile, besideLock: lockedSlots is not null && lockedSlots.Contains(index));

            if (occupied && verdicts is not null && verdicts.TryGetValue(index, out var legal))
                DrawLegalityDot(canvas, tile, legal);

            if (occupied && scene.IsMarked(index))
                PksmPaint.MarkBadge(canvas, tile);
        }

        if ((uint)scene.Selected >= (uint)(Columns * Rows)) return;
        var cursor = layout.Cell(scene.Selected);

        // The Pokémon in hand glides from slot to slot under the pointer.
        if (hand is not null)
        {
            var carrying = scene.Carried is not null;
            var origin = scene.CarryOrigin is { } from && (uint)from < (uint)(Columns * Rows) ? layout.Cell(from) : cursor;
            hand.Sync(carrying, origin, cursor, scene.Selected, scene.Box);
            var held = scene.Carried;
            if (hand.Draw(canvas, layout.CellHeight,
                    (c, r) => { if (held is not null) DrawStanding(c, r, unit, 0f, held, sprites, invalidate, ink, bdspStyle); },
                    (c, r) => StoragePaint.Pointer(c, r, unit, marking)))
                invalidate();
            if (hand.Holding) return;
        }
        StoragePaint.Pointer(canvas, cursor, unit, marking);
    }

    /// <summary>What the no-sprite fallback writes with: the nickname or "#species".</summary>
    private readonly record struct Ink(SKFont Font, SKColor Shadow);

    // The BDSP icons are 128 px renders drawn smaller: smooth sampling, unlike the pixel set.
    private static readonly SKSamplingOptions IconSampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    // The visible part of each Showdown icon, by sheet cell: a shiny's PKHeX sprite is fitted
    // to it. Found once per icon; UI thread only.
    private static readonly Dictionary<SKRectI, SKRectI> IconFootprints = [];

    /// <summary>
    /// Draws one Pokémon standing on the cell's ground line, <paramref name="lift"/> pixels up.
    /// Order: the BDSP add-on icon (BDSP saves), the Showdown icon (a shiny wears PKHeX's shiny
    /// art at the icon's size, or keeps the icon when no shiny art exists), then PKHeX's art for
    /// eggs and forms Showdown does not draw. While an answer is loading nothing is drawn, so no
    /// stand-in flashes first.
    /// </summary>
    private static void DrawStanding(SKCanvas canvas, SKRect cell, float unit, float lift, Domain.SlotSummary slot,
        ISpriteService sprites, Action invalidate, Ink ink, bool bdspStyle)
    {
        var look = slot.Look;
        if (bdspStyle)
        {
            if (!sprites.TryGetBdspIcon(look, invalidate, out var icon)) return;
            if (icon is not null)
            {
                var side = Math.Min(cell.Width, cell.Height) * 0.98f;
                var fit = side / Math.Max(icon.Width, icon.Height);
                var iw = icon.Width * fit;
                var ih = icon.Height * fit;
                var bottom = cell.Bottom - lift;
                using var iconImage = SKImage.FromBitmap(icon);
                canvas.DrawImage(iconImage, new SKRect(cell.MidX - iw / 2, bottom - ih, cell.MidX + iw / 2, bottom), IconSampling);
                return;
            }
        }

        if (!slot.IsEgg)
        {
            if (!sprites.TryGetShowdownIcon(look, invalidate, out var sheet, out var source)) return;
            if (sheet is not null)
            {
                if (slot.IsShiny)
                {
                    if (!sprites.TryGetShinySprite(look, invalidate, out var shiny)) return;
                    if (shiny is not null)
                    {
                        if (!IconFootprints.TryGetValue(source, out var visible))
                            IconFootprints[source] = visible = StoragePaint.OpaqueBounds(sheet, source);
                        var scale = StoragePaint.IconScale * unit;
                        var footprint = new SKSize(visible.Width * scale, visible.Height * scale);
                        using var shinyImage = SKImage.FromBitmap(shiny);
                        canvas.DrawImage(shinyImage,
                            StoragePaint.FootprintRect(cell, new SKSizeI(shiny.Width, shiny.Height), footprint, lift, unit), SpriteSampling);
                        return;
                    }
                }
                using var sheetImage = SKImage.FromBitmap(sheet);
                canvas.DrawImage(sheetImage, SKRect.Create(source.Left, source.Top, source.Width, source.Height),
                    StoragePaint.IconRect(cell, unit, lift), SpriteSampling, null);
                return;
            }
        }

        var bitmap = sprites.GetSprite(look);
        if (bitmap is null)
        {
            // invalidate is expected to be a coalescing, thread-safe repaint request.
            sprites.Warm(look, invalidate);
            PksmPaint.CenterText(canvas, slot.Nickname ?? $"#{slot.Species}", cell.MidX, cell.MidY - lift,
                ink.Font, ColorTheme.Current.Bright, ink.Shadow, SKTextAlign.Center);
            return;
        }
        // PKHeX's art fits a square tile and stands on the ground line with the icons.
        var box = Math.Min(cell.Width, cell.Height) * 0.94f;
        var fitScale = Math.Min(box / bitmap.Width, box / bitmap.Height);
        var w = bitmap.Width * fitScale;
        var h = bitmap.Height * fitScale;
        var ground = cell.Bottom - 14f * unit - lift;
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, new SKRect(cell.MidX - w / 2, ground - h, cell.MidX + w / 2, ground), SpriteSampling);
    }

    private static SKBitmap? _lockIcon;

    /// <summary>The release-lock badge: gold plate with the padlock glyph, bottom-right.</summary>
    private static void DrawLockBadge(SKCanvas canvas, SKRect rect, float cell)
    {
        _lockIcon ??= SKBitmap.Decode(PksmIcons.GetPng("padlock", PksmIcons.White));
        if (_lockIcon is null) return;
        var pad = cell * 0.05f;
        var size = cell * 0.26f;
        var dest = new SKRect(rect.Right - pad - size, rect.Bottom - pad - size, rect.Right - pad, rect.Bottom - pad);
        using var gold = new SKPaint { Color = UiTokens.SkShinyGold, IsAntialias = true };
        canvas.DrawRoundRect(SKRect.Inflate(dest, size * 0.16f, size * 0.16f), size * 0.22f, size * 0.22f, gold);
        using var image = SKImage.FromBitmap(_lockIcon);
        canvas.DrawImage(image, dest, SpriteSampling);
    }

    private static SKBitmap? _itemIcon;

    /// <summary>
    /// The held-item badge, as the Gen 4-7 PC boxes show it: a small bag glyph tucked into the
    /// slot's lower-right corner (top-right is the shiny star, top-left the mark, bottom-left the
    /// legality pip). Crisp white px_item on a logo-void plate with a pale rim, so it reads on
    /// every wallpaper and over any sprite. When the release lock owns the corner, the badge
    /// sits just left of it. Shared with the Bank, Bank search and second-screen grids.
    /// </summary>
    public static void DrawHeldItemBadge(SKCanvas canvas, SKRect rect, bool besideLock = false)
    {
        _itemIcon ??= SKBitmap.Decode(PksmIcons.GetPng("item", PksmIcons.White));
        var cell = Math.Min(rect.Width, rect.Height);
        var pad = cell * 0.05f;
        // Whole device pixels so the 16-px-grid glyph stays pixel-crisp under nearest sampling.
        var size = MathF.Max(8f, MathF.Round(cell * 0.20f));
        var right = rect.Right - pad - (besideLock ? cell * 0.26f + cell * 0.10f : 0f);
        var dest = new SKRect(MathF.Round(right - size), MathF.Round(rect.Bottom - pad - size),
            MathF.Round(right), MathF.Round(rect.Bottom - pad));
        var plate = SKRect.Inflate(dest, size * 0.14f, size * 0.14f);
        var corner = size * 0.2f;
        using var rim = new SKPaint { Color = Pksm.IndigoInk.WithAlpha(0xE6), IsAntialias = true };
        using var fill = new SKPaint { Color = Pksm.LogoVoid.WithAlpha(0xE6), IsAntialias = true };
        var outer = SKRect.Inflate(plate, MathF.Max(1f, size * 0.06f), MathF.Max(1f, size * 0.06f));
        canvas.DrawRoundRect(outer, corner * 1.2f, corner * 1.2f, rim);
        canvas.DrawRoundRect(plate, corner, corner, fill);
        if (_itemIcon is null) return;
        using var image = SKImage.FromBitmap(_itemIcon);
        canvas.DrawImage(image, dest, SpriteSampling);
    }

    /// <summary>Four-point gold sparkle star for shinies - shared with the Bank grid.</summary>
    public static void DrawSparkle(SKCanvas canvas, float cx, float cy, float radius, SKPaint paint)
    {
        using var path = new SKPath();
        var waist = radius * 0.32f;
        path.MoveTo(cx, cy - radius);
        path.LineTo(cx + waist, cy - waist);
        path.LineTo(cx + radius, cy);
        path.LineTo(cx + waist, cy + waist);
        path.LineTo(cx, cy + radius);
        path.LineTo(cx - waist, cy + waist);
        path.LineTo(cx - radius, cy);
        path.LineTo(cx - waist, cy - waist);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    /// <summary>The legality sweep's verdict pip: green (legal) or red (illegal),
    /// bottom-left with a dark rim so it reads on any wallpaper.</summary>
    public static void DrawLegalityDot(SKCanvas canvas, SKRect rect, bool legal)
    {
        var size = Math.Min(rect.Width, rect.Height);
        var radius = size * 0.10f;
        var cx = rect.Left + size * 0.16f;
        var cy = rect.Bottom - size * 0.16f;
        using var rim = new SKPaint { Color = Pksm.LogoVoid, IsAntialias = true };
        using var fill = new SKPaint { Color = legal ? Pksm.Legal : Pksm.Illegal, IsAntialias = true };
        canvas.DrawCircle(cx, cy, radius * 1.35f, rim);
        canvas.DrawCircle(cx, cy, radius, fill);
    }

    /// <summary>Maps a touch to a slot using the same square-cell layout; -1 outside the grid.</summary>
    public static int SlotFromTouch(SKSize canvasSize, SKPoint location)
    {
        var (cell, offsetX, offsetY) = GridMetrics(canvasSize);
        var col = (int)((location.X - offsetX) / cell);
        var row = (int)((location.Y - offsetY) / cell);
        if (location.X < offsetX || location.Y < offsetY || col is < 0 or >= Columns || row is < 0 or >= Rows)
            return -1;
        return row * Columns + col;
    }
}
