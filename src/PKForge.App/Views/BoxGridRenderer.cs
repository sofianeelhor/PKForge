using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Chrome;
using SkiaSharp;

namespace PKForge.App.Views;

/// <summary>
/// Shared LCD box-grid painter (main browser + bank vault): the PKSM storage world.
/// Saturated wallpaper flats with the dot lattice, soft white slots, gold selection
/// frames, and dashed gold carry ghosts.
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

    /// <summary>The box frame tint (the LcdPanel border), derived from the wallpaper flat.</summary>
    public static (Color Background, Color Frame) HueFor(int boxIndex)
    {
        var wallpaper = WallpaperAt(boxIndex);
        var shade = Pksm.WallpaperShade(wallpaper);
        return (Color.FromRgb(wallpaper.Red, wallpaper.Green, wallpaper.Blue),
            Color.FromRgb(shade.Red, shade.Green, shade.Blue));
    }

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

    /// <summary>The PKSM box backdrop: saturated wallpaper flat and faint dot lattice.</summary>
    public static void PaintBackdrop(SKCanvas canvas, SKImageInfo info, int boxIndex)
    {
        PksmPaint.Wallpaper(canvas, new SKRect(0, 0, info.Width, info.Height), WallpaperAt(boxIndex));
    }

    public static void Paint(
        SKCanvas canvas,
        SKImageInfo info,
        BoxBrowserViewModel viewModel,
        ISpriteService sprites,
        ThemeService theme,
        Action invalidate,
        IReadOnlySet<int>? lockedSlots = null,
        CarryHand? hand = null)
    {
        var wallpaper = WallpaperAt(viewModel.BoxIndex);
        var cell = GridMetrics(info).Cell;
        PaintBackdrop(canvas, info, viewModel.BoxIndex);

        // White ink with the wallpaper-shade shadow: text that reads on any flat.
        var shadow = Pksm.WallpaperShade(wallpaper);
        using var font = new SKFont { Size = cell * 0.15f, Edging = SKFontEdging.Antialias };

        var slots = viewModel.VisibleSlots;

        var verdicts = viewModel.CurrentBoxLegality;

        for (var index = 0; index < Columns * Rows; index++)
        {
            var rect = SlotRect(info, index);

            var occupied = index < slots.Count && slots[index].Species is not null;
            var isCarryOrigin = viewModel.CarrySource is { } source
                && source.Box == viewModel.BoxIndex && source.Slot == index;

            // Soft white slot on the wallpaper; a faint waiting ball when empty.
            PksmPaint.Slot(canvas, rect, wallpaper, empty: !occupied);

            var settling = hand is not null && hand.IsLandingOn(index);
            if (occupied && !settling)
            {
                if (isCarryOrigin)
                {
                    // The lifted mon leaves a dashed gold ghost behind.
                    canvas.SaveLayer(GhostPaint);
                    DrawSprite(canvas, rect, slots[index], sprites, invalidate, font, shadow);
                    canvas.Restore();
                    PksmPaint.CarryGhost(canvas, rect);
                }
                else
                {
                    DrawSprite(canvas, rect, slots[index], sprites, invalidate, font, shadow);
                }
            }

            if (viewModel.PendingRectangle is { } range && viewModel.InPendingRectangle(index))
                PksmPaint.RangeWash(canvas, rect, range.Mark);

            if (index == viewModel.SelectedSlot)
            {
                // The games' two hands: red moves one Pokémon, green marks many.
                PksmPaint.Selection(canvas, rect, viewModel.SelectMode ? Pksm.CursorGreen : null);
                if (hand is null && viewModel.CarriedSummary is { } carried && viewModel.CarrySource is not null)
                {
                    var lift = cell * 0.18f;
                    DrawSprite(canvas, new SKRect(rect.Left, rect.Top - lift, rect.Right, rect.Bottom - lift),
                        carried, sprites, invalidate, font, shadow);
                }
            }

            if (occupied && !isCarryOrigin && slots[index].IsShiny)
                DrawSparkle(canvas, rect.Right - rect.Width * 0.14f, rect.Top + rect.Height * 0.16f,
                    Math.Min(rect.Width, rect.Height) * 0.09f, SparklePaint);

            if (occupied && lockedSlots is not null && lockedSlots.Contains(index))
                DrawLockBadge(canvas, rect, Math.Min(rect.Width, rect.Height));

            if (occupied && !isCarryOrigin && slots[index].HasItem)
                DrawHeldItemBadge(canvas, rect, besideLock: lockedSlots is not null && lockedSlots.Contains(index));

            if (occupied && verdicts is not null && verdicts.TryGetValue(index, out var legal))
                DrawLegalityDot(canvas, rect, legal);


            if (viewModel.SelectMode && occupied && viewModel.IsMarked(viewModel.BoxIndex, index))
                PksmPaint.MarkBadge(canvas, rect);
        }

        // The Pokémon in hand glides from slot to slot under the move pointer.
        if (hand is not null && (uint)viewModel.SelectedSlot < (uint)(Columns * Rows))
        {
            var cursor = SlotRect(info, viewModel.SelectedSlot);
            var carrying = viewModel.CarriedSummary is not null && viewModel.CarrySource is not null;
            var origin = viewModel.CarrySource is { } from && from.Box == viewModel.BoxIndex && (uint)from.Slot < (uint)(Columns * Rows)
                ? SlotRect(info, from.Slot)
                : cursor;
            hand.Sync(carrying, origin, cursor, viewModel.SelectedSlot, viewModel.BoxIndex);
            var held = viewModel.CarriedSummary;
            if (hand.Draw(canvas, cell, (c, r) =>
                {
                    if (held is not null) DrawSprite(c, r, held, sprites, invalidate, font, shadow);
                }))
                invalidate();
        }
    }

    private static void DrawSprite(SKCanvas canvas, SKRect rect, Domain.SlotSummary slot,
        ISpriteService sprites, Action invalidate, SKFont font, SKColor shadow)
    {
        var bitmap = sprites.GetSprite(slot.Look);
        if (bitmap is not null)
        {
            // The sprite fills ~94% of the tile - it IS the slot.
            var inset = Math.Min(rect.Width, rect.Height) * 0.03f;
            var box = SKRect.Inflate(rect, -inset, -inset);
            var scale = Math.Min(box.Width / bitmap.Width, box.Height / bitmap.Height);
            var w = bitmap.Width * scale;
            var h = bitmap.Height * scale;
            var dest = new SKRect(rect.MidX - w / 2, rect.MidY - h / 2, rect.MidX + w / 2, rect.MidY + h / 2);
            // SKImage.FromBitmap wraps without copying; DrawImage is the Skia3 path with sampling control.
            using var image = SKImage.FromBitmap(bitmap);
            canvas.DrawImage(image, dest, SpriteSampling);
        }
        else
        {
            // invalidate is expected to be a coalescing, thread-safe repaint request.
            sprites.Warm(slot.Look, invalidate);
            PksmPaint.CenterText(canvas, slot.Nickname ?? $"#{slot.Species}", rect.MidX, rect.MidY,
                font, SKColors.White, shadow, SKTextAlign.Center);
        }
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
