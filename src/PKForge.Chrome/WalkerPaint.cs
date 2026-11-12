using SkiaSharp;
using static PKForge.Chrome.PaintKit;

namespace PKForge.Chrome;

/// <summary>The pages the device's ◀ ▶ buttons turn through.</summary>
public enum WalkerPage { Walk, Radar, Dowsing, Bag, Sync, Info, Debug }

/// <summary>What the course looks like behind the walking Pokémon.</summary>
public enum WalkerScenery { Field, Forest, Rocky, Water, Town, Cave }

/// <summary>Where the device stands: at the left with a card of facts beside it, or alone in the middle.</summary>
public enum WalkerLayout { Side, Centered }

/// <summary>Everything the Pokéwalker screen shows at one moment.</summary>
/// <param name="Walker">The walking Pokémon in LCD tones (<see cref="WalkerLcd.Convert"/>); null when nobody walks.</param>
/// <param name="Frame">The step frame (0 or 1).</param>
/// <param name="Targets">Bushes or grass patches on the screen while a search is on (0: none).</param>
/// <param name="Target">The bush or patch the arrow is over; -1 when none.</param>
/// <param name="Rustle">The bush rustling now (-1: none), with <paramref name="Marks"/> "!" over it.</param>
/// <param name="Lcd">The screen's bottom line (what the centre button does, or news).</param>
/// <param name="Facts">Label and value pairs for the card beside the device.</param>
/// <param name="Pressed">The device button shown pressed: 0 ◀, 1 centre, 2 ▶, -1 none.</param>
public sealed record WalkerScene(
    IReadOnlyList<WalkerPage> Pages,
    int Page,
    WalkerSprite? Walker,
    int Frame,
    WalkerScenery Scenery,
    long Steps,
    int Watts,
    IReadOnlyList<string> Catches,
    IReadOnlyList<string> Items,
    int Targets,
    int Target,
    int Rustle,
    int Marks,
    string Lcd,
    string Course,
    IReadOnlyList<(string Label, string Value)> Facts,
    string Status,
    int Pressed = -1,
    string Headline = "",
    IReadOnlyList<string>? Notes = null);

/// <summary>Where the device's buttons, its screen and the screen's targets landed, in canvas pixels.</summary>
public sealed record WalkerHits(SKRect Left, SKRect Centre, SKRect Right, SKRect Screen, IReadOnlyList<SKRect> Targets);

/// <summary>
/// The artist's Pokéwalker: the shell and the three buttons (each normal and pressed), PNGs in
/// <c>Resources/UI/walker/</c> (see <see cref="WalkerDevice"/> for names, sizes and anchors).
/// </summary>
public sealed record WalkerDeviceArt(SKImage Shell, IReadOnlyList<SKImage> Buttons, IReadOnlyList<SKImage> Pressed);

/// <summary>
/// The device's layout, in the shell PNG's own pixels: the artist draws to these, PKForge pastes
/// the LCD into <see cref="Screen"/> and the buttons into <see cref="Buttons"/>, all scaled by the
/// same whole factor. Nothing of the device is drawn by code.
/// </summary>
public static class WalkerDevice
{
    public const int Width = 188;
    public const int Height = 200;

    /// <summary>Where the 96×64 LCD goes in the shell (the shell's window; anything under it is covered).</summary>
    public static readonly SKRectI Screen = SKRectI.Create(46, 68, WalkerLcd.Width, WalkerLcd.Height);

    /// <summary>◀, centre, ▶: where each button PNG (and its pressed twin, same size) is placed.</summary>
    public static readonly SKRectI[] Buttons = [SKRectI.Create(50, 148, 21, 22), SKRectI.Create(82, 154, 25, 26), SKRectI.Create(118, 148, 21, 22)];

    public const string Folder = "ui/walker";
    public const string ShellFile = "shell.png";
    public static readonly string[] ButtonFiles = ["button-left.png", "button-centre.png", "button-right.png"];
    public static readonly string[] PressedFiles = ["button-left-pressed.png", "button-centre-pressed.png", "button-right-pressed.png"];

    /// <summary>Whether a set of images matches the layout: the shell at its size, each button at its rect's.</summary>
    public static bool Fits(SKImage shell, IReadOnlyList<SKImage> buttons, IReadOnlyList<SKImage> pressed) =>
        shell.Width == Width && shell.Height == Height && buttons.Count == 3 && pressed.Count == 3 &&
        Enumerable.Range(0, 3).All(i => buttons[i].Width == Buttons[i].Width && buttons[i].Height == Buttons[i].Height &&
                                        pressed[i].Width == Buttons[i].Width && pressed[i].Height == Buttons[i].Height);
}

