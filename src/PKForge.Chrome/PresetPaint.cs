using PKForge.Domain;
using SkiaSharp;
using static PKForge.Chrome.PaintKit;
using static PKForge.Chrome.ToolPaint;

namespace PKForge.Chrome;

/// <summary>What a preset card does when chosen: apply its lines, run a tool (Showdown export or
/// import), or make a new personal preset.</summary>
public enum PresetKind { Apply, Other, New }

/// <summary>One preset card: its name, what it sets, how many Pokémon in scope it would change
/// (null when that is not a count, as for the Showdown tools), and whether it is the player's own.</summary>
public sealed record PresetCard(string Title, string Summary, int? Changes, PresetKind Kind, bool Custom = false);

/// <summary>The Pokémon the focused preset is tried on: its name, a line of facts, its sprite, its
/// types, every field the preset touches (before → after), and where it is in the scope.</summary>
public sealed record PresetFeatured(
    string Name,
    string Facts,
    SKImage? Sprite,
    bool Shiny,
    IReadOnlyList<int> Types,
    IReadOnlyList<BatchFieldChange> Lines,
    int Index,
    int Count);

/// <summary>Everything the Presets screen shows at one moment.</summary>
public sealed record PresetScene(
    IReadOnlyList<string> Scopes,
    int Scope,
    IReadOnlyList<PresetCard> Cards,
    int Cursor,
    float Scroll,
    PresetFeatured? Featured,
    int Focus,
    bool CanApply,
    string Status,
    IReadOnlyList<(string Key, string Label)> Prompts,
    Action<SKCanvas, SKRect, int>? DrawType = null);

/// <summary>
/// Presets, in the app's art direction: at the left the box well holds the presets as rimmed
/// cards (what each sets, and how many Pokémon in scope it would change; the player's own carry
/// a gold tag; the last card makes a new one), the cursor as the cyan chip on the card's plate;
/// at the right a fitting room: one Pokémon of the scope, large and free-standing with its type
/// plates, wearing the focused preset, every field it touches shown before → after. L/R change
/// the Pokémon tried on.
/// </summary>
public static class PresetPaint
{
    public static float DesignWidth => UiScale.DesignWidth;
    public const int ScopeBase = 100, CardBase = 1000;

    private const float LeftWidth = 1110f;
    private const float Gap = 18f;
    private const int Columns = 3;
    private const float CardHeight = 150f;
    private const float CardGap = 18f;

    private static SKRect WellRect(SKSizeI size)
    {
        var height = size.Height * DesignWidth / Math.Max(1, size.Width);
        return new SKRect(0, HeaderHeight + 16f, LeftWidth, height - FooterHeight - 8f);
    }

    private static SKRect View(SKRect well) => new(well.Left + 24f, well.Top + 96f, well.Right - 30f, well.Bottom - 18f);

    public static (int Columns, float RowStep, float ViewHeight) Grid(SKSizeI size) =>
        (Columns, CardHeight + CardGap, View(WellRect(size)).Height);

    public static float MaxScroll(SKSizeI size, int cards)
    {
        var rows = (cards + Columns - 1) / Columns;
        return Math.Max(0f, rows * (CardHeight + CardGap) - CardGap - View(WellRect(size)).Height);
    }

    public static ToolHits Paint(SKCanvas c, SKImageInfo info, PresetScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);
        var targets = new List<(int, SKRect)>();

        using var bannerFont = new SKFont(face, 40f);
        using var title = new SKFont(face, 32f);
        using var small = new SKFont(face, 26f);
        using var big = new SKFont(face, 44f);

        StoragePaint.BoxHeader(c, new SKRect(0, 0, DesignWidth, HeaderHeight), "PRESETS", bannerFont, false, false, 1f);

        // ── The cards ──
        var well = WellRect(info.Size);
        StoragePaint.WellPanel(c, well, 1f);
        Text(c, "APPLY TO", well.Left + 30f, well.Top + 48f, title, T.Label, 200f);
        var chipLeft = well.Left + 210f;
        for (var i = 0; i < scene.Scopes.Count; i++)
        {
            var width = 340f;
            var r = new SKRect(chipLeft + i * (width + 16f), well.Top + 20f, chipLeft + i * (width + 16f) + width, well.Top + 76f);
            Choice(c, r, scene.Scopes[i], small, i == scene.Scope, scene.Focus == ScopeBase + i);
            targets.Add((ScopeBase + i, r));
        }

