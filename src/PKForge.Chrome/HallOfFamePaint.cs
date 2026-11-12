using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>One team in the list: its label, when, and its members as box icons.</summary>
public sealed record HallOfFameLine(string Label, string When, IReadOnlyList<(SKImage Sheet, SKRect Cell)> Icons);

/// <summary>One member of the team shown large.</summary>
public sealed record HallOfFameCard(string Name, string Detail, SKImage? Sprite, bool Shiny);

public sealed record HallOfFameScene(
    string Game,
    IReadOnlyList<HallOfFameLine> Teams,
    int Cursor,
    int Scroll,
    IReadOnlyList<HallOfFameCard> Members,
    string Hint,
    string Empty);

public sealed record HallOfFameHits(IReadOnlyList<(int Index, SKRect Rect)> Teams, int VisibleTeams);

/// <summary>
/// The Hall of Fame: the game's banner across the top, the inducted teams down the left in
/// the box well (label, date, the six as box icons), and the team under the cursor large at
/// the right, one card per member with the Pokémon, its name and its level.
/// </summary>
public static class HallOfFamePaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float BannerHeight = 92f;
    private const float ListWidth = 780f;
    private const float RowHeight = 116f;
    private const float FooterHeight = 70f;

    public static int VisibleTeams(SKSizeI size)
    {
        if (size.Width <= 0) return 1;
        var unit = size.Width / DesignWidth;
        var height = size.Height / unit;
        return Math.Max(1, (int)((height - FooterHeight - 16f - BannerHeight - 16f - 40f) / RowHeight));
    }

    public static HallOfFameHits Paint(SKCanvas c, SKImageInfo info, HallOfFameScene scene, SKTypeface face)
    {
        var unit = info.Width / DesignWidth;
        var height = info.Height / unit;
        c.Save();
        c.Scale(unit);

        // The banner.
        var banner = new SKRect(0, 0, DesignWidth, BannerHeight);
        using (var path = Tab(new SKRect(banner.Left, banner.Top, banner.Right - 60f, banner.Bottom), 0f, 40f))
        {
            Gradient(c, path, T.BannerTop, T.BannerBottom);
            Outline(c, path, Pksm.Ink, 3f);
        }
        using var title = new SKFont(face, 46f);
        using var small = new SKFont(face, 30f);
        Text(c, "HALL OF FAME", banner.Left + 36f, banner.MidY, title, Pksm.Ink, 600f);
        Right(c, scene.Game, banner.Right - 120f, banner.MidY, small, Pksm.Ink);

        var bodyTop = BannerHeight + 16f;
        var bodyBottom = height - FooterHeight - 16f;

        // The teams.
        var well = new SKRect(0, bodyTop, ListWidth, bodyBottom);
        StoragePaint.WellPanel(c, well, 1f);
        var hits = new List<(int, SKRect)>();
        var visible = VisibleTeams(new SKSizeI(info.Width, info.Height));
        using var label = new SKFont(face, 36f);
        var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
        if (scene.Teams.Count == 0)
            Centered(c, scene.Empty, well.MidX, well.MidY, small, T.SubInk);
        for (var i = scene.Scroll; i < scene.Teams.Count && i < scene.Scroll + visible; i++)
        {
            var top = well.Top + 20f + (i - scene.Scroll) * RowHeight;
            var row = new SKRect(well.Left + 30f, top, well.Right - 30f, top + RowHeight - 10f);
            hits.Add((i, row));
            var selected = i == scene.Cursor;
            if (selected) Chip(c, row, focused: true);
            else
            {
                using var band = new SKPaint { Color = EditorPaint.RowBand(i % 2 == 0), IsAntialias = true };
                c.DrawRoundRect(row, 12f, 12f, band);
            }
            var team = scene.Teams[i];
            Text(c, team.Label, row.Left + 28f, row.Top + 32f, label, selected ? T.ChipInk : T.Value, 340f);
            Right(c, team.When, row.Right - 28f, row.Top + 32f, small, selected ? T.ChipInk : T.SubInk);
            for (var m = 0; m < team.Icons.Count && m < 6; m++)
            {
                var (sheet, cell) = team.Icons[m];
                var dest = SKRect.Create(row.Left + 24f + m * 96f, row.Bottom - 64f, 80f, 60f);
                c.DrawImage(sheet, cell, dest, sampling);
            }
        }

        // The team under the cursor, large.
        var stage = new SKRect(ListWidth + 24f, bodyTop, DesignWidth, bodyBottom);
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(stage, 28f, 28f, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(stage, -1.5f, -1.5f), 28f, 28f, rim);
        TrainerCardPaint.Stripes(c, SKRect.Inflate(stage, -6f, -6f));
        var cardWidth = (stage.Width - 48f - 2 * 20f) / 3f;
        var cardHeight = (stage.Height - 48f - 20f) / 2f;
        using var name = new SKFont(face, 34f);
        for (var m = 0; m < 6; m++)
        {
            var card = SKRect.Create(stage.Left + 24f + m % 3 * (cardWidth + 20f), stage.Top + 24f + m / 3 * (cardHeight + 20f), cardWidth, cardHeight);
            if (m >= scene.Members.Count) continue;
            var member = scene.Members[m];
            if (member.Sprite is { } sprite)
            {
                var room = new SKRect(card.Left + 16f, card.Top + 12f, card.Right - 16f, card.Bottom - 84f);
                c.DrawImage(sprite, Fit(sprite, room), sampling);
            }
            Centered(c, Fitted(member.Shiny ? member.Name + " ★" : member.Name, name, card.Width - 24f), card.MidX, card.Bottom - 56f, name, member.Shiny ? Pksm.ShinyGold : T.Value);
            Centered(c, Fitted(member.Detail, small, card.Width - 24f), card.MidX, card.Bottom - 22f, small, T.SubInk);
        }

        // What the buttons do here.
        var footer = new SKRect(0, height - FooterHeight, DesignWidth, height);
        using (var band = new SKPaint { Color = T.Band.WithAlpha(170), IsAntialias = true }) c.DrawRoundRect(footer, 16f, 16f, band);
        using (var frame = new SKPaint { Color = T.Rim.WithAlpha(120), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f })
            c.DrawRoundRect(SKRect.Inflate(footer, -1f, -1f), 16f, 16f, frame);
        Text(c, scene.Hint, footer.Left + 28f, footer.MidY, small, T.Value, footer.Width - 56f);

        c.Restore();
        return new HallOfFameHits([.. hits.Select(h => (h.Item1, new SKRect(h.Item2.Left * unit, h.Item2.Top * unit, h.Item2.Right * unit, h.Item2.Bottom * unit)))], visible);
    }
}
