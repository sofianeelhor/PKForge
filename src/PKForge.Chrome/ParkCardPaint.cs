using PKForge.Domain;
using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The Poképark's resident card and dialogue box in the designer's art direction: a navy
/// frame along the bottom of the screen, so the park stays visible above it and the Pokémon
/// can be watched reacting. The PMD portrait sits in a framed square like the PMD games'
/// dialogue boxes. Sizes are design pixels (the 1920×1080 screen) scaled by a unit.
/// </summary>
public static class ParkCardPaint
{
    /// <summary>What the card shows for one resident.</summary>
    /// <param name="Needs">Hunger, energy, fun and company, 0–100.</param>
    /// <param name="Hearts">The bond with the player, 0–5.</param>
    /// <param name="Bonds">How it gets on with each other resident, for the Bonds page.</param>
    /// <param name="About">Its temperament, nature, habits… for the About page.</param>
    public sealed record Card(string Name, bool Shiny, string Personality, string Mood, string Activity, string Likes,
        float[] Needs, int Hearts, bool Fighter = false, IReadOnlyList<BondRow>? Bonds = null, IReadOnlyList<AboutRow>? About = null);

    /// <summary>Another resident and how this one feels about it: by species (love, family, rivals…)
    /// and by what happened in the park (friendship −100…100, rivalry 0…100).</summary>
    public sealed record BondRow(string Name, ParkPairKind Kind, int Friendship, int Rivalry);

    /// <summary>A line of the About page: a title and what it means.</summary>
    public sealed record AboutRow(string Title, string Text);

    /// <summary>The card's pages, switched with the tabs on its top edge (or X).</summary>
    public enum Page { Care, Bonds, About }

    public static readonly IReadOnlyList<(Page Page, string Label)> Pages = [(Page.Care, "Care"), (Page.Bonds, "Bonds"), (Page.About, "About")];

    /// <summary>The panel's height for a page: Bonds and About need more room.</summary>
    public static float HeightOf(Page page) => page == Page.Care ? DesignHeight : 470f;

    /// <summary>The tabs on the panel's top edge, for touch.</summary>
    public static SKRect PageTabRect(SKRect r, int index, float unit) =>
        SKRect.Create(r.Left + 322f * unit + index * 178f * unit, r.Top - 46f * unit, 170f * unit, 50f * unit);

    private static readonly string[] NeedNames = ["FOOD", "ENERGY", "FUN", "FRIENDS"];

    public enum Action { Talk, Snack, Play, Relax }

    public static readonly IReadOnlyList<(Action Action, string Label)> Actions =
        [(Action.Talk, "Talk"), (Action.Snack, "Snack"), (Action.Play, "Play"), (Action.Relax, "Relax")];

    /// <summary>The card's height in design pixels.</summary>
    public const float DesignHeight = 330f;

    internal static SKColor Cyan => EditorPaint.Cyan;
    internal static SKPaint Fill(SKColor c) => new() { Color = c, IsAntialias = true };
    internal static SKPaint Stroke(SKColor c, float w) => new() { Color = c, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = w };

    /// <summary>Where each action button sits, for touch.</summary>
    public static SKRect ActionRect(SKRect r, int index, float unit)
    {
        float w = 196f * unit, h = 112f * unit, gap = 14f * unit;
        var left = r.Right - 28f * unit - 2 * w - gap;
        var top = r.Top + 34f * unit;
        return SKRect.Create(left + index % 2 * (w + gap), top + index / 2 * (h + gap), w, h);
    }

    /// <summary>What the card's buttons offer: the four actions, the berries after Snack, the games after Play.</summary>
    public enum Menu { Actions, Berries, Games }

    /// <summary>The games offered after Play, by key; fighting types also offer to train you.</summary>
    public static IReadOnlyList<(string Key, string Label)> GamesFor(bool fighter) => fighter
        ? [("fetch", "Ball"), ("hide", "Hide"), ("race", "Race"), ("dig", "Dig"), ("brush", "Brush"), ("spar", "Train")]
        : [("fetch", "Ball"), ("hide", "Hide"), ("race", "Race"), ("dig", "Dig"), ("brush", "Brush")];

    /// <summary>How many buttons a row holds for that many choices (Back included).</summary>
    public static int ChoiceColumns(int count) => count > 6 ? 4 : 3;

    /// <summary>A choice after Snack or Play, then Back, in rows of three (four when there are more).</summary>
    public static SKRect ChoiceRect(SKRect r, int index, float unit, int count = 6)
    {
        var columns = ChoiceColumns(count);
        float gap = 14f * unit, h = 112f * unit, w = (406f * unit - (columns - 1) * gap) / columns;
        var left = r.Right - 28f * unit - 406f * unit;
        var top = r.Top + 34f * unit;
        return SKRect.Create(left + index % columns * (w + gap), top + index / columns * (h + gap), w, h);
    }

    /// <summary>The berries offered, by flavor: Cheri (Spicy), Aspear (Sour), Pecha (Sweet), Chesto (Dry), Rawst (Bitter).</summary>
    public static readonly IReadOnlyList<string> BerryNames = ["Cheri", "Aspear", "Pecha", "Chesto", "Rawst"];

    /// <summary>The "Leave the park" link under the buttons, for touch.</summary>
    public static SKRect LeaveRect(SKRect r, float unit) =>
        SKRect.Create(r.Right - 28f * unit - 199f * unit, r.Bottom - 50f * unit, 199f * unit, 36f * unit);