        var view = View(well);
        var max = MaxScroll(info.Size, scene.Cards.Count);
        var scroll = Math.Clamp(scene.Scroll, 0f, max);
        var cw = (view.Width - (Columns - 1) * CardGap) / Columns;
        c.Save();
        c.ClipRect(SKRect.Inflate(view, 8f, 8f));
        for (var i = 0; i < scene.Cards.Count; i++)
        {
            var top = view.Top + (i / Columns) * (CardHeight + CardGap) - scroll;
            if (top > view.Bottom || top + CardHeight < view.Top) continue;
            var left = view.Left + (i % Columns) * (cw + CardGap);
            var r = new SKRect(left, top, left + cw, top + CardHeight);
            Card(c, r, scene.Cards[i], scene.Focus == CardBase + i, i == scene.Cursor, scene.CanApply, title, small, big);
            if (top >= view.Top - 4f && r.Bottom <= view.Bottom + 4f) targets.Add((CardBase + i, r));
        }
        c.Restore();
        Rail(c, new SKRect(well.Right - 16f, view.Top, well.Right - 10f, view.Bottom), scroll, max, view.Height);

        // ── The fitting room ──
        var room = new SKRect(LeftWidth + Gap, HeaderHeight + 16f, DesignWidth, height - FooterHeight - 8f);
        Panel(c, room);
        var card = scene.Cards.Count > 0 ? scene.Cards[Math.Clamp(scene.Cursor, 0, scene.Cards.Count - 1)] : null;
        var tab = SectionTab(c, room.Left, room.Top + 18f, room.Width * 0.8f, card?.Title.ToUpperInvariant() ?? "PRESET", title);
        if (scene.Featured is { } f && card?.Kind == PresetKind.Apply)
        {
            var art = new SKRect(room.Left + 30f, tab.Bottom + 16f, room.Left + 330f, tab.Bottom + 300f);
            if (f.Sprite is { } sprite) c.DrawImage(sprite, Fit(sprite, art), Nearest);
            var textLeft = art.Right + 18f;
            var textWidth = room.Right - 26f - textLeft;
            Text(c, f.Name, textLeft, art.Top + 40f, big, f.Shiny ? Pksm.ShinyGold : T.Value, textWidth);
            Text(c, f.Facts, textLeft, art.Top + 90f, small, T.SubInk, textWidth);
            for (var t = 0; t < f.Types.Count && t < 2 && scene.DrawType is not null; t++)
                scene.DrawType(c, new SKRect(textLeft + t * 150f, art.Top + 122f, textLeft + t * 150f + 140f, art.Top + 158f), f.Types[t]);
            // Which Pokémon of the scope wears it: ◀ 4 / 30 ▶ (L/R).
            var pager = new SKRect(textLeft, art.Top + 190f, textLeft + 240f, art.Top + 240f);
            using (var plate = new SKPaint { Color = T.Band.WithAlpha(150), IsAntialias = true }) c.DrawRoundRect(pager, 10f, 10f, plate);
            Arrow(c, pager.Left + 24f, pager.MidY, -1, T.Value);
            Centered(c, $"{f.Index + 1} / {f.Count}", pager.MidX, pager.MidY, small, T.Value);
            Arrow(c, pager.Right - 24f, pager.MidY, 1, T.Value);

            var y = art.Bottom + 20f;
            for (var i = 0; i < f.Lines.Count; i++)
            {
                if (y + 50f > room.Bottom - 20f) break;
                var line = f.Lines[i];
                var r = new SKRect(room.Left + 24f, y, room.Right - 24f, y + 50f);
                using (var band = new SKPaint { Color = line.Changes ? T.RimFill.WithAlpha(200) : EditorPaint.RowBand(i % 2 == 0), IsAntialias = true })
                    c.DrawRoundRect(r, 10f, 10f, band);
                Text(c, line.Field, r.Left + 18f, r.MidY, small, T.Label, 180f);
                var mid = r.Left + 200f + (r.Width - 200f) / 2f;
                if (line.Before.Length > 0) Right(c, Fitted(line.Before, small, mid - r.Left - 240f), mid - 24f, r.MidY, small, T.Value);
                Arrow(c, mid, r.MidY, 1, line.Changes ? T.Rim : T.SubInk);
                Text(c, line.Known ? line.After : line.After + "?", mid + 24f, r.MidY, small, line.Changes ? T.Rim : T.SubInk, r.Right - mid - 40f);
                y += 58f;
            }
            if (f.Lines.Count > 0 && f.Lines.All(l => !l.Changes))
                Text(c, "Already wears it: nothing would change.", room.Left + 30f, Math.Min(y + 26f, room.Bottom - 30f), small, T.SubInk, room.Width - 60f);
        }
        else if (card is not null)
        {
            var y = tab.Bottom + 60f;
            var words = card.Kind switch
            {
                PresetKind.New => "Make your own preset: a name, then its lines in PKHeX style (Level=50, IV_SPE=0, Friendship=255…). It stays on this device for every save.",
                _ => card.Summary,
            };
            foreach (var line in Wrap(words, title, room.Width - 60f).Take(8))
            {
                Text(c, line, room.Left + 30f, y, title, T.Value, room.Width - 60f);
                y += 44f;
            }
            if (scene.Featured is null && card.Kind == PresetKind.Apply)
                Text(c, "No Pokémon in this scope.", room.Left + 30f, y + 20f, small, T.SubInk, room.Width - 60f);
        }

