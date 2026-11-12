using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One way to get the Pokémon: what kind, where, at which levels, how, and how many spots it stands for.</summary>
/// <param name="Badge">Text drawn in the badge instead of the kind's icon (the game list puts the generation there).</param>
/// <param name="Dim">Drawn faint: a game that does not give this Pokémon out.</param>
public sealed record EncounterRow(string Kind, string Location, string Levels, string Method, int Spots, bool Shiny, string Detail,
    string? Badge = null, bool Dim = false, string? Tag = null);

public sealed record EncounterScene(
    string Species,
    SKImage? Sprite,
    string Game,
    IReadOnlyList<EncounterRow> Rows,
    int Cursor,
    int Scroll,
    Func<string, SKImage?> KindIcon,
    // The game list: rows are games, the legend counts them by generation, the subtitle says how many give it.
    bool Games = false,
    string? Subtitle = null);

public sealed record EncounterHits(IReadOnlyList<(int Index, SKRect Rect)> Rows, SKRect List, int VisibleRows, float RowHeight);

/// <summary>
/// "How to get" in the app's art direction: the Pokémon large on a panel at the left with
/// how many ways each kind offers, and at the right every way as one dense row in the box
/// well (a kind badge, the place, the levels and the method), the full description of the
/// row under the cursor along the bottom.
/// </summary>
public static class EncounterPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float PanelWidth = 600f;
    private const float RowHeight = 116f;
    private const float RowInset = 22f;
    private const float FooterHeight = 70f;

    /// <summary>The kind as the player says it.</summary>
    public static string KindTitle(string kind) => kind switch
    {
        "Egg" => "Egg",
        "Wild" => "Wild",
        "Static" => "Gift or fixed",
        "Trade" => "In-game trade",
        "Event" => "Event",
        _ => "Special",
    };

    /// <summary>The bundled icon for a kind.</summary>
    public static string KindIconName(string kind) => kind switch
    {
        "Egg" => "egg",
        "Wild" => "tree",
        "Static" => "map-pin",
        "Trade" => "arrows-left-right",
        "Event" => "gift",
        _ => "star",
    };

    public static int VisibleRows(SKSizeI size)
    {
        if (size.Width <= 0) return 1;
        var unit = size.Width / DesignWidth;
        var well = size.Height / unit - FooterHeight - 16f;
        return Math.Max(1, (int)((well - 2 * RowInset) / RowHeight));
    }

    public static EncounterHits Paint(SKCanvas c, SKImageInfo info, EncounterScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        PaintPokemon(c, new SKRect(0, 0, PanelWidth, height - FooterHeight - 16f), scene, face);

        var well = new SKRect(PanelWidth + 24f, 0, DesignWidth, height - FooterHeight - 16f);
        StoragePaint.WellPanel(c, well, 1f);
        var hits = new List<(int, SKRect)>();
        using var place = new SKFont(face, 40f);
        using var small = new SKFont(face, 28f);
        var visible = Math.Max(1, (int)((well.Height - 2 * RowInset) / RowHeight));
        if (scene.Rows.Count == 0)
            Centered(c, $"No game data says how to get {scene.Species} here.", well.MidX, well.MidY, small, T.SubInk);
        for (var i = scene.Scroll; i < scene.Rows.Count && i < scene.Scroll + visible; i++)
        {
            var top = well.Top + RowInset + (i - scene.Scroll) * RowHeight;
            var row = new SKRect(well.Left + RowInset + 12f, top, well.Right - RowInset - 28f, top + RowHeight - 10f);
            hits.Add((i, row));
            PaintRow(c, row, scene.Rows[i], i == scene.Cursor, i % 2 == 0, scene.KindIcon, place, small, scene.Games);
        }
        if (scene.Rows.Count > visible)
        {
            var rail = new SKRect(well.Right - RowInset - 10f, well.Top + RowInset, well.Right - RowInset - 4f, well.Bottom - RowInset);
            using var track = new SKPaint { Color = T.Band.WithAlpha(120), IsAntialias = true };
            c.DrawRoundRect(rail, 3f, 3f, track);
            var span = rail.Height * visible / scene.Rows.Count;
            var at = rail.Top + (rail.Height - span) * scene.Scroll / Math.Max(1, scene.Rows.Count - visible);
            using var thumb = new SKPaint { Color = T.Rim, IsAntialias = true };
            c.DrawRoundRect(new SKRect(rail.Left, at, rail.Right, at + span), 3f, 3f, thumb);
        }

        // The full description of the way under the cursor.
        var footer = new SKRect(0, height - FooterHeight, DesignWidth, height);
        using (var band = new SKPaint { Color = T.Band.WithAlpha(170), IsAntialias = true }) c.DrawRoundRect(footer, 16f, 16f, band);
        using (var rim = new SKPaint { Color = T.Rim.WithAlpha(120), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f })
            c.DrawRoundRect(SKRect.Inflate(footer, -1f, -1f), 16f, 16f, rim);
        if (scene.Cursor >= 0 && scene.Cursor < scene.Rows.Count)
        {
            var current = scene.Rows[scene.Cursor];
            using var detail = new SKFont(face, 30f);
            var text = string.IsNullOrWhiteSpace(current.Detail) ? $"{KindTitle(current.Kind)} · {current.Location}" : current.Detail;
            Text(c, text, footer.Left + 28f, footer.MidY, detail, T.Value, footer.Width - 56f);
        }

        c.Restore();
        SKRect Px(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new EncounterHits([.. hits.Select(h => (h.Item1, Px(h.Item2)))], Px(well), visible, RowHeight * unit);
    }

    /// <summary>One way: the kind badge, the place large, then levels, method and spot count; a star when shiny is guaranteed.</summary>
    private static void PaintRow(SKCanvas c, SKRect row, EncounterRow way, bool selected, bool dark, Func<string, SKImage?> kindIcon, SKFont place, SKFont small, bool games)
    {
        if (selected) Chip(c, row, focused: true);
        else
        {
            using var band = new SKPaint { Color = EditorPaint.RowBand(dark), IsAntialias = true };
            c.DrawRoundRect(row, 12f, 12f, band);
        }

        var badge = new SKRect(row.Left + 26f, row.MidY - 38f, row.Left + 102f, row.MidY + 38f);
        using (var plate = new SKPath())
        {
            plate.AddRoundRect(badge, 16f, 16f);
            using (var fill = new SKPaint { Color = selected ? T.TabActiveBottom : T.PanelFill, IsAntialias = true }) c.DrawPath(plate, fill);
            Outline(c, plate, selected ? Pksm.Ink : T.Rim.WithAlpha(110), 2f);
        }
        if (way.Badge is { } badgeText)
        {
            using var badgeFont = new SKFont(small.Typeface, 34f);
            Centered(c, badgeText, badge.MidX, badge.MidY, badgeFont, selected ? T.TabIcon : way.Dim ? T.SubInk : T.Rim);
        }
        else if (kindIcon(KindIconName(way.Kind)) is { } icon)
        {
            using var tint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(selected ? T.TabIcon : T.Rim, SKBlendMode.SrcIn) };
            c.DrawImage(icon, SKRect.Inflate(badge, -14f, -14f), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), tint);
        }

        var ink = selected ? T.ChipInk : way.Dim ? T.SubInk : T.Value;
        var soft = selected ? T.ChipInk : way.Dim ? T.SubInk.WithAlpha(150) : T.SubInk;
        var left = badge.Right + 26f;
        var width = row.Right - left - 200f;
        Text(c, way.Location, left, row.MidY - 20f, place, ink, width);
        var parts = new List<string> { way.Levels };
        if (way.Method.Length > 0) parts.Add(way.Method);
        if (way.Spots > 1) parts.Add($"{way.Spots} spots");
        Text(c, string.Join(" · ", parts), left, row.MidY + 24f, small, soft, width);
        Right(c, way.Tag ?? (games ? "" : KindTitle(way.Kind)), row.Right - 34f, row.MidY + 24f, small, soft);
        if (way.Shiny)
        {
            using var star = new SKFont(place.Typeface, 40f);
            Right(c, "★", row.Right - 34f, row.MidY - 20f, star, Pksm.ShinyGold);
        }
    }

    /// <summary>The Pokémon panel: its name on the tab, the game, the sprite large, how many ways of each kind.</summary>
    private static void PaintPokemon(SKCanvas c, SKRect r, EncounterScene scene, SKTypeface face)
    {
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(r, 28f, 28f, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), 28f, 28f, rim);

        var tab = new SKRect(r.Left - 2f, r.Top + 28f, r.Left + r.Width * 0.84f, r.Top + 106f);
        using (var path = Tab(tab, 0f, 36f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        using var big = new SKFont(face, 50f);
        using var small = new SKFont(face, 28f);
        using var mid = new SKFont(face, 32f);
        Text(c, scene.Species, tab.Left + 30f, tab.MidY, big, Pksm.Ink, tab.Width - 80f);
        var ways = scene.Rows.Count == 1 ? "1 way" : $"{scene.Rows.Count} ways";
        Text(c, scene.Subtitle ?? $"In {scene.Game} · {ways}", r.Left + 30f, tab.Bottom + 36f, small, T.SubInk, r.Width - 60f);

        if (scene.Sprite is { } sprite)
        {
            var box = new SKRect(r.Left + 40f, tab.Bottom + 70f, r.Right - 40f, tab.Bottom + 370f);
            c.DrawImage(sprite, Fit(sprite, box), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }

        // How many ways of each kind (or, for the game list, games per generation) under the Pokémon.
        var y = tab.Bottom + 400f;
        if (scene.Games)
        {
            string[] roman = ["I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX"];
            var generations = scene.Rows.Where(row => !row.Dim && row.Badge is not null)
                .GroupBy(row => row.Badge!)
                .OrderBy(group => Array.IndexOf(roman, group.Key))
                .ToList();
            var colWidth = (r.Width - 60f) / 2f;
            for (var i = 0; i < generations.Count; i++)
            {
                var chip = SKRect.Create(r.Left + 24f + i % 2 * (colWidth + 12f), y + i / 2 * 58f, colWidth, 48f);
                if (chip.Bottom > r.Bottom - 20f) break;
                using (var band = new SKPaint { Color = T.Band.WithAlpha(110), IsAntialias = true }) c.DrawRoundRect(chip, 10f, 10f, band);
                Text(c, $"Gen {generations[i].Key}", chip.Left + 18f, chip.MidY, mid, T.Value, chip.Width - 80f);
                Right(c, generations[i].Count().ToString(System.Globalization.CultureInfo.InvariantCulture), chip.Right - 18f, chip.MidY, mid, T.Value);
            }
            return;
        }
        foreach (var group in scene.Rows.GroupBy(row => row.Kind))
        {
            if (y + 44f > r.Bottom - 20f) break;
            var line = new SKRect(r.Left + 24f, y, r.Right - 24f, y + 52f);
            using (var band = new SKPaint { Color = T.Band.WithAlpha(110), IsAntialias = true }) c.DrawRoundRect(line, 10f, 10f, band);
            if (scene.KindIcon(KindIconName(group.Key)) is { } icon)
            {
                using var tint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(T.Rim, SKBlendMode.SrcIn) };
                c.DrawImage(icon, SKRect.Create(line.Left + 16f, line.MidY - 16f, 32f, 32f), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), tint);
            }
            Text(c, KindTitle(group.Key), line.Left + 64f, line.MidY, mid, T.Value, line.Width - 160f);
            Right(c, group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture), line.Right - 20f, line.MidY, mid, T.Value);
            y += 60f;
        }
    }
}