    /// <summary>The "Find in its box" link, left of the leave link, for touch.</summary>
    // Under the action buttons, beside the leave link: further left it ran into the FRIENDS row (QA 2026-11-12).
    public static SKRect FindRect(SKRect r, float unit) =>
        SKRect.Create(r.Right - 28f * unit - 406f * unit, r.Bottom - 50f * unit, 199f * unit, 36f * unit);

    /// <param name="selected">0–3 the action buttons, 4 the leave link, 5 the find link; in the berry or game choice, 0–4 the choices and 5 Back.</param>
    public static void PaintCard(SKCanvas c, SKRect r, Card card, SKImage? portrait, int selected, SKTypeface face, float unit, Menu menu = Menu.Actions,
        Page page = Page.Care)
    {
        // The page tabs sit on the top edge; the open one joins the panel.
        using (var tabFont = new SKFont(face, 26f * unit))
            for (var i = 0; i < Pages.Count; i++)
            {
                var tab = PageTabRect(r, i, unit);
                var open = Pages[i].Page == page;
                using (var fill = Fill(open ? StoragePaint.Frame : StoragePaint.Well)) c.DrawRoundRect(tab, 12f * unit, 12f * unit, fill);
                using (var edge = Stroke(open ? Cyan : StoragePaint.FrameEdge, 3f * unit)) c.DrawRoundRect(SKRect.Inflate(tab, -1.5f * unit, -1.5f * unit), 12f * unit, 12f * unit, edge);
                Centered(c, Pages[i].Label, tab.MidX, tab.MidY - 4f * unit, tabFont, open ? Cyan : EditorPaint.Label);
            }
        using (var hint = new SKFont(face, 22f * unit))
        {
            var spot = PageTabRect(r, Pages.Count, unit);
            var chip = SKRect.Create(spot.Left + 4f * unit, spot.Top + 6f * unit, hint.MeasureFallback("L R  pages") + 24f * unit, spot.Height - 16f * unit);
            using (var back = Fill(StoragePaint.Well.WithAlpha(230))) c.DrawRoundRect(chip, 10f * unit, 10f * unit, back);
            Text(c, "L R  pages", chip.Left + 12f * unit, chip.MidY, hint, EditorPaint.Label);
        }
        Frame(c, r, unit);
        // Redraw the open tab's join over the frame's top edge.
        var joined = PageTabRect(r, Pages.ToList().FindIndex(p => p.Page == page), unit);
        using (var seam = Fill(StoragePaint.Frame)) c.DrawRect(joined.Left + 3f * unit, r.Top - 2f * unit, joined.Width - 6f * unit, 8f * unit, seam);
        var photo = SKRect.Create(r.Left + 28f * unit, r.Top + 30f * unit, 268f * unit, 268f * unit);
        Portrait(c, photo, portrait, unit);

        // The name tab and the personality tab beside the portrait.
        var info = r.Left + 322f * unit;
        var nameTab = SKRect.Create(info, r.Top + 30f * unit, 520f * unit, 64f * unit);
        Tab(c, nameTab, StoragePaint.BannerTop, StoragePaint.BannerBottom, Pksm.Ink, unit);
        using var big = new SKFont(face, 34f * unit);
        Text(c, card.Name, nameTab.Left + 24f * unit, nameTab.MidY, big, Pksm.Ink, nameTab.Width - 90f * unit);
        if (card.Shiny) Centered(c, "★", nameTab.Right - 54f * unit, nameTab.MidY, big, Pksm.ShinyGold);
        for (var i = 0; i < 5; i++) Heart(c, nameTab.Right + 18f * unit + i * 40f * unit, nameTab.MidY, i < card.Hearts, 4f * unit);
        var kindTab = SKRect.Create(info, nameTab.Bottom + 6f * unit, 300f * unit, 50f * unit);
        Tab(c, kindTab, EditorPaint.CyanFill, EditorPaint.CyanFill, Cyan, unit);
        using var mid = new SKFont(face, 28f * unit);
        Text(c, card.Personality, kindTab.Left + 20f * unit, kindTab.MidY, mid, Cyan, kindTab.Width - 50f * unit);

        if (page == Page.Bonds) { PaintBonds(c, r, card, kindTab.Bottom + 24f * unit, face, unit); return; }
        if (page == Page.About) { PaintAbout(c, r, card, kindTab.Bottom + 24f * unit, face, unit); return; }

        // Mood, what it is doing, and its trait, as label and value lines.
        using var label = new SKFont(face, 22f * unit);
        using var value = new SKFont(face, 28f * unit);
        var y = kindTab.Bottom + 34f * unit;
        var needsLeft = ActionRect(r, 0, unit).Left - 28f * unit - 330f * unit;
        var textRight = needsLeft - 24f * unit;
        foreach (var (title, line) in new[] { ("MOOD", card.Mood), ("RIGHT NOW", card.Activity), ("LIKES", card.Likes) })
        {
            Text(c, title, info, y, label, EditorPaint.Label, 180f * unit);
            Text(c, line, info + 190f * unit, y, value, EditorPaint.Value, textRight - info - 190f * unit);
            y += 46f * unit;
        }

        // The four needs as rows of little icons, five each, halves included (like hearts).
        using var needFont = new SKFont(face, 22f * unit);
        for (var i = 0; i < NeedNames.Length; i++)
        {
            var rowY = kindTab.Bottom + 34f * unit + (i - 1) * 46f * unit;
            Text(c, NeedNames[i], needsLeft, rowY, needFont, EditorPaint.Label, 108f * unit);
            NeedIcons(c, i, needsLeft + 110f * unit, rowY, 44f * unit, 4.4f * unit, i < card.Needs.Length ? card.Needs[i] : 0);
        }

        if (menu != Menu.Actions)
        {
            var games = GamesFor(card.Fighter);
            var count = (menu == Menu.Berries ? BerryNames.Count : games.Count) + 1;
            for (var i = 0; i < count; i++)
            {
                var back = i == count - 1;
                var caption = back ? "Back" : menu == Menu.Berries ? BerryNames[i] : games[i].Label;
                ChoiceButton(c, ChoiceRect(r, i, unit, count), back ? null : menu == Menu.Berries ? Berry(i) : GameIcons[i], caption, i == selected, face, unit);
            }
            return;
        }
        for (var i = 0; i < Actions.Count; i++)
            Button(c, ActionRect(r, i, unit), Actions[i].Action, Actions[i].Label, i == selected, face, unit);
        var leave = LeaveRect(r, unit);
        using var small = new SKFont(face, 24f * unit);
        Centered(c, selected == 4 ? "▶ Leave the park" : "Leave the park", leave.MidX, leave.MidY, small,
            selected == 4 ? Cyan : EditorPaint.Label);
        var find = FindRect(r, unit);
        Centered(c, selected == 5 ? "▶ Find in its box" : "Find in its box", find.MidX, find.MidY, small,
            selected == 5 ? Cyan : EditorPaint.Label);
    }

