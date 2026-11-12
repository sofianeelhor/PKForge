using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One champion standing in a hall: name, species and level, the large sprite.</summary>
public sealed record MuseumMember(string Name, string Detail, SKImage? Sprite, bool Shiny);

/// <summary>One playthrough's hall as the museum shows it.</summary>
/// <param name="When">The timeline's date under the stop: the first clear's day, or the year last played.</param>
/// <param name="Facts">The plaque's lines: first clear, time played, Pokédex, Pokémon.</param>
/// <param name="Badges">Art keys ("kanto/boulder") and whether each was earned.</param>
public sealed record MuseumHallView(
    string Game,
    string When,
    SKImage? Art,
    string Trainer,
    int Gender,
    bool Champion,
    IReadOnlyList<string> Facts,
    IReadOnlyList<(string Key, bool Earned)> Badges,
    IReadOnlyList<(string Facility, string Label, string Value)> Records);

/// <summary>The team on show in the hall under the cursor.</summary>
public sealed record MuseumTeamView(string Label, string When, IReadOnlyList<MuseumMember> Members);

public sealed record MuseumScene(
    IReadOnlyList<(string Label, string Value)> Totals,
    IReadOnlyList<MuseumHallView> Halls,
    int Cursor,
    MuseumTeamView? Team,
    int TeamIndex,
    int TeamCount,
    string NoTeam,
    string Hint,
    string Empty,
    // The game's Hall of Fame ribbon, the stop on the timeline: in colour once crowned.
    SKImage? Medal = null);

/// <summary>Touch targets from the last paint, in canvas pixels.</summary>
public sealed record MuseumHits(IReadOnlyList<(int Index, SKRect Rect)> Halls, SKRect PreviousTeam, SKRect NextTeam);