        var prompts = Footer(c, new SKRect(0, height - FooterHeight, DesignWidth, height), scene.Prompts, scene.Status, small);
        c.Restore();
        return new ToolHits(unit, [.. targets.Select(t => (t.Item1, Px(t.Item2, unit)))], [.. prompts.Select(p => Px(p, unit))], Px(view, unit), max);
    }

    private static void Card(SKCanvas c, SKRect r, PresetCard card, bool focused, bool current, bool canApply, SKFont title, SKFont small, SKFont big)
    {
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(r, 18f, 18f, fill);
        var plate = new SKRect(r.Left + 10f, r.Top + 10f, r.Right - 10f, r.Top + 62f);
        if (focused) Chip(c, plate, focused: true);
        else
        {
            using var band = new SKPaint { Color = EditorPaint.RowBand(true), IsAntialias = true };
            c.DrawRoundRect(plate, 10f, 10f, band);
        }
        var ink = focused ? T.ChipInk : T.Value;
        var tagRoom = card.Custom ? 96f : 0f;
        if (card.Kind == PresetKind.New)
        {
            // A plus drawn in two bars, the app's line weight.
            using var plus = new SKPaint { Color = ink, IsAntialias = false };
            var cx = plate.Left + 36f;
            c.DrawRect(new SKRect(cx - 14f, plate.MidY - 3f, cx + 14f, plate.MidY + 3f), plus);
            c.DrawRect(new SKRect(cx - 3f, plate.MidY - 14f, cx + 3f, plate.MidY + 14f), plus);
            Text(c, card.Title, plate.Left + 64f, plate.MidY, title, ink, plate.Width - 80f);
        }
        else Text(c, card.Title, plate.Left + 26f, plate.MidY, title, ink, plate.Width - 40f - tagRoom);
        if (card.Custom)
        {
            var tag = new SKRect(plate.Right - 92f, plate.MidY - 15f, plate.Right - 14f, plate.MidY + 15f);
            using (var gold = new SKPaint { Color = Pksm.ShinyGold, IsAntialias = true }) c.DrawRoundRect(tag, 8f, 8f, gold);
            Centered(c, "YOURS", tag.MidX, tag.MidY, small, Pksm.Ink);
        }
        Text(c, card.Summary, r.Left + 22f, plate.Bottom + 28f, small, T.SubInk, r.Width - 44f);
        if (card.Changes is { } n)
        {
            var text = !canApply ? "Hardcore keeps them" : n == 0 ? "nothing would change" : n == 1 ? "1 would change" : $"{n} would change";
            Text(c, text, r.Left + 22f, plate.Bottom + 64f, small, n > 0 && canApply ? T.Rim : T.SubInk, r.Width - 44f);
        }
        using var rim = new SKPaint
        {
            Color = focused || current ? T.Rim : T.Rim.WithAlpha(90), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = focused ? 5f : 3f,
        };
        c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), 18f, 18f, rim);
        _ = big;
    }
}