    // Every other resident in two columns: an icon for how it feels by species, the name, a word
    // for the bond, and bars for friendship and rivalry.
    private static void PaintBonds(SKCanvas c, SKRect r, Card card, float top, SKTypeface face, float unit)
    {
        var rows = card.Bonds ?? [];
        using var name = new SKFont(face, 28f * unit);
        using var small = new SKFont(face, 22f * unit);
        var left = r.Left + 322f * unit;
        if (rows.Count == 0)
        {
            Text(c, "No one else lives in the park yet.", left, top + 20f * unit, name, EditorPaint.Label);
            return;
        }
        var columnWidth = (r.Right - 28f * unit - left - 30f * unit) / 2;
        var rowHeight = 46f * unit;
        var perColumn = Math.Max(1, (int)((r.Bottom - 20f * unit - top) / rowHeight));
        // The strongest bonds come first; when they do not all fit, the last line says how many more.
        var shown = rows.Count <= perColumn * 2 ? rows.Count : perColumn * 2 - 1;
        if (shown < rows.Count)
            Text(c, $"… and {rows.Count - shown} more, getting on quietly", left + (columnWidth + 30f * unit) + 34f * unit,
                top + (perColumn - 1) * rowHeight + 14f * unit, small, EditorPaint.Label, columnWidth - 40f * unit);
        for (var i = 0; i < shown; i++)
        {
            var row = rows[i];
            var x = left + i / perColumn * (columnWidth + 30f * unit);
            var y = top + i % perColumn * rowHeight + 14f * unit;
            BondIcon(c, row.Kind, row.Friendship, row.Rivalry, x + 12f * unit, y, unit);
            Text(c, row.Name, x + 34f * unit, y, name, EditorPaint.Value, 220f * unit);
            Text(c, BondWord(row), x + 264f * unit, y, small, BondColor(row), 190f * unit);
            var barLeft = x + 460f * unit; var barWidth = columnWidth - 470f * unit;
            // Friendship in green on top, rivalry in red below.
            using var well = Fill(StoragePaint.Well);
            foreach (var (amount, color, offset) in new[] { (Math.Max(0, row.Friendship), new SKColor(0x6c, 0xd0, 0x7a), -15f), (row.Rivalry, new SKColor(0xe8, 0x5a, 0x4f), 3f) })
            {
                var track = SKRect.Create(barLeft, y + offset * unit, barWidth, 11f * unit);
                c.DrawRoundRect(track, 5f * unit, 5f * unit, well);
                if (amount <= 0) continue;
                using var fill = Fill(color);
                c.DrawRoundRect(SKRect.Create(track.Left, track.Top, track.Width * Math.Min(100, amount) / 100f, track.Height), 5f * unit, 5f * unit, fill);
            }
        }
    }

    /// <summary>A word for a bond: the species' own feeling first, then what happened in the park.</summary>
    public static string BondWord(BondRow row) => row.Kind switch
    {
        ParkPairKind.Love => "in love",
        ParkPairKind.Family => "family",
        ParkPairKind.Friends => "friends",
        ParkPairKind.Rivals => "sworn rivals",
        ParkPairKind.Fears => "scared of it",
        ParkPairKind.Hunts => "hunts it",
        _ when row.Rivalry >= 40 => "rivals",
        _ when row.Friendship >= 40 => "close friends",
        _ when row.Rivalry >= 15 => "tense",
        _ when row.Friendship >= 15 => "getting along",
        _ => "barely knows it"
    };