/// <summary>
/// The Hall of Fame museum: every playthrough on the shelf. The banner across the top, the
/// totals on plates under it, the timeline of the halls (a rail with a stop per game, gold once
/// its champion was crowned), then the hall under the cursor: the game's plaque at the left
/// (art, trainer, first clear, time, Pokédex, badges, the records the game keeps) and its
/// champion team at the right, large, standing free on the ruled floor of the stage.
/// </summary>
public static class MuseumPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float BannerHeight = 92f;
    private const float TotalsHeight = 86f;
    private const float TimelineHeight = 170f;
    private const float FooterHeight = 70f;
    private const float PlaqueWidth = 660f;
    private const float StopPitch = 250f;

    private static SKSamplingOptions Nearest => new(SKFilterMode.Nearest, SKMipmapMode.None);

    public static MuseumHits Paint(SKCanvas c, SKImageInfo info, MuseumScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        using var title = new SKFont(face, 46f);
        using var big = new SKFont(face, 40f);
        using var mid = new SKFont(face, 34f);
        using var small = new SKFont(face, 28f);

        // The banner.
        var banner = new SKRect(0, 0, DesignWidth, BannerHeight);
        using (var path = Tab(new SKRect(banner.Left, banner.Top, banner.Right - 60f, banner.Bottom), 0f, 40f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        Text(c, "HALL OF FAME MUSEUM", banner.Left + 36f, banner.MidY, title, Pksm.Ink, 900f);
        var count = scene.Halls.Count;
        Right(c, count == 1 ? "1 playthrough" : $"{count} playthroughs", banner.Right - 120f, banner.MidY, small, Pksm.Ink);

        // The totals, one plate each.
        var totalsTop = BannerHeight + 14f;
        if (scene.Totals.Count > 0)
        {
            var gap = 16f;
            var plateWidth = (DesignWidth - gap * (scene.Totals.Count - 1)) / scene.Totals.Count;
            for (var i = 0; i < scene.Totals.Count; i++)
            {
                var plate = SKRect.Create(i * (plateWidth + gap), totalsTop, plateWidth, TotalsHeight);
                using (var fill = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true }) c.DrawRoundRect(plate, 14f, 14f, fill);
                using (var rim = new SKPaint { Color = T.Rim.WithAlpha(110), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f })
                    c.DrawRoundRect(SKRect.Inflate(plate, -1f, -1f), 14f, 14f, rim);
                var (label, value) = scene.Totals[i];
                Text(c, label.ToUpperInvariant(), plate.Left + 22f, plate.MidY, small, T.SubInk, plate.Width * 0.5f);
                Right(c, Fitted(value, big, plate.Width * 0.5f - 30f), plate.Right - 22f, plate.MidY, big, T.Value);
            }
        }

        // The timeline: a rail, a stop per hall; the cursor's stop on the chip.
        var lineTop = totalsTop + TotalsHeight + 14f;
        var timeline = new SKRect(0, lineTop, DesignWidth, lineTop + TimelineHeight);
        StoragePaint.WellPanel(c, timeline, 1f);
        var hallHits = new List<(int, SKRect)>();
        if (count == 0)
            Centered(c, scene.Empty, timeline.MidX, timeline.MidY, mid, T.SubInk);
        else
        {
            var visible = Math.Max(1, (int)((timeline.Width - 120f) / StopPitch));
            var first = Math.Clamp(scene.Cursor - visible / 2, 0, Math.Max(0, count - visible));
            var shown = Math.Min(visible, count - first);
            var pitch = Math.Min(StopPitch * 1.4f, (timeline.Width - 120f) / shown);
            var left = timeline.MidX - pitch * shown / 2f;
            var railY = timeline.Top + 62f;
            using (var rail = new SKPaint { Color = T.Rim.WithAlpha(150), StrokeWidth = 4f, IsAntialias = true })
                c.DrawLine(timeline.Left + 30f, railY, timeline.Right - 30f, railY, rail);
            if (first > 0) Arrow(c, timeline.Left + 30f, railY, -1, T.Rim);
            if (first + shown < count) Arrow(c, timeline.Right - 30f, railY, 1, T.Rim);
            for (var k = 0; k < shown; k++)
            {
                var i = first + k;
                var hall = scene.Halls[i];
                var cx = left + pitch * (k + 0.5f);
                var stop = new SKRect(cx - pitch / 2f + 10f, timeline.Top + 10f, cx + pitch / 2f - 10f, timeline.Bottom - 10f);
                hallHits.Add((i, stop));
                var selected = i == scene.Cursor;
                var plate = new SKRect(stop.Left, railY + 22f, stop.Right, stop.Bottom);
                if (selected) Chip(c, plate, focused: true);
                // The stop: the game's Hall of Fame ribbon, in colour once the champion was
                // crowned, a quiet silhouette in the theme's colour while not.
                if (scene.Medal is { } medal)
                {
                    var spot = Fit(medal, new SKRect(cx - 30f, railY - 30f, cx + 30f, railY + 30f));
                    if (hall.Champion) c.DrawImage(medal, spot, Nearest);
                    else
                    {
                        using var ghost = new SKPaint { ColorFilter = SKColorFilter.CreateBlendMode(T.Rim.WithAlpha(90), SKBlendMode.SrcIn) };
                        c.DrawImage(medal, spot, Nearest, ghost);
                    }
                }
                // Above the rail: the game's art on its left, the date on its right.
                var dateInk = selected ? T.Value : T.SubInk;
                if (hall.Art is { } art)
                {
                    c.DrawImage(art, Fit(art, new SKRect(cx - 74f, stop.Top - 2f, cx - 26f, railY - 18f)), Nearest);
                    Text(c, hall.When, cx + 22f, stop.Top + 22f, small, dateInk, pitch / 2f - 30f);
                }
                else Centered(c, Fitted(hall.When, small, pitch - 30f), cx, stop.Top + 22f, small, dateInk);
                var ink = selected ? T.ChipInk : T.Value;
                var game = hall.Game.StartsWith("Pokémon ", StringComparison.Ordinal) ? hall.Game["Pokémon ".Length..] : hall.Game;
                Centered(c, Fitted(game, small, plate.Width - 40f), plate.MidX, plate.Top + 22f, small, ink);
                Centered(c, Fitted(hall.Trainer, small, plate.Width - 40f), plate.MidX, plate.Top + 54f, small, selected ? T.ChipInk : T.SubInk);
            }
        }

        var bodyTop = timeline.Bottom + 16f;
        var bodyBottom = height - FooterHeight - 16f;
        var prev = SKRect.Empty;
        var next = SKRect.Empty;
        if (count > 0 && scene.Cursor >= 0 && scene.Cursor < count)
        {
            var hall = scene.Halls[scene.Cursor];
            PaintPlaque(c, new SKRect(0, bodyTop, PlaqueWidth, bodyBottom), hall, face);
            (prev, next) = PaintStage(c, new SKRect(PlaqueWidth + 20f, bodyTop, DesignWidth, bodyBottom), scene, face);
        }

        // What the buttons do here.
        var footer = new SKRect(0, height - FooterHeight, DesignWidth, height);
        using (var band = new SKPaint { Color = T.Band.WithAlpha(170), IsAntialias = true }) c.DrawRoundRect(footer, 16f, 16f, band);
        using (var frame = new SKPaint { Color = T.Rim.WithAlpha(120), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f })
            c.DrawRoundRect(SKRect.Inflate(footer, -1f, -1f), 16f, 16f, frame);
        Text(c, scene.Hint, footer.Left + 28f, footer.MidY, small, T.Value, footer.Width - 56f);

        c.Restore();
        SKRect Px(SKRect r) => new(r.Left * unit, r.Top * unit, r.Right * unit, r.Bottom * unit);
        return new MuseumHits([.. hallHits.Select(h => (h.Item1, Px(h.Item2)))], Px(prev), Px(next));
    }

    // ── The plaque: the game, its trainer, what the run reached ──

    private static void PaintPlaque(SKCanvas c, SKRect r, MuseumHallView hall, SKTypeface face)
    {
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(r, 28f, 28f, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(r, -1.5f, -1.5f), 28f, 28f, rim);

        using var name = new SKFont(face, 40f);
        using var line = new SKFont(face, 30f);
        using var small = new SKFont(face, 26f);

        // The game's tab: its art, its name, the trainer.
        var tab = new SKRect(r.Left + 14f, r.Top + 16f, r.Right - 14f, r.Top + 112f);
        using (var path = Tab(tab, 0f, 36f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        var textLeft = tab.Left + 24f;
        if (hall.Art is { } art)
        {
            c.DrawImage(art, Fit(art, new SKRect(tab.Left + 14f, tab.Top + 8f, tab.Left + 94f, tab.Bottom - 8f)), Nearest);
            textLeft = tab.Left + 110f;
        }
        Text(c, hall.Game, textLeft, tab.Top + 32f, name, Pksm.Ink, tab.Right - textLeft - 60f);
        var sign = hall.Gender switch { 0 => " ♂", 1 => " ♀", _ => "" };
        Text(c, hall.Trainer, textLeft, tab.Bottom - 26f, line, Pksm.Ink, tab.Right - textLeft - 120f);
        if (sign.Length > 0)
            Text(c, sign, textLeft + line.MeasureFallback(hall.Trainer), tab.Bottom - 26f, line, hall.Gender == 1 ? Pksm.Female : Pksm.SignalBlue, 60f);

        // The facts, on alternating bands.
        var y = tab.Bottom + 14f;
        for (var i = 0; i < hall.Facts.Count; i++)
        {
            var row = new SKRect(r.Left + 22f, y, r.Right - 22f, y + 46f);
            using (var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true }) c.DrawRoundRect(row, 10f, 10f, band);
            Text(c, hall.Facts[i], row.Left + 18f, row.MidY, line, i == 0 && hall.Champion ? Pksm.ShinyGold : T.Value, row.Width - 36f);
            y = row.Bottom + 6f;
        }

        // The badge case, eight to a row.
        if (hall.Badges.Count > 0)
        {
            y += 6f;
            var per = Math.Min(8, hall.Badges.Count);
            var size = Math.Min(64f, (r.Width - 44f) / per);
            for (var b = 0; b < hall.Badges.Count; b++)
            {
                var room = SKRect.Create(r.Left + 22f + b % per * size, y + b / per * size, size, size);
                BadgeArt.Draw(c, hall.Badges[b].Key, SKRect.Inflate(room, -4f, -4f), hall.Badges[b].Earned, T.Rim.WithAlpha(120));
            }
            y += (hall.Badges.Count + per - 1) / per * size + 8f;
        }

        // The records the game keeps, as many as fit.
        if (hall.Records.Count > 0)
        {
            using (var rule = new SKPaint { Color = T.Rim.WithAlpha(90), StrokeWidth = 2f }) c.DrawLine(r.Left + 22f, y + 4f, r.Right - 22f, y + 4f, rule);
            y += 12f;
            var rowHeight = 38f;
            var fit = Math.Max(0, (int)((r.Bottom - 16f - y) / rowHeight));
            var shown = Math.Min(fit, hall.Records.Count);
            if (shown < hall.Records.Count && shown > 0) shown--; // room for the "and more" line
            for (var i = 0; i < shown; i++)
            {
                var (facility, label, value) = hall.Records[i];
                var mid = y + rowHeight * (i + 0.5f);
                Text(c, $"{facility} · {label}", r.Left + 30f, mid, small, T.SubInk, r.Width - 200f);
                Right(c, value, r.Right - 30f, mid, line, T.Value);
            }
            if (shown < hall.Records.Count && fit > 0)
                Text(c, $"and {hall.Records.Count - shown} more", r.Left + 30f, y + rowHeight * (shown + 0.5f), small, T.SubInk, r.Width - 60f);
        }
    }

    // ── The stage: the champion team, large, standing free ──

    private static (SKRect Previous, SKRect Next) PaintStage(SKCanvas c, SKRect stage, MuseumScene scene, SKTypeface face)
    {
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(stage, 28f, 28f, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(stage, -1.5f, -1.5f), 28f, 28f, rim);
        TrainerCardPaint.Stripes(c, SKRect.Inflate(stage, -6f, -6f));

        using var label = new SKFont(face, 36f);
        using var name = new SKFont(face, 32f);
        using var small = new SKFont(face, 26f);

        // The team's name plate between its chevrons, when the hall keeps more than one.
        var head = new SKRect(stage.Left + 24f, stage.Top + 18f, stage.Right - 24f, stage.Top + 78f);
        var previous = SKRect.Empty;
        var next = SKRect.Empty;
        if (scene.Team is null)
        {
            Centered(c, Fitted(scene.NoTeam, label, stage.Width - 60f), stage.MidX, stage.MidY, label, T.SubInk);
            return (previous, next);
        }
        var plate = new SKRect(head.Left + 50f, head.Top, head.Right - 50f, head.Bottom);
        Chip(c, plate, focused: false);
        Text(c, scene.Team.Label, plate.Left + 36f, plate.MidY, label, T.ChipInk, plate.Width * 0.55f);
        var when = scene.TeamCount > 1 ? $"{scene.Team.When}{(scene.Team.When.Length > 0 ? " · " : "")}{scene.TeamIndex + 1} of {scene.TeamCount}" : scene.Team.When;
        Right(c, Fitted(when, small, plate.Width * 0.4f), plate.Right - 36f, plate.MidY, small, T.ChipInk);
        if (scene.TeamCount > 1)
        {
            previous = new SKRect(head.Left - 10f, head.Top, head.Left + 46f, head.Bottom);
            next = new SKRect(head.Right - 46f, head.Top, head.Right + 10f, head.Bottom);
            Arrow(c, previous.MidX, head.MidY, -1, scene.TeamIndex > 0 ? T.Rim : T.Rim.WithAlpha(70));
            Arrow(c, next.MidX, head.MidY, 1, scene.TeamIndex < scene.TeamCount - 1 ? T.Rim : T.Rim.WithAlpha(70));
        }

        // Six seats in two rows of three; each Pokémon stands on its row's floor line.
        var seatsTop = head.Bottom + 16f;
        var seatW = (stage.Width - 48f) / 3f;
        var seatH = (stage.Bottom - 16f - seatsTop) / 2f;
        for (var m = 0; m < scene.Team.Members.Count && m < 6; m++)
        {
            var member = scene.Team.Members[m];
            var seat = SKRect.Create(stage.Left + 24f + m % 3 * seatW, seatsTop + m / 3 * seatH, seatW, seatH);
            if (member.Sprite is { } sprite)
            {
                var room = new SKRect(seat.Left + 12f, seat.Top + 4f, seat.Right - 12f, seat.Bottom - 74f);
                var at = Fit(sprite, room);
                at.Offset(0, room.Bottom - at.Bottom); // feet on the floor
                c.DrawImage(sprite, at, Nearest);
            }
            Centered(c, Fitted(member.Shiny ? member.Name + " ★" : member.Name, name, seat.Width - 24f), seat.MidX, seat.Bottom - 50f, name, member.Shiny ? Pksm.ShinyGold : T.Value);
            Centered(c, Fitted(member.Detail, small, seat.Width - 24f), seat.MidX, seat.Bottom - 18f, small, T.SubInk);
        }
        return (previous, next);
    }
}