/// <summary>
/// The Pokéwalker held in the hand: the artist's shell and buttons (<see cref="WalkerDeviceArt"/>)
/// scaled up by a whole factor with no filtering, its grey-green four-tone LCD (96×64, the device's
/// own size) pasted into the shell's window, showing the course, the walking Pokémon's real sprite in
/// two-bit tones, the steps, and the pages ◀ ▶ turn through. Without the art the LCD stands alone in
/// a plain panel, with ordinary ◀ ● ▶ chips under it, so the feature still works. Design pixels of a
/// 1872 wide screen for the banner and the card; the device itself in canvas pixels.
/// </summary>
public static class WalkerPaint
{
    public static float DesignWidth => UiScale.DesignWidth;

    private const float HeaderHeight = 88f;

    /// <summary>The LCD's four tones, light to dark: the device's grey-green.</summary>
    public static readonly SKColor[] LcdTones = [new(0xC6, 0xD0, 0xA2), new(0x93, 0xA0, 0x6F), new(0x58, 0x64, 0x47), new(0x23, 0x29, 0x1F)];

    public static WalkerScenery SceneryFor(int course) => course switch
    {
        1 or 11 or 15 => WalkerScenery.Forest,
        2 or 10 or 14 => WalkerScenery.Rocky,
        3 or 6 or 9 or 16 or 17 or 18 => WalkerScenery.Water,
        4 or 7 => WalkerScenery.Town,
        5 or 12 or 19 => WalkerScenery.Cave,
        _ => WalkerScenery.Field,
    };

    private static readonly SKSamplingOptions Crisp = new(SKFilterMode.Nearest, SKMipmapMode.None);

    public static WalkerHits Paint(SKCanvas c, SKImageInfo info, WalkerScene scene, SKTypeface face,
        WalkerDeviceArt? art, WalkerLayout layout = WalkerLayout.Side)
    {
        var unit = info.Width / DesignWidth;
        var designHeight = info.Height / unit;
        c.Save();
        c.Scale(unit);
        var header = new SKRect(240f, 0, DesignWidth - 240f, HeaderHeight);
        using (var font = new SKFont(face, 40f)) StoragePaint.BoxHeader(c, header, "POKéWALKER", font, false, false, 1f, chevrons: false);
        c.Restore();

        var top = (HeaderHeight + 24f) * unit;
        var bottom = info.Height - 12f * unit;
        var zone = layout == WalkerLayout.Side ? info.Width * 0.52f : info.Width;
        var area = new SKRect(30f * unit, top, zone - 30f * unit, bottom);

        var (hits, device) = art is null ? PaintBare(c, area, scene, face, unit) : PaintDevice(c, area, scene, art);

        c.Save();
        c.Scale(unit);
        if (layout == WalkerLayout.Side)
            Card(c, new SKRect(info.Width * 0.53f / unit, HeaderHeight + 24f, DesignWidth, designHeight - 12f), scene, face);
        else
            Margins(c, new SKRect(device.Left / unit, device.Top / unit, device.Right / unit, device.Bottom / unit), designHeight, scene, face);
        c.Restore();
        return hits;
    }

    // ---- The device -------------------------------------------------------------------------

    // The artist's shell, as large as whole steps allow, the LCD in its window, the buttons on their anchors.
    private static (WalkerHits, SKRect) PaintDevice(SKCanvas c, SKRect area, WalkerScene scene, WalkerDeviceArt art)
    {
        var device = Fit(art.Shell, area);
        var s = device.Width / art.Shell.Width;
        c.DrawImage(art.Shell, device, Crisp);
        SKRect Place(SKRectI r) => SKRect.Create(device.Left + r.Left * s, device.Top + r.Top * s, r.Width * s, r.Height * s);
        for (var i = 0; i < 3; i++)
            c.DrawImage(scene.Pressed == i ? art.Pressed[i] : art.Buttons[i], Place(WalkerDevice.Buttons[i]), Crisp);
        var screen = Place(WalkerDevice.Screen);
        var targets = PaintLcd(c, screen, (int)s, scene);
        SKRect Hit(int i) => SKRect.Inflate(Place(WalkerDevice.Buttons[i]), 4 * s, 4 * s);
        return (new WalkerHits(Hit(0), Hit(1), Hit(2), screen, targets), device);
    }