    private static SKColor BondColor(BondRow row) => row.Kind switch
    {
        ParkPairKind.Love => new SKColor(0xe0, 0x4f, 0x6a),
        ParkPairKind.Rivals or ParkPairKind.Hunts => new SKColor(0xe8, 0x5a, 0x4f),
        ParkPairKind.Fears => new SKColor(0x7f, 0xb8, 0xe8),
        _ when row.Rivalry >= 15 => new SKColor(0xe8, 0x9a, 0x4f),
        _ when row.Friendship >= 15 || row.Kind is ParkPairKind.Family or ParkPairKind.Friends => new SKColor(0x6c, 0xd0, 0x7a),
        _ => EditorPaint.Label
    };

    private static void BondIcon(SKCanvas c, ParkPairKind kind, int friendship, int rivalry, float x, float midY, float unit)
    {
        string[] rows = kind switch
        {
            ParkPairKind.Love => [".#.#.", "#####", ".###.", "..#.."],
            ParkPairKind.Family => ["..#..", ".###.", "#####", ".#.#."],
            ParkPairKind.Friends => ["..#..", "#####", ".###.", ".#.#."],
            ParkPairKind.Rivals or ParkPairKind.Hunts => ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"],
            ParkPairKind.Fears => ["..#..", ".###.", ".###.", "..#.."],
            _ when rivalry >= 15 => ["#...#", ".#.#.", "..#..", ".#.#.", "#...#"],
            _ when friendship >= 15 => ["..#..", "#####", ".###.", ".#.#."],
            _ => [".....", "..#..", ".....", "....."]
        };
        var color = BondColor(new BondRow("", kind, friendship, rivalry));
        var px = 4f * unit;
        using var paint = new SKPaint { Color = color, IsAntialias = false };
        for (var j = 0; j < rows.Length; j++)
            for (var i = 0; i < rows[j].Length; i++)
                if (rows[j][i] == '#') c.DrawRect(MathF.Round(x - 10f * unit + i * px), MathF.Round(midY - rows.Length * px / 2 + j * px), MathF.Ceiling(px), MathF.Ceiling(px), paint);
    }

    // Its temperament, nature, tastes, habits, what touching it does, its records: everything the
    // park knows about it, each with what it means.
    private static void PaintAbout(SKCanvas c, SKRect r, Card card, float top, SKTypeface face, float unit)
    {
        using var title = new SKFont(face, 22f * unit);
        using var text = new SKFont(face, 28f * unit);
        var left = r.Left + 322f * unit;
        var y = top + 14f * unit;
        foreach (var row in card.About ?? [])
        {
            if (y > r.Bottom - 20f * unit) break;
            Text(c, row.Title.ToUpperInvariant(), left, y, title, EditorPaint.Label, 230f * unit);
            Text(c, row.Text, left + 240f * unit, y, text, EditorPaint.Value, r.Right - 28f * unit - left - 240f * unit);
            y += 44f * unit;
        }
    }

    /// <summary>How many lines of text the dialogue box shows at once.</summary>
    public const int DialogueLines = 4;

    private static float DialogueTextLeft(SKRect r, bool hasPortrait, float unit) => r.Left + (hasPortrait ? 322f : 40f) * unit;

    /// <summary>
    /// <paramref name="text"/> cut into the pages the dialogue box can show, one box-full
    /// each. A long explanation used to be cut after its fourth line with no way to read on.
    /// </summary>
    public static IReadOnlyList<string> DialoguePages(SKRect r, string text, bool hasPortrait, SKTypeface face, float unit)
    {
        using var body = new SKFont(face, 34f * unit);
        var lines = Wrap(text, body, r.Right - 48f * unit - DialogueTextLeft(r, hasPortrait, unit));
        // Wrapping is greedy, word by word: a page joined back with spaces wraps into the same lines.
        var pages = lines.Chunk(DialogueLines).Select(page => string.Join(' ', page)).ToArray();
        return pages.Length == 0 ? [""] : pages;
    }

    /// <summary>The dialogue box: framed portrait, the speaker's tab, and the text so far.
    /// <paramref name="more"/> draws the arrow that promises another page.</summary>
    public static void PaintDialogue(SKCanvas c, SKRect r, string speaker, string text, SKImage? portrait, bool more, long time, SKTypeface face, float unit)
    {
        Frame(c, r, unit);
        var info = DialogueTextLeft(r, portrait is not null, unit);
        if (portrait is not null)
            Portrait(c, SKRect.Create(r.Left + 28f * unit, r.Top + 30f * unit, 268f * unit, 268f * unit), portrait, unit);
        var nameTab = SKRect.Create(info, r.Top + 30f * unit, 420f * unit, 60f * unit);
        Tab(c, nameTab, StoragePaint.BannerTop, StoragePaint.BannerBottom, Pksm.Ink, unit);
        using var big = new SKFont(face, 32f * unit);
        Text(c, speaker, nameTab.Left + 24f * unit, nameTab.MidY, big, Pksm.Ink, nameTab.Width - 50f * unit);
        using var body = new SKFont(face, 34f * unit);
        var lines = Wrap(text, body, r.Right - 48f * unit - info);
        var y = nameTab.Bottom + 46f * unit;
        foreach (var line in lines.Take(DialogueLines))
        {
            Text(c, line, info, y, body, EditorPaint.Value, r.Right - info);
            y += 48f * unit;
        }
        if (more && time / 400 % 2 == 0)
        {
            // The PMD "more" arrow, blinking at the bottom right.
            using var arrow = new SKPath();
            float ax = r.Right - 52f * unit, ay = r.Bottom - 46f * unit, s = 12f * unit;
            arrow.MoveTo(ax - s, ay - s / 2); arrow.LineTo(ax + s, ay - s / 2); arrow.LineTo(ax, ay + s / 2); arrow.Close();
            using var fill = Fill(Cyan);
            c.DrawPath(arrow, fill);
        }
    }

