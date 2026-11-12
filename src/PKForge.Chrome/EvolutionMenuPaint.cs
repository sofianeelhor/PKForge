using PKForge.Domain;
using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>
/// Everything the evolution menu shows at one moment: the plan, the branch under the cursor, the
/// Pokémon's front sprite now (<paramref name="From"/>), each branch's front sprite and box icon
/// (null while they load), and whether HaX mode is on.
/// </summary>
public sealed record EvolutionMenuScene(
    EvolutionPlan Plan,
    int Cursor,
    SKImage? From,
    IReadOnlyList<SKImage?> Sprites,
    IReadOnlyList<SKImage?> Icons,
    bool Hax);

/// <summary>Where each touch target landed on the last paint, in canvas pixels.</summary>
public sealed record EvolutionMenuHits(SKRect Panel, IReadOnlyList<SKRect> Branches, SKRect Evolve, SKRect Cancel);

/// <summary>How a branch stands, as the menu colours it.</summary>
public enum EvolutionReadiness
{
    /// <summary>The game's requirement is met.</summary>
    Ready,
    /// <summary>A trade: PKForge performs it, so it is always ready.</summary>
    Trade,
    /// <summary>Not met, but PKForge can meet it legally first ("Meet &amp; evolve").</summary>
    MeetFirst,
    /// <summary>Not met; HaX evolves it anyway.</summary>
    Hax,
    /// <summary>Not possible right now.</summary>
    Blocked,
}