    // Without the art: the LCD alone, framed by a plain panel, and three ordinary chips for ◀ ● ▶.
    private static (WalkerHits, SKRect) PaintBare(SKCanvas c, SKRect area, WalkerScene scene, SKTypeface face, float unit)
    {
        var chipHeight = 84f * unit;
        var s = Math.Max(1, (int)MathF.Floor(Math.Min((area.Width - 80f * unit) / WalkerLcd.Width, (area.Height - chipHeight - 110f * unit) / WalkerLcd.Height)));
        var w = WalkerLcd.Width * s;
        var h = WalkerLcd.Height * s;
        var screen = SKRect.Create((int)(area.MidX - w / 2f), (int)(area.Top + (area.Height - h - chipHeight - 40f * unit) / 2f), w, h);
        var panel = new SKRect(screen.Left - 30f * unit, screen.Top - 30f * unit, screen.Right + 30f * unit, screen.Bottom + chipHeight + 70f * unit);
        var radius = 28f * unit;
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(panel, radius, radius, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f * unit })
            c.DrawRoundRect(SKRect.Inflate(panel, -1.5f * unit, -1.5f * unit), radius, radius, rim);
        var targets = PaintLcd(c, screen, s, scene);

        var gap = 24f * unit;
        var chipWidth = (screen.Width - 2 * gap) / 3f;
        var chips = new SKRect[3];
        using var font = new SKFont(face, 32f * unit);
        for (var i = 0; i < 3; i++)
        {
            var chip = SKRect.Create(screen.Left + i * (chipWidth + gap), screen.Bottom + 30f * unit, chipWidth, chipHeight - 10f * unit);
            chips[i] = chip;
            Chip(c, chip, focused: scene.Pressed == i);
            var ink = scene.Pressed == i ? T.ChipInk : T.Value;
            if (i == 1)
                using (var paint = new SKPaint { Color = ink, IsAntialias = false })
                    c.DrawTextFallback("OK", chip.MidX, Baseline(chip.MidY, font), SKTextAlign.Center, font, paint);
            else
            {
                c.Save();
                c.Translate(chip.MidX, chip.MidY);
                c.Scale(unit);
                Arrow(c, 0, 0, i == 0 ? -1 : 1, ink);
                c.Restore();
            }
        }
        return (new WalkerHits(chips[0], chips[1], chips[2], screen, targets), panel);
    }

    // The LCD at a whole scale into its window, with its dot grid; returns the targets in canvas pixels.
    private static List<SKRect> PaintLcd(SKCanvas c, SKRect screen, int s, WalkerScene scene)
    {
        var lcd = new WalkerLcd();
        var targets = Compose(lcd, scene);
        using (var image = lcd.ToImage(LcdTones)) c.DrawImage(image, screen, Crisp);
        // The LCD's dot grid: a hairline between the screen's pixels once they are big enough.
        if (s >= 4)
            using (var grid = new SKPaint { Color = LcdTones[0].WithAlpha(90), IsAntialias = false })
            {
                for (var i = 1; i < WalkerLcd.Width; i++) c.DrawRect(SKRect.Create(screen.Left + i * s, screen.Top, 1, screen.Height), grid);
                for (var j = 1; j < WalkerLcd.Height; j++) c.DrawRect(SKRect.Create(screen.Left, screen.Top + j * s, screen.Width, 1), grid);
            }
        return [.. targets.Select(t => SKRect.Create(screen.Left + t.Left * s, screen.Top + t.Top * s, t.Width * s, t.Height * s))];
    }

    // ---- The screen ---------------------------------------------------------------------------

    /// <summary>Draws the scene's page into <paramref name="lcd"/>, as the device shows it (the home-screen widget reuses it).</summary>
    public static void ComposeLcd(WalkerLcd lcd, WalkerScene scene) => Compose(lcd, scene);

    /// <summary>Draws the page into the LCD; returns the targets (bushes, patches) in LCD pixels.</summary>
    private static List<SKRectI> Compose(WalkerLcd lcd, WalkerScene scene)
    {
        lcd.Clear();
        var page = scene.Pages.Count == 0 ? WalkerPage.Walk : scene.Pages[Math.Clamp(scene.Page, 0, scene.Pages.Count - 1)];
        var targets = new List<SKRectI>();
        if (page == WalkerPage.Walk)
        {
            WalkPage(lcd, scene);
            return targets;
        }

        PageBar(lcd, scene, page);
        switch (page)
        {
            case WalkerPage.Radar:
            {
                (int X, int Y)[] spots = [(10, 20), (64, 20), (10, 41), (64, 41)];
                for (var i = 0; i < spots.Length; i++)
                {
                    var (x, y) = spots[i];
                    var shake = i == scene.Rustle ? (scene.Frame == 0 ? -1 : 1) : 0;
                    lcd.Pattern(WalkerArt.Bush, x + shake, y);
                    targets.Add(SKRectI.Create(x - 4, y - 8, 30, 19));
                    if (i == scene.Rustle)
                        for (var m = 0; m < scene.Marks; m++)
                        {
                            var mx = x + 11 + (m - (scene.Marks - 1) / 2f) * 4;
                            lcd.Fill((int)mx, y - 7, 1, 3, 3);
                            lcd.Dot((int)mx, y - 3, 3);
                        }
                    if (i == scene.Target && scene.Targets > 0 && scene.Rustle < 0) lcd.Pattern(WalkerArt.Cursor, x + 9, y - 5);
                }
                break;
            }
            case WalkerPage.Dowsing:
            {
                for (var i = 0; i < 6; i++)
                {
                    var x = 2 + i * 16;
                    Tuft(lcd, x, 36);
                    targets.Add(SKRectI.Create(x - 2, 22, 16, 24));
                    if (i == scene.Target && scene.Targets > 0) lcd.Pattern(WalkerArt.Cursor, x + 4, 27);
                }
                Center(lcd, scene.Targets > 0 ? "WHERE IS IT?" : $"{scene.Watts}W", 15);
                break;
            }
            case WalkerPage.Bag:
                for (var i = 0; i < 3; i++)
                {
                    var caught = i < scene.Catches.Count;
                    lcd.Pattern(WalkerArt.Ball, 1, 12 + i * 10, only: caught ? null : 1);
                    lcd.Text(caught ? WalkerLcd.Cut(scene.Catches[i], 36) : "-", 10, 13 + i * 10, 3);
                    var found = i < scene.Items.Count;
                    lcd.Pattern(WalkerArt.Bag, 49, 12 + i * 10, only: found ? null : 1);
                    lcd.Text(found ? WalkerLcd.Cut(scene.Items[i], 36) : "-", 58, 13 + i * 10, 3);
                }
                break;
            case WalkerPage.Sync:
                Big(lcd, WalkerArt.Sync, 41, 13);
                break;
            case WalkerPage.Info:
            {
                var notes = scene.Notes ?? [];
                for (var i = 0; i < Math.Min(4, notes.Count); i++) lcd.Text(WalkerLcd.Cut(notes[i], 94), 1, 13 + i * 9, 3);
                break;
            }
            case WalkerPage.Debug:
                Big(lcd, WalkerArt.Plus, 41, 13);
                break;
        }
        Bottom(lcd, scene.Lcd);
        return targets;
    }

    // The home page: the course scenery, the Pokémon walking, the watts, and the steps.
    private static void WalkPage(WalkerLcd lcd, WalkerScene scene)
    {
        lcd.Pattern(WalkerArt.Note, 2, 1);
        var watts = $"{scene.Watts}W";
        lcd.Text(watts, 94 - WalkerLcd.TextWidth(watts), 2, 3);

        Scenery(lcd, scene.Scenery, scene.Frame);
        if (scene.Walker is { } sprite)
        {
            var bob = scene.Frame == 1 ? 1 : 0;
            lcd.Sprite(sprite, 64 - sprite.Width / 2, 51 - sprite.Height - bob);
        }
        else
        {
            Center(lcd, "NO POKEMON", 15);
            Center(lcd, "ON A WALK", 22);
        }

        lcd.HLine(0, 53, 96, 3);
        lcd.Pattern(WalkerArt.Ball, 1, 56);
        lcd.Text(scene.Catches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), 10, 57, 3);
        lcd.Pattern(WalkerArt.Bag, 16, 56);
        lcd.Text(scene.Items.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), 25, 57, 3);
        lcd.BigNumber(scene.Steps, 94, 56, 3);
        if (scene.Lcd.Length > 0 && scene.Walker is null) Center(lcd, scene.Lcd, 31);
    }

    private static void Scenery(WalkerLcd lcd, WalkerScenery kind, int frame)
    {
        // The ground: a strip of grass whose tufts step back as the Pokémon walks.
        if (kind != WalkerScenery.Cave)
        {
            lcd.HLine(0, 51, 96, 2);
            for (var x = (frame * 3) % 8; x < 96; x += 8)
            {
                lcd.Dot(x, 50, 2);
                lcd.Dot(x + 2, 50, 2);
                lcd.Dot(x + 1, 49, 2);
            }
        }
        switch (kind)
        {
            case WalkerScenery.Field:
                lcd.Pattern(WalkerArt.Tree, 1, 39);
                lcd.Pattern(WalkerArt.Tree, 15, 37);
                lcd.Pattern(WalkerArt.Rock, 30, 45);
                break;
            case WalkerScenery.Forest:
                lcd.Pattern(WalkerArt.Pine, 0, 39);
                lcd.Pattern(WalkerArt.Pine, 11, 34);
                lcd.Pattern(WalkerArt.Pine, 23, 39);
                break;
            case WalkerScenery.Rocky:
                lcd.Pattern(WalkerArt.Rock, 2, 45);
                lcd.Pattern(WalkerArt.Rock, 9, 40);
                lcd.Pattern(WalkerArt.Rock, 18, 45);
                lcd.Pattern(WalkerArt.Pine, 28, 39);
                break;
            case WalkerScenery.Water:
                lcd.Fill(0, 44, 38, 7, 1);
                for (var row = 0; row < 2; row++)
                    for (var x = (frame * 2 + row * 3) % 6; x < 38; x += 6)
                    {
                        lcd.Dot(x, 46 + row * 3, 2);
                        lcd.Dot(x + 1, 46 + row * 3, 2);
                    }
                lcd.Pattern(WalkerArt.Tree, 6, 31);
                break;
            case WalkerScenery.Town:
                lcd.Pattern(WalkerArt.House, 1, 42);
                lcd.Pattern(WalkerArt.House, 15, 42);
                lcd.Pattern(WalkerArt.Tree, 29, 39);
                break;
            case WalkerScenery.Cave:
                // A dim cave: a light dither on the rock, stalactites, a stony floor.
                for (var y = 10; y < 51; y += 2)
                    for (var x = (y / 2) % 2; x < 40; x += 4) lcd.Dot(x, y, 1);
                for (var x = 0; x < 96; x += 12) lcd.Pattern(WalkerArt.Stalactite, x, 9);
                lcd.HLine(0, 51, 96, 2);
                for (var x = frame * 2; x < 96; x += 7) lcd.Dot(x, 50, 2);
                lcd.Pattern(WalkerArt.Rock, 4, 45);
                break;
        }
    }

    // A clump of grass for Dowsing: blades of different heights on a line of earth.
    private static void Tuft(WalkerLcd lcd, int x, int y)
    {
        lcd.HLine(x, y + 7, 12, 3);
        lcd.HLine(x + 1, y + 6, 10, 2);
        foreach (var (bx, h) in new[] { (1, 3), (3, 6), (5, 4), (7, 7), (9, 5), (10, 2) })
        {
            lcd.Fill(x + bx, y + 7 - h, 1, h, 2);
            lcd.Dot(x + bx, y + 6 - h, 3);
        }
    }

    // The menu strip: one icon a page, the current one in ink.
    private static void PageBar(WalkerLcd lcd, WalkerScene scene, WalkerPage page)
    {
        var count = scene.Pages.Count;
        var start = 48 - (count * 12 - 3) / 2;
        for (var i = 0; i < count; i++)
        {
            var x = start + i * 12;
            var current = scene.Pages[i] == page;
            if (current) lcd.Fill(x - 1, 0, 9, 9, 3);
            lcd.Pattern(Icon(scene.Pages[i]), x, 1, only: current ? 0 : null);
        }
        lcd.HLine(0, 10, 96, 2);
        // ◀ ▶ marks at the strip's ends.
        lcd.Dot(1, 4, 2); lcd.Fill(2, 3, 1, 3, 2); lcd.Fill(3, 2, 1, 5, 2);
        lcd.Dot(94, 4, 2); lcd.Fill(93, 3, 1, 3, 2); lcd.Fill(92, 2, 1, 5, 2);
    }

    private static string[] Icon(WalkerPage page) => page switch
    {
        WalkerPage.Walk => WalkerArt.Walk,
        WalkerPage.Radar => WalkerArt.Radar,
        WalkerPage.Dowsing => WalkerArt.Dowse,
        WalkerPage.Bag => WalkerArt.Bag,
        WalkerPage.Sync => WalkerArt.Sync,
        WalkerPage.Debug => WalkerArt.Plus,
        _ => WalkerArt.Info,
    };

    private static void Bottom(WalkerLcd lcd, string text)
    {
        lcd.HLine(0, 53, 96, 2);
        Center(lcd, text, 57);
    }

    private static void Center(WalkerLcd lcd, string text, int y)
    {
        var cut = WalkerLcd.Cut(text, 94);
        lcd.Text(cut, 48 - WalkerLcd.TextWidth(cut) / 2, y, 3);
    }

    // An icon at twice its size.
    private static void Big(WalkerLcd lcd, string[] icon, int x, int y)
    {
        for (var j = 0; j < icon.Length; j++)
            for (var i = 0; i < icon[j].Length; i++)
                if (icon[j][i] is >= '0' and <= '3') lcd.Fill(x + i * 2, y + j * 2, 2, 2, icon[j][i] - '0');
    }

    // ---- Beside the device --------------------------------------------------------------------

    // The card at the right: the course on a tab, a few facts, and what the focused page does.
    private static void Card(SKCanvas c, SKRect card, WalkerScene scene, SKTypeface face)
    {
        const float radius = 28f;
        using (var fill = new SKPaint { Color = T.PanelFill, IsAntialias = true }) c.DrawRoundRect(card, radius, radius, fill);
        using (var rim = new SKPaint { Color = T.Rim, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3f })
            c.DrawRoundRect(SKRect.Inflate(card, -1.5f, -1.5f), radius, radius, rim);
        using var big = new SKFont(face, 44f);
        using var mid = new SKFont(face, 32f);
        using var small = new SKFont(face, 28f);

        var tab = new SKRect(card.Left + 36f, card.Top + 30f, card.Right - 60f, card.Top + 96f);
        using (var path = Tab(tab, 0f, 30f))
        {
            using (var band = new SKPaint { Color = EditorPaint.RowBand(true), IsAntialias = true }) c.DrawPath(path, band);
            Outline(c, path, T.Rim.WithAlpha(120), 3f);
        }
        Text(c, scene.Course, tab.Left + 26f, tab.MidY, big, T.Value, tab.Width - 70f);

        var y = tab.Bottom + 26f;
        foreach (var (label, value) in scene.Facts)
        {
            var row = new SKRect(card.Left + 36f, y, card.Right - 36f, y + 64f);
            using (var band = new SKPaint { Color = EditorPaint.RowBand(false), IsAntialias = true }) c.DrawRoundRect(row, 10f, 10f, band);
            Text(c, label, row.Left + 26f, row.MidY, mid, T.SubInk, row.Width * 0.45f);
            Right(c, Fitted(value, mid, row.Width * 0.5f), row.Right - 26f, row.MidY, mid, T.Value);
            y = row.Bottom + 10f;
        }

        y += 24f;
        if (scene.Headline.Length > 0)
        {
            Text(c, scene.Headline, card.Left + 40f, y + 20f, mid, T.Value, card.Width - 80f);
            y += 54f;
        }
        foreach (var line in Wrap(scene.Status, small, card.Width - 80f).Take(Math.Max(0, (int)((card.Bottom - 30f - y) / 38f))))
        {
            Text(c, line, card.Left + 40f, y + 16f, small, T.SubInk, card.Width - 80f);
            y += 38f;
        }
    }

    // With the device in the middle: the course and facts at its left, what the page does at its right.
    private static void Margins(SKCanvas c, SKRect device, float height, WalkerScene scene, SKTypeface face)
    {
        using var big = new SKFont(face, 40f);
        using var mid = new SKFont(face, 30f);
        using var small = new SKFont(face, 26f);
        var leftWidth = device.Left - 60f;
        if (leftWidth > 160f)
        {
            var y = device.Top + 60f;
            Text(c, scene.Course, 30f, y, big, T.Value, leftWidth);
            y += 56f;
            foreach (var (label, value) in scene.Facts)
            {
                Text(c, label, 30f, y, small, T.SubInk, leftWidth);
                Text(c, value, 30f, y + 32f, mid, T.Value, leftWidth);
                y += 84f;
            }
        }
        var rightLeft = device.Right + 30f;
        var rightWidth = DesignWidth - rightLeft - 30f;
        if (rightWidth > 160f)
        {
            var y = device.Top + 60f;
            if (scene.Headline.Length > 0) { Text(c, scene.Headline, rightLeft, y, big, T.Value, rightWidth); y += 56f; }
            foreach (var line in Wrap(scene.Status, small, rightWidth).Take(14))
            {
                Text(c, line, rightLeft, y, small, T.SubInk, rightWidth);
                y += 36f;
            }
        }
    }
}