    /// <summary>What the lower screen shows for the selected resident.</summary>
    public sealed record Journal(Card Card, string Taste, string Memory, IReadOnlyList<string> Recent, string Bonds = "");

    /// <summary>The lower screen while the park is open (1240×1080 design pixels): the portrait
    /// large, the name, hearts and mood, the four needs, what it is doing, its tastes and the
    /// park's latest moments.</summary>
    public static void PaintJournal(SKCanvas c, SKRect r, Journal journal, SKImage? portrait, SKTypeface face, float unit)
    {
        var card = journal.Card;
        var panel = SKRect.Inflate(r, -24f * unit, -24f * unit);
        Frame(c, panel, unit);
        var photo = SKRect.Create(panel.Left + 32f * unit, panel.Top + 32f * unit, 348f * unit, 348f * unit);
        Portrait(c, photo, portrait, unit);

        var info = photo.Right + 32f * unit;
        var nameTab = SKRect.Create(info, photo.Top, panel.Right - 32f * unit - info, 72f * unit);
        Tab(c, nameTab, StoragePaint.BannerTop, StoragePaint.BannerBottom, Pksm.Ink, unit);
        using var big = new SKFont(face, 40f * unit);
        Text(c, card.Name, nameTab.Left + 26f * unit, nameTab.MidY, big, Pksm.Ink, nameTab.Width - 100f * unit);
        if (card.Shiny) Centered(c, "★", nameTab.Right - 60f * unit, nameTab.MidY, big, Pksm.ShinyGold);
        var kindTab = SKRect.Create(info, nameTab.Bottom + 8f * unit, Math.Min(380f * unit, nameTab.Width), 54f * unit);
        Tab(c, kindTab, EditorPaint.CyanFill, EditorPaint.CyanFill, Cyan, unit);
        using var mid = new SKFont(face, 30f * unit);
        Text(c, card.Personality, kindTab.Left + 22f * unit, kindTab.MidY, mid, Cyan, kindTab.Width - 50f * unit);
        for (var i = 0; i < 5; i++) Heart(c, info + i * 52f * unit, kindTab.Bottom + 52f * unit, i < card.Hearts, 6f * unit);
        using var value = new SKFont(face, 32f * unit);
        using var label = new SKFont(face, 24f * unit);
        Text(c, "MOOD", info, kindTab.Bottom + 118f * unit, label, EditorPaint.Label, 150f * unit);
        Text(c, card.Mood, info + 150f * unit, kindTab.Bottom + 118f * unit, value, EditorPaint.Value, panel.Right - 32f * unit - info - 150f * unit);
        Text(c, "TASTE", info, kindTab.Bottom + 166f * unit, label, EditorPaint.Label, 150f * unit);
        Text(c, journal.Taste, info + 150f * unit, kindTab.Bottom + 166f * unit, value, EditorPaint.Value, panel.Right - 32f * unit - info - 150f * unit);

        // The four needs, wide.
        var y = photo.Bottom + 58f * unit;
        for (var i = 0; i < NeedNames.Length; i++)
        {
            Text(c, NeedNames[i], panel.Left + 32f * unit, y, value, EditorPaint.Label, 200f * unit);
            NeedIcons(c, i, panel.Left + 240f * unit, y, 54f * unit, 4.6f * unit, i < card.Needs.Length ? card.Needs[i] : 0);
            y += 54f * unit;
        }

        y += 18f * unit;
        foreach (var (title, line) in new[] { ("RIGHT NOW", card.Activity), ("LIKES", card.Likes), ("BONDS", journal.Bonds) })
        {
            Text(c, title, panel.Left + 32f * unit, y, label, EditorPaint.Label, 200f * unit);
            Text(c, line, panel.Left + 240f * unit, y, value, EditorPaint.Value, panel.Width - 272f * unit);
            y += 50f * unit;
        }
        y += 6f * unit;
        Text(c, "MEMORY", panel.Left + 32f * unit, y, label, EditorPaint.Label, 200f * unit);
        foreach (var line in Wrap(journal.Memory, value, panel.Width - 272f * unit).Take(2))
        {
            Text(c, line, panel.Left + 240f * unit, y, value, EditorPaint.Value, panel.Width - 272f * unit);
            y += 42f * unit;
        }

        // The park's latest moments, newest first.
        if (journal.Recent.Count == 0) return;
        y += 16f * unit;
        var sectionHeight = 56f * unit;
        EditorPaint.PaintSection(c, SKRect.Create(panel.Left, y - sectionHeight / 2, panel.Width, sectionHeight), "In the park", face, unit);
        y += 52f * unit;
        using var small = new SKFont(face, 26f * unit);
        foreach (var line in journal.Recent)
        {
            if (y > panel.Bottom - 30f * unit) break;
            Text(c, "· " + line, panel.Left + 32f * unit, y, small, EditorPaint.Value, panel.Width - 64f * unit);
            y += 38f * unit;
        }
    }