/// <summary>
/// The evolution menu, painted in the DA: the title on a banner tab; when the Pokémon can evolve
/// more than one way, the branches on the summary's slanted tab strip with their box icons; the
/// Pokémon now and after on a dark stage, its pixel chevrons in the requirement's colour; the
/// requirement on its own plate with a word on how it stands; and on the right, what changes
/// (stats, ability, the moves it can learn, its name, an item used up, the trade record). The two
/// keys at the foot. Design pixels of a 1872 wide screen.
/// </summary>
public static class EvolutionMenuPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float BannerHeight = 84f;
    private const float TabsHeight = 96f;
    private const float FootHeight = 76f;
    private const float Pad = 28f;

    private static readonly SKColor Amber = new(0xF2, 0xA6, 0x52);

    /// <summary>How <paramref name="option"/> stands, given HaX mode.</summary>
    public static EvolutionReadiness Readiness(EvolutionOption option) => option switch
    {
        { Available: false, MeetCondition: not null } => EvolutionReadiness.MeetFirst,
        { Available: false } => EvolutionReadiness.Blocked,
        { IsTrade: true } => EvolutionReadiness.Trade,
        { ConditionMet: false } => EvolutionReadiness.Hax,
        _ => EvolutionReadiness.Ready,
    };

    /// <summary>True when the confirm key does something on this branch.</summary>
    public static bool CanGo(EvolutionOption option) => Readiness(option) != EvolutionReadiness.Blocked;

    private static SKColor Tone(EvolutionReadiness readiness) => readiness switch
    {
        EvolutionReadiness.Ready => Pksm.Legal,
        EvolutionReadiness.Trade => T.Cyan,
        EvolutionReadiness.MeetFirst or EvolutionReadiness.Hax => Amber,
        _ => Pksm.Illegal,
    };

    public static EvolutionMenuHits Paint(SKCanvas c, SKImageInfo info, EvolutionMenuScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        using var title = new SKFont(face, 44f);
        using var big = new SKFont(face, 40f);
        using var label = new SKFont(face, 34f);
        using var body = new SKFont(face, 30f);
        using var small = new SKFont(face, 26f);

        var plan = scene.Plan;
        var options = plan.Options;
        var cursor = Math.Clamp(scene.Cursor, 0, Math.Max(0, options.Count - 1));
        var option = options[cursor];
        var readiness = Readiness(option);

        var width = MathF.Min(1720f, DesignWidth - 48f);
        var panelHeight = MathF.Min(height - 40f, 1000f);
        var panel = SKRect.Create((DesignWidth - width) / 2f, MathF.Max(20f, (height - panelHeight) / 2f), width, panelHeight);
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(panel, 26f, 26f, fill);
        TrainerCardPaint.Stripes(c, SKRect.Inflate(panel, -10f, -10f));
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f })
            c.DrawRoundRect(SKRect.Inflate(panel, -2f, -2f), 26f, 26f, rim);

        // The title on its banner tab: what is evolving.
        var banner = new SKRect(panel.Left + 12f, panel.Top + 12f, panel.Left + MathF.Min(980f, width - 90f), panel.Top + BannerHeight);
        using (var path = Tab(banner, 16f, 34f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        var name = plan.Nickname.Length > 0 && !string.Equals(plan.Nickname, plan.SpeciesName, StringComparison.OrdinalIgnoreCase)
            ? plan.Nickname : plan.SpeciesName;
        Text(c, scene.Hax ? $"Evolve {name} · HaX" : $"Evolve {name}", banner.Left + 28f, banner.MidY, title, Pksm.Ink, banner.Width - 80f);
        if (options.Count > 1)
            Right(c, $"{cursor + 1} / {options.Count} ways", panel.Right - Pad - 6f, banner.MidY, small, T.SubInk);

        var y = banner.Bottom + 16f;
        var branches = new List<SKRect>();
        if (options.Count > 1)
        {
            branches = Branches(c, new SKRect(panel.Left + Pad, y, panel.Right - Pad, y + TabsHeight), scene, cursor, label);
            y += TabsHeight + 14f;
        }

        var foot = new SKRect(panel.Left + Pad, panel.Bottom - FootHeight, panel.Right - Pad, panel.Bottom - 12f);
        var bodyBottom = foot.Top - 8f;
        var leftWidth = (panel.Width - 2 * Pad) * 0.5f;
        var left = new SKRect(panel.Left + Pad, y, panel.Left + Pad + leftWidth, bodyBottom);
        var right = new SKRect(left.Right + 24f, y, panel.Right - Pad, bodyBottom);

        // The requirement plate under the stage.
        var plate = new SKRect(left.Left, left.Bottom - 170f, left.Right, left.Bottom);
        var stage = new SKRect(left.Left, left.Top, left.Right, plate.Top - 16f);
        Stage(c, stage, scene, option, readiness, big, small);
        Requirement(c, plate, option, readiness, big, body, small);
        Changes(c, right, plan, option, label, body, small);

        // The keys.
        var go = readiness switch { EvolutionReadiness.MeetFirst => "Meet & evolve", EvolutionReadiness.Trade => "Trade & evolve", _ => "Evolve" };
        var evolve = readiness == EvolutionReadiness.Blocked
            ? DimKey(c, "A", go, foot.Left, foot.MidY, small, body)
            : PadMenuPaint.Key(c, "A", go, foot.Left, foot.MidY, small, body);
        var cancel = PadMenuPaint.Key(c, "B", "Cancel", evolve.Right + 40f, foot.MidY, small, body);
        if (options.Count > 1) PadMenuPaint.Key(c, "◀▶", "Way", cancel.Right + 40f, foot.MidY, small, body);

        c.Restore();
        SKRect Up(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new EvolutionMenuHits(Up(panel), [.. branches.Select(Up)], Up(evolve), Up(cancel));
    }

    /// <summary>A key that does nothing right now: an empty cap and quiet ink.</summary>
    private static SKRect DimKey(SKCanvas c, string key, string text, float x, float mid, SKFont keyFont, SKFont font)
    {
        var cap = SKRect.Create(x, mid - 22f, MathF.Max(50f, keyFont.MeasureFallback(key) + 30f), 44f);
        using (var path = Parallelogram(cap, 10f)) Outline(c, path, T.SubInk, 2.5f);
        Centered(c, key, cap.MidX, mid, keyFont, T.SubInk);
        var w = font.MeasureFallback(text);
        Text(c, text, cap.Right + 14f, mid, font, T.SubInk, w + 4f);
        return new SKRect(cap.Left - 8f, cap.Top - 8f, cap.Right + 14f + w + 8f, cap.Bottom + 8f);
    }

    /// <summary>The branches on the slanted tab strip: each target's box icon and name, the cursor's lit.</summary>
    private static List<SKRect> Branches(SKCanvas c, SKRect r, EvolutionMenuScene scene, int cursor, SKFont font)
    {
        var options = scene.Plan.Options;
        var n = options.Count;
        const float slant = 26f;
        var step = MathF.Min(330f, (r.Width - slant - 8f) / n);
        var left = r.Left + 4f;
        var top = r.Top + 10f;
        var bottom = r.Bottom - 4f;
        using (var strip = Parallelogram(new SKRect(left, top, left + step * n + slant, bottom), slant))
        {
            Gradient(c, strip, T.TabTop, T.TabBottom);
            Outline(c, strip, T.Rim, 4f);
        }
        using (var edge = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 4f })
            for (var i = 1; i < n; i++)
            {
                var x = left + i * step;
                c.DrawLine(x + slant, top, x, bottom, edge);
            }
        var ax = left + cursor * step;
        using (var lit = Parallelogram(new SKRect(ax - 6f, top - 6f, ax + step + slant + 2f, bottom + 2f), slant))
        {
            Gradient(c, lit, T.TabActiveTop, T.TabActiveBottom);
            Outline(c, lit, Pksm.Ink, 4f);
        }
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        var hits = new List<SKRect>();
        for (var i = 0; i < n; i++)
        {
            var cellLeft = left + i * step + slant * 0.6f;
            var cellRight = left + (i + 1) * step + slant * 0.4f;
            var mid = (top + bottom) / 2;
            hits.Add(new SKRect(left + i * step, r.Top, left + (i + 1) * step + slant / 2, r.Bottom));
            var active = i == cursor;
            var blocked = Readiness(options[i]) == EvolutionReadiness.Blocked;
            var x = step >= 250f ? cellLeft + 6f : (cellLeft + cellRight) / 2f - 40f;
            if (i < scene.Icons.Count && scene.Icons[i] is { } icon)
            {
                // The box icon at ×2: its 40×30 cell, whole pixels.
                var dest = SKRect.Create(x, mid - 30f, 80f, 60f);
                if (blocked)
                {
                    using var dim = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(T.Silhouette, SKBlendMode.SrcIn) };
                    c.DrawImage(icon, dest, sampling, dim);
                }
                else c.DrawImage(icon, dest, sampling);
                x += 84f;
            }
            // Names only where they fit whole; many branches (Eevee) show their icons alone.
            var room = cellRight - x - 8f;
            if (step >= 250f)
                Text(c, options[i].SpeciesName, x, mid, font, active ? Pksm.Ink : blocked ? T.SubInk : T.TabIcon, room);
        }
        return hits;
    }

    /// <summary>The Pokémon now and after on the dark stage, feet on one floor, the chevrons between.</summary>
    private static void Stage(SKCanvas c, SKRect r, EvolutionMenuScene scene, EvolutionOption option, EvolutionReadiness readiness,
        SKFont big, SKFont small)
    {
        StoragePaint.WellPanel(c, r, 1f);
        var plan = scene.Plan;
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        var into = scene.Cursor < scene.Sprites.Count ? scene.Sprites[scene.Cursor] : null;
        var nameBand = 100f;
        var floor = r.Bottom - nameBand - 10f;
        var half = r.Width / 2f;
        var room = new SKSize(half - 150f, floor - r.Top - 26f);
        // One whole-step scale for both, so the new form shows how much bigger it gets.
        var tallest = MathF.Max(scene.From?.Height ?? 1, into?.Height ?? 1);
        var widest = MathF.Max(scene.From?.Width ?? 1, into?.Width ?? 1);
        var scale = MathF.Max(1f, MathF.Floor(MathF.Min(room.Height / tallest, room.Width / widest)));
        scale = MathF.Min(scale, 4f);

        using (var line = new SKPaint { Color = T.Rim.WithAlpha(90) })
        {
            c.DrawRect(new SKRect(r.Left + 40f, floor, r.Left + half - 60f, floor + 3f), line);
            c.DrawRect(new SKRect(r.Left + half + 60f, floor, r.Right - 40f, floor + 3f), line);
        }
        void Mon(SKImage? image, float cx, bool silhouette)
        {
            if (image is null) return;
            var w = image.Width * scale;
            var h = image.Height * scale;
            var dest = SKRect.Create(MathF.Round(cx - w / 2), MathF.Round(floor - h), w, h);
            if (!silhouette)
            {
                c.DrawImage(image, dest, sampling);
                return;
            }
            using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(T.Silhouette, SKBlendMode.SrcIn) };
            c.DrawImage(image, dest, sampling, paint);
        }
        var leftX = r.Left + half / 2f - 6f;
        var rightX = r.Right - half / 2f + 6f;
        Mon(scene.From, leftX, silhouette: false);
        // A branch that cannot happen shows as an unseen Pokémon would: its silhouette.
        Mon(into, rightX, silhouette: readiness == EvolutionReadiness.Blocked);

        // The evolution beat: three chevrons of square pixels, in the requirement's colour.
        var tone = Tone(readiness);
        using (var px = new SKPaint { IsAntialias = false })
        {
            const float block = 8f;
            var cy = floor - MathF.Min(90f, (floor - r.Top) / 3f);
            for (var k = 0; k < 3; k++)
            {
                px.Color = k == 2 ? tone : tone.WithAlpha((byte)(110 + k * 60));
                var x0 = r.MidX - 52f + k * 34f;
                for (var j = -3; j <= 3; j++)
                    c.DrawRect(SKRect.Create(x0 + (3 - Math.Abs(j)) * block, cy + j * block - block / 2, block, block), px);
            }
        }

        // Names under the floor: the Pokémon and its level, the branch's species.
        var plan0 = plan.Nickname.Length > 0 && !string.Equals(plan.Nickname, plan.SpeciesName, StringComparison.OrdinalIgnoreCase)
            ? plan.Nickname : plan.SpeciesName;
        var nameY = floor + 46f;
        Centered(c, Fitted(plan0, big, half - 60f), leftX, nameY, big, T.Value);
        Centered(c, plan0 == plan.SpeciesName ? $"Lv. {plan.Level}" : $"{plan.SpeciesName} · Lv. {plan.Level}", leftX, nameY + 40f, small, T.SubInk);
        Centered(c, Fitted(option.SpeciesName, big, half - 60f), rightX, nameY, big, readiness == EvolutionReadiness.Blocked ? T.SubInk : T.Value);
        var after = option.NewNickname is null && plan0 != plan.SpeciesName ? $"keeps the name {plan0}" : "";
        if (after.Length > 0) Centered(c, Fitted(after, small, half - 60f), rightX, nameY + 40f, small, T.SubInk);
    }

    /// <summary>The requirement in the games' words, with a word on how it stands and one line on what that means.</summary>
    private static void Requirement(SKCanvas c, SKRect r, EvolutionOption option, EvolutionReadiness readiness, SKFont big, SKFont body, SKFont small)
    {
        using (var band = new SKPaint { Color = EditorPaint.RowBand(true), IsAntialias = true }) c.DrawRoundRect(r, 14f, 14f, band);
        var tone = Tone(readiness);
        using (var rim = new SKPaint { Color = tone.WithAlpha(200), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), 14f, 14f, rim);

        var word = readiness switch
        {
            EvolutionReadiness.Ready => "MET",
            EvolutionReadiness.Trade => "TRADE",
            EvolutionReadiness.MeetFirst => "NOT YET",
            EvolutionReadiness.Hax => "HaX",
            _ => "NO",
        };
        var chipWidth = small.MeasureFallback(word) + 46f;
        var chip = SKRect.Create(r.Left + 24f, r.Top + 24f, chipWidth, 46f);
        using (var path = Parallelogram(chip, 12f))
        {
            using var paint = new SKPaint { Color = tone, IsAntialias = true };
            c.DrawPath(path, paint);
            Outline(c, path, Pksm.Ink, 2.5f);
        }
        Centered(c, word, chip.MidX, chip.MidY, small, Pksm.Ink);
        // The requirement in the games' words: big on one line, or smaller on two when long.
        var reqRoom = r.Right - chip.Right - 40f;
        var noteTop = chip.Bottom + 30f;
        if (big.MeasureFallback(option.Requirement) <= reqRoom)
            Text(c, option.Requirement, chip.Right + 18f, chip.MidY, big, T.Value, reqRoom);
        else
        {
            var parts = Wrap(option.Requirement, body, reqRoom).ToList();
            Text(c, parts[0], chip.Right + 18f, chip.MidY - (parts.Count > 1 ? 17f : 0f), body, T.Value, reqRoom);
            if (parts.Count > 1)
            {
                Text(c, string.Join(' ', parts.Skip(1)), chip.Right + 18f, chip.MidY + 19f, body, T.Value, reqRoom);
                noteTop += 14f;
            }
        }

        var note = readiness switch
        {
            EvolutionReadiness.Ready => "The requirement is met.",
            EvolutionReadiness.Trade => "No second console needed: PKForge performs the link trade.",
            EvolutionReadiness.MeetFirst => $"Meet & evolve does it first: {option.MeetCondition}.",
            EvolutionReadiness.Hax => "HaX: the game's requirement is not met; it evolves anyway.",
            _ => option.BlockedReason ?? "Not possible right now.",
        };
        var lines = Wrap(note.Replace("..", "."), body, r.Width - 48f).Take(2).ToList();
        var ly = noteTop;
        foreach (var line in lines)
        {
            Text(c, line, r.Left + 24f, ly, body, readiness == EvolutionReadiness.Ready ? T.SubInk : tone, r.Width - 48f);
            ly += 36f;
        }
    }

    /// <summary>What changes, on bands: the six stats before and after, then a line per change.</summary>
    private static void Changes(SKCanvas c, SKRect r, EvolutionPlan plan, EvolutionOption option, SKFont label, SKFont body, SKFont small)
    {
        StoragePaint.WellPanel(c, r, 1f);
        var inner = SKRect.Inflate(r, -24f, -20f);
        Text(c, "WHAT CHANGES", inner.Left + 4f, inner.Top + 18f, small, T.LabelInk, inner.Width);
        var y = inner.Top + 44f;

        // Stats: HP / Atk / Def down the left, SpA / SpD / Spe down the right.
        const float cellHeight = 58f;
        var cellWidth = (inner.Width - 12f) / 2f;
        for (var i = 0; i < 6 && i < option.StatsAfter.Count && i < option.StatsBefore.Count; i++)
        {
            var cell = SKRect.Create(inner.Left + i / 3 * (cellWidth + 12f), y + i % 3 * (cellHeight + 6f), cellWidth, cellHeight);
            using (var band = new SKPaint { Color = EditorPaint.RowBand(i % 3 % 2 == 0), IsAntialias = true }) c.DrawRoundRect(cell, 10f, 10f, band);
            var before = option.StatsBefore[i];
            var after = option.StatsAfter[i];
            var d = after - before;
            var tone = d > 0 ? Pksm.Legal : d < 0 ? Pksm.Illegal : T.SubInk;
            Text(c, NatureFacts.StatNames[i], cell.Left + 16f, cell.MidY, small, T.LabelInk, 70f);
            Right(c, $"{before}", cell.Left + 160f, cell.MidY, body, T.SubInk);
            Arrow(c, cell.Left + 186f, cell.MidY, 1, d == 0 ? T.SubInk : T.Rim);
            Right(c, $"{after}", cell.Left + 270f, cell.MidY, body, T.Value);
            Right(c, d > 0 ? $"+{d}" : d < 0 ? $"{d}" : "=", cell.Right - 16f, cell.MidY, small, tone);
        }
        y += 3 * (cellHeight + 6f) + 14f;

        var rows = new List<(string Caption, string Text, SKColor Tone)>();
        if (option.AbilityBefore != "—")
            rows.Add(("ABILITY", option.AbilityBefore == option.AbilityAfter ? option.AbilityAfter : $"{option.AbilityBefore} → {option.AbilityAfter}",
                option.AbilityBefore == option.AbilityAfter ? T.Value : T.Cyan));
        rows.Add(("MOVES", option.Moves.Count == 0 ? "Nothing new to learn"
            : $"Can learn {string.Join(", ", option.Moves.Select(m => m.Name))} · you choose", option.Moves.Count == 0 ? T.SubInk : T.Cyan));
        var keeps = plan.Nickname.Length > 0 && !string.Equals(plan.Nickname, plan.SpeciesName, StringComparison.OrdinalIgnoreCase);
        rows.Add(("NAME", option.NewNickname is { } nick ? $"Becomes {nick}" : keeps ? $"Keeps {plan.Nickname}" : $"Becomes {option.SpeciesName}", T.Value));
        // A stone PKForge uses for you (Meet & evolve) leaves the bag alone: no "used up" line then.
        var fromBag = !(option.Trigger == EvolutionTrigger.UseItem && Readiness(option) == EvolutionReadiness.MeetFirst);
        if (option.ConsumedItem is { } item && fromBag) rows.Add(("ITEM", $"{item} is used up", Amber));
        if (option.HandlerNote is { } handler) rows.Add(("TRADE", handler, T.SubInk));

        const float rowHeight = 64f;
        var shown = 0;
        foreach (var (caption, text, tone) in rows)
        {
            var row = SKRect.Create(inner.Left, y, inner.Width, rowHeight - 8f);
            if (row.Bottom > inner.Bottom) break;
            using (var band = new SKPaint { Color = EditorPaint.RowBand(shown % 2 == 0), IsAntialias = true }) c.DrawRoundRect(row, 10f, 10f, band);
            Text(c, caption, row.Left + 16f, row.MidY, small, T.LabelInk, 150f);
            var room = row.Width - 190f;
            // A long line (a trade record, many moves) wraps to two lines in the same band.
            var lines = Wrap(text, small, room).ToList();
            if (lines.Count > 1 && body.MeasureFallback(text) > room)
            {
                Text(c, lines[0], row.Left + 176f, row.MidY - 14f, small, tone, room);
                Text(c, string.Join(' ', lines.Skip(1)), row.Left + 176f, row.MidY + 14f, small, tone, room);
            }
            else Text(c, text, row.Left + 176f, row.MidY, body, tone, room);
            y += rowHeight;
            shown++;
        }
    }
}