    public static IReadOnlyList<string> Wrap(string text, SKFont font, float width)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && font.MeasureFallback(next) > width) { lines.Add(line); line = word; }
            else line = next;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    // Pixel icons for the needs, 7×7: k outline, f colour, l highlight, s second colour.
    private static readonly string[][] NeedGlyphs =
    [
        // Food: an Oran Berry with its leaf.
        ["...ss..", "..ss...", ".kkkkk.", "kflfffk", "kfffffk", "kfffffk", ".kkkkk."],
        // Energy: a bolt.
        ["..kkkk.", ".kflk..", "kffk...", "kffffk.", "..kffk.", ".kffk..", ".kkk..."],
        // Fun: a Poké Ball.
        [".kkkkk.", "kflfffk", "kfffffk", "kkkskkk", "ksssssk", "ksssssk", ".kkkkk."],
        // Friends: a speech bubble, chatting.
        [".kkkkk.", "kflfffk", "kfffffk", "ksfsfsk", "kfffffk", ".kfkkk.", ".kk...."],
    ];

    private static readonly (SKColor Fill, SKColor Light, SKColor Second)[] NeedColors =
    [
        (new(0x3a, 0x7c, 0xe0), new(0xa0, 0xc8, 0xff), new(0x4c, 0xb0, 0x50)),
        (new(0xf8, 0xc8, 0x30), new(0xff, 0xf0, 0xa0), new(0xf8, 0xc8, 0x30)),
        (new(0xe0, 0x40, 0x40), new(0xff, 0xa8, 0xa8), new(0xf2, 0xf2, 0xf2)),
        (new(0xf0, 0x90, 0xb0), new(0xff, 0xc8, 0xd8), new(0x8a, 0x3a, 0x5c)),
    ];

    /// <summary>A need as five icons, each worth 20 points, a half icon for 10 (like Minecraft's
    /// hunger bar); the empty ones are drawn as an outline.</summary>
    private static void NeedIcons(SKCanvas c, int need, float left, float midY, float step, float px, float value)
    {
        var glyph = NeedGlyphs[need % NeedGlyphs.Length];
        var (fill, light, second) = NeedColors[need % NeedColors.Length];
        var halves = (int)MathF.Round(Math.Clamp(value, 0, 100) / 10);
        var top = midY - glyph.Length * px / 2;
        using var paint = new SKPaint { IsAntialias = false };
        for (var icon = 0; icon < 5; icon++)
        {
            var x = left + icon * step;
            // Full, half (its left side coloured) or empty.
            var filled = Math.Clamp(halves - icon * 2, 0, 2);
            for (var j = 0; j < glyph.Length; j++)
                for (var i = 0; i < glyph[j].Length; i++)
                {
                    var cell = glyph[j][i];
                    if (cell == '.') continue;
                    var lit = filled == 2 || filled == 1 && i < glyph[j].Length / 2 + 1;
                    paint.Color = !lit
                        ? cell == 'k' ? EditorPaint.Label.WithAlpha(150) : StoragePaint.Well
                        : cell switch { 'k' => new SKColor(0x30, 0x24, 0x2a), 'l' => light, 's' => second, _ => fill };
                    c.DrawRect(MathF.Round(x + i * px), MathF.Round(top + j * px), MathF.Ceiling(px), MathF.Ceiling(px), paint);
                }
        }
    }

    private static readonly string[] HeartGlyph = [".##.##.", "#######", "#######", ".#####.", "..###..", "...#..."];

    // A pixel heart, filled red for each earned heart, an outline otherwise.
    private static void Heart(SKCanvas c, float x, float midY, bool full, float px)
    {
        var left = x; var top = midY - HeartGlyph.Length * px / 2;
        using var paint = new SKPaint { IsAntialias = false };
        for (var j = 0; j < HeartGlyph.Length; j++)
            for (var i = 0; i < HeartGlyph[j].Length; i++)
            {
                if (HeartGlyph[j][i] != '#') continue;
                var edge = j == 0 || i == 0 || i == HeartGlyph[j].Length - 1 || HeartGlyph[j][Math.Max(0, i - 1)] != '#' ||
                    HeartGlyph[j][Math.Min(HeartGlyph[j].Length - 1, i + 1)] != '#' || j + 1 >= HeartGlyph.Length || HeartGlyph[j + 1][i] != '#';
                paint.Color = full ? (edge ? new SKColor(0x9c, 0x2a, 0x3c) : new SKColor(0xe0, 0x4f, 0x6a)) : (edge ? EditorPaint.Label : StoragePaint.Well);
                c.DrawRect(MathF.Round(left + i * px), MathF.Round(top + j * px), MathF.Ceiling(px), MathF.Ceiling(px), paint);
            }
    }

    internal static void Frame(SKCanvas c, SKRect r, float unit)
    {
        var edge = 3f * unit;
        using (var shadow = Fill(new SKColor(0, 0, 0, 90))) c.DrawRoundRect(SKRect.Create(r.Left, r.Top + 8f * unit, r.Width, r.Height), 22f * unit, 22f * unit, shadow);
        using (var fill = Fill(StoragePaint.Frame)) c.DrawRoundRect(r, 22f * unit, 22f * unit, fill);
        using var stroke = Stroke(StoragePaint.FrameEdge, edge);
        c.DrawRoundRect(SKRect.Inflate(r, -edge / 2, -edge / 2), 22f * unit, 22f * unit, stroke);
    }

    // The portrait as a framed photo: its own painted background stays inside the frame.
    private static void Portrait(SKCanvas c, SKRect r, SKImage? portrait, float unit)
    {
        using (var ink = Fill(Pksm.Ink)) c.DrawRect(r, ink);
        var rim = SKRect.Inflate(r, -6f * unit, -6f * unit);
        using (var light = Fill(Cyan)) c.DrawRect(rim, light);
        var inner = SKRect.Inflate(rim, -4f * unit, -4f * unit);
        using (var well = Fill(StoragePaint.Well)) c.DrawRect(inner, well);
        if (portrait is null) return;
        // The largest whole-number scale that fits, so PMD's pixels stay square.
        var scale = MathF.Max(1, MathF.Floor(MathF.Min(inner.Width / portrait.Width, inner.Height / portrait.Height)));
        float w = portrait.Width * scale, h = portrait.Height * scale;
        c.DrawImage(portrait, SKRect.Create(MathF.Round(inner.MidX - w / 2), MathF.Round(inner.MidY - h / 2), w, h),
            new SKSamplingOptions(SKFilterMode.Nearest));
    }

    // Pixel icons as rows of characters and the color each character stands for.
    private sealed record Glyph(string[] Rows, Dictionary<char, SKColor> Colors);

    private static readonly SKColor IconInk = new(0x2b, 0x2d, 0x42);

    private static Glyph Berry(int flavor)
    {
        var color = ParkProps.BerryColors[flavor];
        return new(["...##..", "..#....", ".#####.", "#,,,,,#", "#,',,,#", "#,,,,,#", ".#####."],
            new() { ['#'] = IconInk, [','] = color, ['\''] = PksmPaint.Mix(color, SKColors.White, .6f) });
    }

    private static readonly Glyph[] GameIcons =
    [
        new([".#####.", "#rrrrr#", "#rrrrr#", "###o###", "#wwwww#", "#wwwww#", ".#####."],
            new() { ['#'] = IconInk, ['r'] = new(0xe8, 0x5a, 0x4f), ['w'] = new(0xfa, 0xf6, 0xea), ['o'] = new(0xfa, 0xf6, 0xea) }),
        new(["..ggg..", ".ggggg.", "ggggggg", "gwkgwkg", "ggggggg", ".ggggg.", "..#.#.."],
            new() { ['g'] = new(0x4e, 0x9a, 0x3c), ['w'] = new(0xff, 0xff, 0xff), ['k'] = IconInk, ['#'] = new(0x7a, 0x55, 0x30) }),
        new(["#rrrr..", "#rrrrr.", "#rrrr..", "#......", "#......", "#......", "#......"],
            new() { ['#'] = new(0x7a, 0x55, 0x30), ['r'] = new(0xe8, 0x5a, 0x4f) }),
        new(["..#ss#.", "..#ss#.", "...##..", "...##..", "..####.", ".#mmmm#", "..#mm#."],
            new() { ['#'] = IconInk, ['s'] = new(0xc8, 0xcc, 0xd8), ['m'] = new(0x9a, 0x9e, 0xae) }),
        new([".#####.", "#ppppp#", "#b.b.b#", ".#####.", "...#...", "...#...", "...#..."],
            new() { ['#'] = IconInk, ['p'] = new(0xd8, 0x8a, 0xc8), ['b'] = new(0xfa, 0xf6, 0xea) }),
        new([".####..", "#rrrr#.", "#rrrrr#", "#rrrrr#", ".#####.", "..#w#..", "..###.."],
            new() { ['#'] = IconInk, ['r'] = new(0xe8, 0x5a, 0x4f), ['w'] = new(0xfa, 0xf6, 0xea) }),
    ];

    private static void ChoiceButton(SKCanvas c, SKRect r, Glyph? glyph, string text, bool selected, SKTypeface face, float unit)
    {
        var box = Plate(c, r, selected, unit);
        if (glyph is not null)
        {
            float px = 7f * unit, left = box.MidX - glyph.Rows[0].Length * px / 2, top = box.Top + 42f * unit - glyph.Rows.Length * px / 2;
            using var paint = new SKPaint { IsAntialias = false };
            for (var j = 0; j < glyph.Rows.Length; j++)
                for (var i = 0; i < glyph.Rows[j].Length; i++)
                {
                    if (!glyph.Colors.TryGetValue(glyph.Rows[j][i], out var color)) continue;
                    paint.Color = color;
                    c.DrawRect(MathF.Round(left + i * px), MathF.Round(top + j * px), MathF.Ceiling(px), MathF.Ceiling(px), paint);
                }
        }
        using var font = new SKFont(face, 26f * unit);
        Centered(c, text, box.MidX, glyph is not null ? box.Bottom - 22f * unit : box.MidY, font, selected ? EditorPaint.ChipInk : EditorPaint.Value);
    }

    // A button's plate: the selected one is lifted, filled and rimmed, with no glow and no shadow.
    public static SKRect Plate(SKCanvas c, SKRect r, bool selected, float unit)
    {
        var lift = selected ? 4f * unit : 0;
        var box = SKRect.Create(r.Left, r.Top - lift, r.Width, r.Height);
        using (var fill = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, box.Top), new SKPoint(0, box.Bottom),
            selected ? [EditorPaint.ChipTop, EditorPaint.ChipBottom] : [StoragePaint.Well, StoragePaint.Well], SKShaderTileMode.Clamp) })
            c.DrawRoundRect(box, 14f * unit, 14f * unit, fill);
        using (var outline = Stroke(selected ? Cyan : StoragePaint.WellEdge, (selected ? 4f : 3f) * unit))
            c.DrawRoundRect(SKRect.Inflate(box, -2f * unit, -2f * unit), 14f * unit, 14f * unit, outline);
        return box;
    }

    private static void Button(SKCanvas c, SKRect r, Action action, string text, bool selected, SKTypeface face, float unit)
    {
        var box = Plate(c, r, selected, unit);
        Icon(c, action, box.MidX, box.Top + 42f * unit, 7f * unit);
        using var font = new SKFont(face, 28f * unit);
        Centered(c, text, box.MidX, box.Bottom - 24f * unit, font, selected ? EditorPaint.ChipInk : EditorPaint.Value);
    }

    // Pixel icons, one glyph pixel = px screen pixels, centered on (x, y).
    private static readonly Dictionary<Action, (string[] Rows, SKColor[] Colors)> Icons = new()
    {
        [Action.Talk] = ([".#######.", "#,,,,,,,#", "#,#,#,#,#", "#,,,,,,,#", ".###,###.", "...##....", "...#....."],
            [new(0x2b, 0x2d, 0x42), new(0xff, 0xfb, 0xec)]),
        [Action.Snack] = (["....##...", "...#.....", ".#####...", "#,,,,,#..", "#,',,,#..", "#,,,,,#..", ".#####..."],
            [new(0x2b, 0x2d, 0x42), new(0x3f, 0x7c, 0xd8), new(0x9c, 0xc8, 0xff)]),
        [Action.Play] = ([".#####.", "#,,,,,#", "#,,,,,#", "###o###", "#'''''#", "#'''''#", ".#####."],
            [new(0x2b, 0x2d, 0x42), new(0xe8, 0x5a, 0x4f), new(0xfa, 0xf6, 0xea), new(0xfa, 0xf6, 0xea)]),
        [Action.Relax] = (["...####", "...#..#", "...#..#", "...#..#", ".###.##", "####.##", ".##...."],
            [new(0x2b, 0x2d, 0x42)]),
    };

    private static void Icon(SKCanvas c, Action action, float x, float y, float px)
    {
        var (rows, colors) = Icons[action];
        var left = x - rows[0].Length * px / 2; var top = y - rows.Length * px / 2;
        using var paint = new SKPaint { IsAntialias = false };
        for (var j = 0; j < rows.Length; j++)
            for (var i = 0; i < rows[j].Length; i++)
            {
                var color = rows[j][i] switch { '#' => colors[0], ',' => colors.ElementAtOrDefault(1), '\'' => colors.ElementAtOrDefault(2), 'o' => colors.ElementAtOrDefault(3), _ => SKColors.Transparent };
                if (color == SKColors.Transparent || color == default) continue;
                paint.Color = color;
                c.DrawRect(MathF.Round(left + i * px), MathF.Round(top + j * px), MathF.Ceiling(px), MathF.Ceiling(px), paint);
            }
    }

    // A tab rounded on the left, its right edge slanting out toward the bottom.
    internal static void Tab(SKCanvas c, SKRect r, SKColor top, SKColor bottom, SKColor edge, float unit)
    {
        var radius = 14f * unit; var slant = 22f * unit;
        using var path = new SKPath();
        path.MoveTo(r.Left + radius, r.Top);
        path.LineTo(r.Right - slant, r.Top);
        path.LineTo(r.Right, r.Bottom);
        path.LineTo(r.Left + radius, r.Bottom);
        path.ArcTo(new SKRect(r.Left, r.Bottom - 2 * radius, r.Left + 2 * radius, r.Bottom), 90, 90, false);
        path.LineTo(r.Left, r.Top + radius);
        path.ArcTo(new SKRect(r.Left, r.Top, r.Left + 2 * radius, r.Top + 2 * radius), 180, 90, false);
        path.Close();
        using (var fill = new SKPaint { IsAntialias = true, Shader = SKShader.CreateLinearGradient(new SKPoint(0, r.Top), new SKPoint(0, r.Bottom), [top, bottom], SKShaderTileMode.Clamp) })
            c.DrawPath(path, fill);
        using var outline = Stroke(edge, 3f * unit);
        c.DrawPath(path, outline);
    }

    internal static void Text(SKCanvas c, string text, float x, float midY, SKFont font, SKColor color, float maxWidth = float.MaxValue)
    {
        if (font.MeasureFallback(text) > maxWidth)
        {
            while (text.Length > 1 && font.MeasureFallback(text + "…") > maxWidth) text = text[..^1];
            text += "…";
        }
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        font.GetFontMetrics(out var m);
        c.DrawTextFallback(text, x, midY - (m.Ascent + m.Descent) / 2, SKTextAlign.Left, font, paint);
    }

    public static void Centered(SKCanvas c, string text, float x, float midY, SKFont font, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        font.GetFontMetrics(out var m);
        c.DrawTextFallback(text, x, midY - (m.Ascent + m.Descent) / 2, SKTextAlign.Center, font, paint);
    }
}
