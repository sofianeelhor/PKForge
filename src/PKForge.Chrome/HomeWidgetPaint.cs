using PKForge.Domain;
using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The three home-screen widgets beside the Poképark (the Pocket Pokéwalker, the partner, the egg),
/// each one painted picture per animation frame, in the Poképark widget's way: the scene is drawn at
/// low resolution, enlarged by a whole step with nearest sampling so every pixel stays square, and
/// only the words are drawn afterwards at full size. Flat colours only: no gradient, glow or blur.
/// The launcher flips between the frames (a ViewFlipper), so nothing here ever runs a loop.
/// </summary>
public static class HomeWidgetPaint
{
    // The family the Poképark widget set: a sky-cyan ground, cyan panels with a one-pixel rim,
    // a navy slanted title tab edged in cyan, dark ink.
    private static readonly SKColor Backdrop = new(0xa8, 0xe0, 0xf8), PanelFill = new(0xe0, 0xf8, 0xf8), PanelRim = new(0x38, 0x98, 0xb0);
    private static readonly SKColor PanelText = new(0x20, 0x40, 0x50), SubText = new(0x48, 0x70, 0x80);
    private static readonly SKColor TabFill = new(0x28, 0x38, 0x68), TabRim = new(0x58, 0xd0, 0xe8), Ink = new(0x18, 0x20, 0x30);
    private static readonly SKColor Alert = new(0xe8, 0x50, 0x40), AlertInk = new(0xff, 0xf8, 0xe0);
    /// <summary>The widgets' own background (home_widget_background): what surrounds a layout smaller than its tile.</summary>
    public static SKColor Ground => Backdrop;

    /// <summary>The mark a widget wears before it has read anything: no claim about a walk, a partner or an egg.</summary>
    public const string Mark = "PKForge";

    private static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    private static string Num(long value) => EggWidget.Num(value);

    private static string Steps(long value) => value == 1 ? "1 step" : $"{Num(value)} steps";

    /// <summary>The scene height each widget is drawn at before it is enlarged, by size.</summary>
    private static int Target(HomeWidgetKind kind, HomeWidgetSizeHint size) => (kind, size) switch
    {
        (HomeWidgetKind.Walker, HomeWidgetSizeHint.Small) => 118,
        (HomeWidgetKind.Walker, HomeWidgetSizeHint.Wide) => 108,
        (HomeWidgetKind.Walker, _) => 252,
        (_, HomeWidgetSizeHint.Small) => 76,
        (_, HomeWidgetSizeHint.Wide) => 92,
        _ => 124,
    };

    /// <summary>
    /// The pixel size at which a widget paints at a step of one (its scene's own size): what the
    /// Widgets menu renders and then enlarges by whole steps. Null <paramref name="kind"/>: the Poképark widget.
    /// </summary>
    public static (int Width, int Height) NaturalSize(HomeWidgetKind? kind, HomeWidgetSizeHint size)
    {
        var (w, h) = HomeWidgetSizes.Dp(size);
        var target = kind is { } k ? Target(k, size) : size switch { HomeWidgetSizeHint.Small => 80, HomeWidgetSizeHint.Wide => 90, _ => 150 };
        if (kind == HomeWidgetKind.Walker && size == HomeWidgetSizeHint.Small) target = 112;
        return ((int)Math.Round(target * (double)w / h), target);
    }

    /// <summary>The whole step that brings a widget of that pixel size to a scene of about the wanted height.</summary>
    public static int StepFor(HomeWidgetKind kind, HomeWidgetSizeHint size, int widthPx, int heightPx)
    {
        var target = Target(kind, size);
        var fit = Math.Min(heightPx, kind == HomeWidgetKind.Walker && size == HomeWidgetSizeHint.Small ? widthPx * 1.0 : widthPx * 1.5);
        // The Pokéwalker's LCD is 96 pixels wide and must fit with its bezel.
        var step = Math.Clamp((int)Math.Round(fit / target), 1, 8);
        if (kind == HomeWidgetKind.Walker)
            while (step > 1 && (widthPx / step < 98 || heightPx / step < 80)) step--;
        return step;
    }

    // ── Shared pieces ────────────────────────────────────────────────────────────────────────

    private sealed class Words
    {
        public readonly List<(string Text, float X, float Y, SKColor Color, float Max, bool Right)> Items = [];
        public void Add(string text, float x, float y, SKColor color, float max, bool right = false) => Items.Add((text, x, y, color, max, right));
    }

    private static SKBitmap Finish(SKBitmap scene, int step, Words words, SKTypeface face)
    {
        var output = new SKBitmap(scene.Width * step, scene.Height * step, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        using (var image = SKImage.FromBitmap(scene))
            canvas.DrawImage(image, SKRect.Create(0, 0, output.Width, output.Height), Nearest, null);
        using var font = new SKFont(face, 7 * step) { Edging = SKFontEdging.Alias, Hinting = SKFontHinting.None };
        using var paint = new SKPaint { IsAntialias = false };
        foreach (var (text, x, y, color, max, right) in words.Items)
        {
            paint.Color = color;
            var fitted = text;
            var room = max * step;
            while (fitted.Length > 2 && font.MeasureFallback(fitted) > room) fitted = fitted[..^2].TrimEnd() + "…";
            canvas.DrawTextFallback(fitted, x * step, y * step, right ? SKTextAlign.Right : SKTextAlign.Left, font, paint);
        }
        return output;
    }

    private static void Rect(SKCanvas c, SKPaint p, SKColor color, int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        p.Color = color;
        c.DrawRect(x, y, w, h, p);
    }

    /// <summary>A filled pixel ellipse, row by row.</summary>
    private static void Ellipse(SKCanvas c, SKPaint p, SKColor color, int cx, int cy, int rx, int ry)
    {
        for (var dy = -ry; dy <= ry; dy++)
        {
            var half = (int)Math.Round(rx * Math.Sqrt(Math.Max(0, 1 - dy * dy / (double)(ry * ry))));
            Rect(c, p, color, cx - half, cy + dy, half * 2 + 1, 1);
        }
    }

    /// <summary>The navy slanted title tab with its cyan edge; returns its width.</summary>
    private static int TitleTab(SKCanvas c, SKPaint p, Words words, string title, int x, int y, int maxWidth)
    {
        var width = Math.Min(maxWidth, title.Length * 5 + 10);
        for (var row = 0; row < 11; row++)
        {
            var right = x + width - row / 2;
            Rect(c, p, row == 10 ? TabRim : TabFill, x, y + row, right - x, 1);
            Rect(c, p, TabRim, right, y + row, 1, 1);
        }
        words.Add(title, x + 3, y + 8.5f, SKColors.White, width - 8);
        return width;
    }

    private static void Panel(SKCanvas c, SKPaint p, int x, int y, int w, int h)
    {
        Rect(c, p, PanelRim, x, y, w, h);
        Rect(c, p, PanelFill, x + 1, y + 1, w - 2, h - 2);
    }

    /// <summary>A flat two-band sky for the hour, stars at night, the hills and the grass with its tufts.</summary>
    private static void Outdoors(SKCanvas c, SKPaint p, int x, int y, int w, int h, int horizon, ParkDayPhase phase, int seed)
    {
        var pal = ParkWidgetPaint.For(phase, ParkWeather.Sunny);
        var band = horizon / 2;
        Rect(c, p, pal.SkyTop, x, y, w, band);
        Rect(c, p, pal.SkyLow, x, y + band, w, horizon - band);
        for (var i = 0; i < w; i += 2) Rect(c, p, pal.SkyTop, x + i, y + band, 1, 1);
        if (phase == ParkDayPhase.Night)
        {
            var rng = new Random(seed);
            for (var i = 0; i < w * horizon / 80; i++)
                Rect(c, p, i % 4 == 0 ? new SKColor(0xff, 0xf0, 0x90) : new SKColor(0xd8, 0xe0, 0xff), x + rng.Next(w), y + rng.Next(Math.Max(1, horizon - 6)), 1, 1);
        }
        for (var i = 0; i < w; i++)
        {
            var far = (int)(3 + 2 * Math.Sin((i + seed) / 8.0) + 1.5 * Math.Sin(i / 3.7 + 1));
            Rect(c, p, pal.Far, x + i, y + horizon - far, 1, far);
        }
        Rect(c, p, pal.Grass, x, y + horizon, w, h - horizon);
        var r = new Random(seed + 5);
        for (var i = 0; i < w * (h - horizon) / 45; i++)
        {
            int tx = x + r.Next(w), ty = y + horizon + 2 + r.Next(Math.Max(1, h - horizon - 3));
            Rect(c, p, pal.Tuft, tx, ty, 1, 1); Rect(c, p, pal.Tuft, tx + 2, ty, 1, 1); Rect(c, p, pal.Tuft, tx + 1, ty - 1, 1, 1);
        }
    }

    private static void Veil(SKCanvas c, SKPaint p, int x, int y, int w, int h) => Rect(c, p, new SKColor(0x10, 0x18, 0x40, 0x50), x, y, w, h);

    /// <summary>A "!" chip: red, cream ink, one pixel taller on the second frame.</summary>
    private static void Bang(SKCanvas c, SKPaint p, int x, int y, int frame)
    {
        y -= frame % 2;
        Rect(c, p, Ink, x, y, 7, 11);
        Rect(c, p, Alert, x + 1, y + 1, 5, 9);
        Rect(c, p, AlertInk, x + 3, y + 2, 1, 5);
        Rect(c, p, AlertInk, x + 3, y + 8, 1, 1);
    }

    private static void Lines(Words words, IReadOnlyList<(string Text, SKColor Color)> lines, int x, int y, int width, int maxLines)
    {
        for (var i = 0; i < Math.Min(maxLines, lines.Count); i++)
            words.Add(lines[i].Text, x, y + 8 + i * 9, lines[i].Color, width);
    }

    // ── The Pocket Pokéwalker ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The Pokéwalker in the pocket: the artist's ball-shaped shell (<paramref name="shell"/>; a plain
    /// bezel when it is missing), its four-tone LCD on the walk page with the walking Pokémon stepping
    /// on <paramref name="frame"/> 0 and 1, the big number (steps today or this walk), the watts, and a
    /// red "!" when a Radar or Dowsing can be afforded. Wider sizes add a panel of facts.
    /// </summary>
    public static SKBitmap RenderWalker(WalkerWidgetView v, HomeWidgetSizeHint size, int widthPx, int heightPx, int frame,
        WalkerSprite? sprite, SKImage? shell, bool bigToday, bool alert, SKTypeface face)
    {
        var step = StepFor(HomeWidgetKind.Walker, size, widthPx, heightPx);
        int w = Math.Max(98, (widthPx + step - 1) / step), h = Math.Max(80, (heightPx + step - 1) / step);
        using var scene = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        var words = new Words();
        using (var c = new SKCanvas(scene))
        using (var p = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill })
        {
            Rect(c, p, Backdrop, 0, 0, w, h);
            using var lcd = Lcd(v, frame, sprite, bigToday);
            var showBang = alert && v.Alert;
            var big = bigToday ? v.TodaySteps : v.WalkSteps;
            var facts = new List<(string, SKColor)>
            {
                (v.Walking ? $"{v.Name} · {v.CourseName}" : "Nobody on a walk", PanelText),
                ($"Today {Steps(v.TodaySteps)}", PanelText),
            };
            if (v.Walking) facts.Add(($"This walk {Steps(v.WalkSteps)}", PanelText));
            facts.Add(($"{Num(v.Watts)} watts", PanelText));
            if (showBang) facts.Add((v.CanRadar ? "! Poké Radar ready" : "! Dowsing ready", Alert));
            else if (!v.Walking) facts.Add(("Tap to send a Pokémon", SubText));

            switch (size)
            {
                case HomeWidgetSizeHint.Small:
                {
                    const int strip = 13;
                    var lx = (w - WalkerLcd.Width) / 2;
                    var ly = Math.Max(3, (h - strip - WalkerLcd.Height) / 2 + 2);
                    Device(c, p, shell, lcd, lx, ly);
                    Panel(c, p, 0, h - strip, w, strip);
                    Rect(c, p, Ink, 0, h - 1, w, 1);
                    words.Add(v.Walking ? $"{Num(big)} {(bigToday ? "today" : "steps")}" : "No walk", 3, h - 3.5f, PanelText, w * 0.62f);
                    words.Add($"{Num(v.Watts)}W", w - 3, h - 3.5f, PanelText, w * 0.35f, right: true);
                    if (showBang) Bang(c, p, lx + WalkerLcd.Width - 8, Math.Max(1, ly - 13), frame);
                    break;
                }
                case HomeWidgetSizeHint.Wide:
                {
                    var region = Math.Clamp(h, 104, w / 2);
                    var lx = (region - WalkerLcd.Width) / 2;
                    var ly = Math.Max(3, (h - WalkerLcd.Height) / 2);
                    Device(c, p, shell, lcd, lx, ly);
                    var px = region + 2;
                    Panel(c, p, px, 2, w - px - 2, h - 4);
                    TitleTab(c, p, words, "Pokéwalker", px + 1, 3, w - px - 4);
                    Lines(words, facts, px + 4, 16, w - px - 10, (h - 20) / 9);
                    if (showBang) Bang(c, p, w - 12, 4, frame);
                    break;
                }
                default:
                {
                    var lines = Math.Min(facts.Count, 4);
                    var panel = lines * 9 + 6;
                    var top = h - panel - 2;
                    var lx = (w - WalkerLcd.Width) / 2;
                    // The whole shell when it fits above the panel; otherwise its middle, as the small size shows it.
                    var ly = shell is not null && top >= WalkerDevice.Height + 2 ? (top - WalkerDevice.Height) / 2 + WalkerDevice.Screen.Top
                        : Math.Max(3, (top - WalkerLcd.Height) / 2);
                    Device(c, p, shell, lcd, lx, ly);
                    Panel(c, p, 2, top, w - 4, panel);
                    Lines(words, facts, 6, top + 1, w - 14, lines);
                    TitleTab(c, p, words, "Pokéwalker", 0, 0, w / 2);
                    if (showBang) Bang(c, p, lx + WalkerLcd.Width - 8, Math.Max(13, ly - 14), frame);
                    break;
                }
            }
        }
        return Finish(scene, step, words, face);
    }

    /// <summary>The walk page of the device's LCD, as an image of 96×64.</summary>
    private static SKImage Lcd(WalkerWidgetView v, int frame, WalkerSprite? sprite, bool bigToday)
    {
        var lcd = new WalkerLcd();
        var scene = new WalkerScene([WalkerPage.Walk], 0, v.Walking ? sprite : null, frame % 2,
            v.Walking ? WalkerPaint.SceneryFor(v.Course) : WalkerScenery.Field,
            bigToday ? v.TodaySteps : v.WalkSteps, v.Watts,
            [.. Enumerable.Repeat("", v.Catches)], [.. Enumerable.Repeat("", v.Items)],
            0, -1, -1, 0, v.Walking ? "" : "TAP TO START", "", [], "");
        WalkerPaint.ComposeLcd(lcd, scene);
        return lcd.ToImage(WalkerPaint.LcdTones);
    }

    /// <summary>The shell placed so its window falls on the LCD at (lx, ly), cut by the scene's edges.</summary>
    private static void Device(SKCanvas c, SKPaint p, SKImage? shell, SKImage lcd, int lx, int ly)
    {
        if (shell is not null)
            c.DrawImage(shell, SKRect.Create(lx - WalkerDevice.Screen.Left, ly - WalkerDevice.Screen.Top, shell.Width, shell.Height), Nearest);
        else
        {
            Rect(c, p, Ink, lx - 4, ly - 4, WalkerLcd.Width + 8, WalkerLcd.Height + 8);
            Rect(c, p, new SKColor(0x40, 0x45, 0x52), lx - 3, ly - 3, WalkerLcd.Width + 6, WalkerLcd.Height + 6);
        }
        c.DrawImage(lcd, SKRect.Create(lx, ly, WalkerLcd.Width, WalkerLcd.Height), Nearest);
    }

    // ── The partner ─────────────────────────────────────────────────────────────────────────

    /// <summary>A short word for the small size.</summary>
    public static string MoodWord(PartnerMood mood) => mood switch
    {
        PartnerMood.Asleep => "Asleep",
        PartnerMood.Tired => "Tired",
        PartnerMood.Sleepy => "Drowsy",
        PartnerMood.Happy => "Lively!",
        _ => "Happy",
    };

    /// <summary>
    /// The partner on a pixel battle base under the sky of the hour: it bobs on the second frame when
    /// awake, sleeps under the night sky with Z's rising, and sweats with a low battery. With no
    /// partner, an empty base holds a Poké Ball.
    /// </summary>
    public static SKBitmap RenderPartner(PartnerChoice? partner, PartnerMood mood, int? battery, SKBitmap? sprite, DateTime local,
        HomeWidgetSizeHint size, int widthPx, int heightPx, int frame, SKTypeface face)
    {
        var step = StepFor(HomeWidgetKind.Partner, size, widthPx, heightPx);
        if (sprite is not null)
        {
            // A big sprite gets a bigger scene, so it always stands whole at its own size on the stage.
            var below = size switch { HomeWidgetSizeHint.Small => 13, HomeWidgetSizeHint.Wide => 0, _ => 33 };
            var needed = (int)Math.Ceiling(sprite.Height / 0.62) + below;
            while (step > 1 && heightPx / step < needed) step--;
        }
        int w = Math.Max(48, (widthPx + step - 1) / step), h = Math.Max(48, (heightPx + step - 1) / step);
        using var scene = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        var words = new Words();
        using (var c = new SKCanvas(scene))
        using (var p = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill })
        {
            var phase = mood == PartnerMood.Asleep ? ParkDayPhase.Night : ParkCare.PhaseAt(local);
            var name = partner is null ? "Partner" : partner.Name;
            var line = partner is null ? PartnerWidget.EmptyLine : PartnerWidget.Line(partner, mood, battery);
            var origin = partner is null ? "" : partner.Level > 0 ? $"Lv. {partner.Level} · {partner.Origin}" : partner.Origin;
            switch (size)
            {
                case HomeWidgetSizeHint.Small:
                {
                    const int strip = 13;
                    Stage(c, p, sprite, partner is null, mood, phase, 0, 0, w, h - strip, frame);
                    Panel(c, p, 0, h - strip, w, strip);
                    Rect(c, p, Ink, 0, h - 1, w, 1);
                    TitleTab(c, p, words, name, 0, 0, w - 4);
                    words.Add(partner is null ? "Pick a partner" : MoodWord(mood), 3, h - 3.5f, PanelText, w - 6);
                    break;
                }
                case HomeWidgetSizeHint.Wide:
                {
                    var region = Math.Min(w / 2, Math.Max(Math.Max(h, 60), w * 2 / 5));
                    Stage(c, p, sprite, partner is null, mood, phase, 0, 0, region, h, frame);
                    var px = region;
                    Panel(c, p, px, 0, w - px, h);
                    Rect(c, p, Ink, 0, h - 1, w, 1);
                    TitleTab(c, p, words, name, px + 1, 1, w - px - 4);
                    var lines = Wrap(line, (w - px - 10) / 5).Select(l => (l, PanelText)).ToList();
                    if (origin.Length > 0) lines.Insert(0, (origin, SubText));
                    Lines(words, lines, px + 4, 14, w - px - 10, (h - 16) / 9);
                    break;
                }
                default:
                {
                    var lines = Wrap(line, (w - 14) / 5).Select(l => (l, PanelText)).ToList();
                    if (origin.Length > 0) lines.Insert(0, (origin, SubText));
                    var count = Math.Min(lines.Count, 3);
                    var panel = count * 9 + 6;
                    Stage(c, p, sprite, partner is null, mood, phase, 0, 0, w, h - panel, frame);
                    Panel(c, p, 0, h - panel, w, panel);
                    Rect(c, p, Ink, 0, h - 1, w, 1);
                    TitleTab(c, p, words, name, 0, 0, w - 4);
                    Lines(words, lines, 4, h - panel + 1, w - 10, count);
                    break;
                }
            }
            if (mood == PartnerMood.Tired && battery is { } level) Battery(c, p, w - 13, 2, level);
        }
        return Finish(scene, step, words, face);
    }

    /// <summary>Breaks a sentence into lines of about <paramref name="chars"/> characters.</summary>
    private static List<string> Wrap(string text, int chars)
    {
        chars = Math.Max(6, chars);
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > chars) { lines.Add(line); line = word; }
            else line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    /// <summary>The sky, the grass, the battle base and the partner on it, inside one region of the scene.</summary>
    private static void Stage(SKCanvas c, SKPaint p, SKBitmap? sprite, bool empty, PartnerMood mood, ParkDayPhase phase,
        int x, int y, int w, int h, int frame, bool bare = false)
    {
        var horizon = h * 11 / 20;
        Outdoors(c, p, x, y, w, h, horizon, phase, w * 13 + h);
        var pal = ParkWidgetPaint.For(phase, ParkWeather.Sunny);
        var cx = x + w / 2;
        var feet = y + horizon + (h - horizon) * 3 / 5;
        var spriteWidth = sprite is null ? 24 : sprite.Width * Math.Clamp((int)(h * 0.5f / Math.Max(1, sprite.Height)), 1, 3);
        var rx = Math.Clamp(spriteWidth / 2 + 8, 16, Math.Max(16, w / 2 - 4));
        var ry = Math.Max(4, rx / 4);
        // The base: a darker rim, a lighter top, a highlight row, like the games' grass platforms.
        var rim = Mix(pal.Tuft, Ink, .35f);
        Ellipse(c, p, rim, cx, feet + 1, rx + 1, ry + 1);
        Ellipse(c, p, pal.Tuft, cx, feet + 1, rx, ry);
        Ellipse(c, p, Mix(pal.Grass, SKColors.White, .3f), cx, feet, rx - 2, Math.Max(2, ry - 2));
        Rect(c, p, Mix(pal.Grass, SKColors.White, .55f), cx - rx / 2, feet - ry + 2, rx / 2, 1);

        if (bare) { }
        else if (empty || sprite is null)
        {
            Ball(c, p, cx - 6, feet - 12 - (frame % 2));
        }
        else
        {
            var bob = mood switch
            {
                PartnerMood.Happy => frame % 2 * 2,
                PartnerMood.Awake or PartnerMood.Sleepy => frame % 2,
                _ => 0,
            };
            var sink = mood == PartnerMood.Asleep ? 1 : 0;
            // Whole steps only: as big as about half the stage's height allows.
            var k = Math.Clamp((int)(h * 0.5f / Math.Max(1, sprite.Height)), 1, 3);
            int sw = sprite.Width * k, sh = sprite.Height * k;
            var top = feet + 1 - sh - bob * k + sink * k;
            using (var image = SKImage.FromBitmap(sprite))
                c.DrawImage(image, SKRect.Create(cx - sw / 2, top, sw, sh), Nearest);
            var head = Math.Max(y + 2, top + 2);
            var side = Math.Min(x + w - 10, cx + sw / 2 - 2);
            switch (mood)
            {
                case PartnerMood.Asleep:
                    Zed(c, p, side, head + 6 - frame % 2 * 3, frame % 2 == 0 ? 3 : 5);
                    if (frame % 2 == 1) Zed(c, p, side + 6, head - 2, 3);
                    break;
                case PartnerMood.Sleepy:
                    if (frame % 2 == 1) Zed(c, p, side, head + 4, 3);
                    break;
                case PartnerMood.Tired:
                    Drop(c, p, side, head + 2 + frame % 2 * 2);
                    break;
                case PartnerMood.Happy:
                    if (frame % 2 == 0) { Sparkle(c, p, side + 2, head); Sparkle(c, p, cx - sw / 2 - 2, head + 6); }
                    break;
            }
        }
        if (phase == ParkDayPhase.Night) Veil(c, p, x, y + horizon - 6, w, h - horizon + 6);
    }

    private static SKColor Mix(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t), (byte)(a.Green + (b.Green - a.Green) * t), (byte)(a.Blue + (b.Blue - a.Blue) * t));

    private static readonly string[] BallArt =
    [
        "....3333....", "..33111133..", ".3111111113.", ".3111111113.", "311111111113", "333333333333",
        "333330033333", "322223322223", ".3222222223.", ".3222222223.", "..33222233..", "....3333....",
    ];

    private static void Ball(SKCanvas c, SKPaint p, int x, int y)
    {
        SKColor[] tones = [SKColors.White, new(0xe8, 0x40, 0x38), new(0xf0, 0xf0, 0xf0), Ink];
        for (var j = 0; j < BallArt.Length; j++)
            for (var i = 0; i < BallArt[j].Length; i++)
                if (BallArt[j][i] is >= '0' and <= '3') Rect(c, p, tones[BallArt[j][i] - '0'], x + i, y + j, 1, 1);
    }

    /// <summary>A pixel "Z" of <paramref name="size"/> (3 or 5), white with an ink shadow.</summary>
    private static void Zed(SKCanvas c, SKPaint p, int x, int y, int size)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            var color = pass == 0 ? Ink : SKColors.White;
            var o = pass == 0 ? 1 : 0;
            Rect(c, p, color, x + o, y + o, size, 1);
            for (var i = 1; i < size - 1; i++) Rect(c, p, color, x + o + size - 1 - i, y + o + i, 1, 1);
            Rect(c, p, color, x + o, y + o + size - 1, size, 1);
        }
    }

    private static void Drop(SKCanvas c, SKPaint p, int x, int y)
    {
        var blue = new SKColor(0x88, 0xc8, 0xf8);
        Rect(c, p, Ink, x + 1, y - 1, 1, 1);
        Rect(c, p, Ink, x, y, 3, 3);
        Rect(c, p, blue, x + 1, y, 1, 1);
        Rect(c, p, blue, x + 1, y + 1, 1, 1);
    }

    private static void Sparkle(SKCanvas c, SKPaint p, int x, int y)
    {
        var gold = new SKColor(0xff, 0xf0, 0x80);
        Rect(c, p, gold, x, y - 1, 1, 3);
        Rect(c, p, gold, x - 1, y, 3, 1);
    }

    /// <summary>A small battery with its level in red, for a tired partner.</summary>
    private static void Battery(SKCanvas c, SKPaint p, int x, int y, int level)
    {
        Rect(c, p, Ink, x, y, 10, 6);
        Rect(c, p, Ink, x + 10, y + 2, 1, 2);
        Rect(c, p, SKColors.White, x + 1, y + 1, 8, 4);
        Rect(c, p, Alert, x + 1, y + 1, Math.Clamp(level * 8 / 100, 1, 8), 4);
    }

    // ── The egg ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The artist's egg in a straw nest by the Day Care fence, wobbling more as it nears hatching
    /// (<see cref="EggWidget.Motion"/> picks the frame), cracked when it is ready, with the cycles or
    /// steps left and a row of pips that fills up. With no egg, the nest is empty.
    /// </summary>
    public static SKBitmap RenderEgg(EggWidgetView v, DateTime local, HomeWidgetSizeHint size, int widthPx, int heightPx, int frame, SKTypeface face)
    {
        var step = StepFor(HomeWidgetKind.Egg, size, widthPx, heightPx);
        int w = Math.Max(48, (widthPx + step - 1) / step), h = Math.Max(48, (heightPx + step - 1) / step);
        using var scene = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        var words = new Words();
        using (var c = new SKCanvas(scene))
        using (var p = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill })
        {
            var phase = ParkCare.PhaseAt(local);
            var lead = v.Stage == EggStage.Ready || v.Stage == EggStage.Waiting ? Alert : PanelText;
            // The words wrapped to the panel's width; the save's name last, on one line.
            List<(string, SKColor)> Wrapped(int width)
            {
                var chars = (width - 10) / 5;
                var list = Wrap(v.Line, chars).Select(l => (l, lead)).Concat(Wrap(v.Detail, chars).Select(l => (l, SubText))).ToList();
                if (v.SaveName.Length > 0) list.Add((v.SaveName, SubText));
                return list;
            }
            var lines = Wrapped(w);
            switch (size)
            {
                case HomeWidgetSizeHint.Small:
                {
                    const int strip = 13;
                    Nest(c, p, v, phase, 0, 0, w, h - strip, frame, 3);
                    Panel(c, p, 0, h - strip, w, strip);
                    Rect(c, p, Ink, 0, h - 1, w, 1);
                    TitleTab(c, p, words, v.Title, 0, 0, w - 4);
                    words.Add(v.Line, 3, h - 3.5f, lines[0].Item2, w - 6);
                    break;
                }
                case HomeWidgetSizeHint.Wide:
                {
                    var region = Math.Min(w / 2, Math.Max(Math.Max(h, 60), w * 2 / 5));
                    Nest(c, p, v, phase, 0, 0, region, h, frame, 3);
                    var px = region;
                    Panel(c, p, px, 0, w - px, h);
                    Rect(c, p, Ink, 0, h - 1, w, 1);
                    TitleTab(c, p, words, v.Title, px + 1, 1, w - px - 4);
                    Lines(words, Wrapped(w - px), px + 4, 14, w - px - 10, (h - 26) / 9);
                    if (v.Stage is not (EggStage.None or EggStage.Waiting)) Pips(c, p, px + 5, h - 9, Math.Min(10, (w - px - 12) / 5), v.Progress);
                    break;
                }
                default:
                {
                    var panel = Math.Min(4, lines.Count) * 9 + 14;
                    Nest(c, p, v, phase, 0, 0, w, h - panel, frame, 4);
                    Panel(c, p, 0, h - panel, w, panel);
                    Rect(c, p, Ink, 0, h - 1, w, 1);
                    TitleTab(c, p, words, v.Title, 0, 0, w - 4);
                    Lines(words, lines, 4, h - panel + 1, w - 10, 4);
                    if (v.Stage is not (EggStage.None or EggStage.Waiting)) Pips(c, p, 5, h - 9, Math.Min(10, (w - 12) / 5), v.Progress);
                    break;
                }
            }
        }
        return Finish(scene, step, words, face);
    }

    /// <summary>The fence, the grass, the nest and the egg (scaled by <paramref name="scale"/> when it fits).</summary>
    private static void Nest(SKCanvas c, SKPaint p, EggWidgetView v, ParkDayPhase phase, int x, int y, int w, int h, int frame, int scale)
    {
        var horizon = h * 9 / 20;
        Outdoors(c, p, x, y, w, h, horizon, phase, w * 7 + h);
        // The Day Care fence along the horizon: posts and two rails, in wood.
        var wood = new SKColor(0xc8, 0x88, 0x48);
        var woodDark = new SKColor(0x80, 0x50, 0x28);
        Rect(c, p, wood, x, y + horizon - 6, w, 1);
        Rect(c, p, wood, x, y + horizon - 3, w, 1);
        for (var px = x + 3; px < x + w; px += 9)
        {
            Rect(c, p, woodDark, px, y + horizon - 9, 2, 10);
            Rect(c, p, wood, px, y + horizon - 9, 1, 9);
        }

        if (v.Stage == EggStage.None && v.Title.Length == 0)
        {
            // The neutral face: the Day Care's fence and grass, no nest that would say "no egg".
            if (phase == ParkDayPhase.Night) Veil(c, p, x, y + horizon - 10, w, h - horizon + 10);
            return;
        }
        while (scale > 1 && EggSprite.Height * scale + 16 > h - horizon + 14) scale--;
        var cx = x + w / 2;
        var bottom = y + h - Math.Max(4, (h - horizon) / 5);
        var rx = Math.Max(10, EggSprite.Width * scale / 2 + 7);
        var ry = Math.Max(3, rx / 3);
        // The nest: dark straw under, the bowl, light straws across.
        var straw = new SKColor(0xd8, 0xa8, 0x58);
        var strawDark = new SKColor(0x98, 0x68, 0x30);
        Ellipse(c, p, Ink, cx, bottom, rx + 1, ry + 1);
        Ellipse(c, p, strawDark, cx, bottom, rx, ry);
        Ellipse(c, p, new SKColor(0x68, 0x40, 0x20), cx, bottom - 1, rx - 3, Math.Max(1, ry - 2));

        if (v.Stage != EggStage.None)
        {
            var (frames, _) = EggWidget.Motion(v.Stage);
            var name = frames[frame % frames.Length];
            if (EggSprite.Get(name) is { } egg)
            {
                var ew = EggSprite.Width * scale;
                var eh = EggSprite.Height * scale;
                c.DrawImage(egg, SKRect.Create(cx - ew / 2, bottom - eh + ry / 2, ew, eh), Nearest);
                if (v.Stage == EggStage.Ready && frame % 2 == 1)
                {
                    Sparkle(c, p, cx - ew / 2 - 3, bottom - eh);
                    Sparkle(c, p, cx + ew / 2 + 3, bottom - eh / 2);
                }
                if (v.Stage == EggStage.Waiting) Bang(c, p, cx + ew / 2 + 1, Math.Max(y + 12, bottom - eh - 8), frame);
            }
        }
        // The front lip of the nest, over the egg's foot.
        for (var i = -rx + 1; i < rx; i += 2)
            Rect(c, p, (i / 2) % 2 == 0 ? straw : strawDark, cx + i, bottom + ry / 2 - Math.Abs(i) * ry / (2 * rx), 2, 1);
        Rect(c, p, straw, cx - rx + 1, bottom + 1, rx * 2 - 1, 1);
        if (phase == ParkDayPhase.Night) Veil(c, p, x, y + horizon - 10, w, h - horizon + 10);
    }

    /// <summary>A row of pips, filled to <paramref name="progress"/>.</summary>
    private static void Pips(SKCanvas c, SKPaint p, int x, int y, int count, float progress)
    {
        var lit = (int)Math.Round(progress * count);
        for (var i = 0; i < count; i++)
        {
            Rect(c, p, PanelRim, x + i * 5, y, 4, 4);
            Rect(c, p, i < lit ? new SKColor(0xf8, 0xc0, 0x40) : PanelFill, x + i * 5 + 1, y + 1, 2, 2);
        }
    }

    // ── Before the first real picture ──────────────────────────────────────────────────────────

    /// <summary>
    /// A widget's neutral face: its own art (the Pokéwalker with a blank screen, the partner's empty
    /// stage, the Day Care's fence) under its title, with the PKForge mark and nothing else. The
    /// launcher's initial picture and the quick first pass use it: a host that photographs a widget
    /// once (Cocoon) keeps that picture, so it must never say anything that could stop being true.
    /// </summary>
    public static SKBitmap RenderNeutral(HomeWidgetKind kind, HomeWidgetSizeHint size, int widthPx, int heightPx, SKImage? shell, SKTypeface face)
    {
        var walker = kind == HomeWidgetKind.Walker;
        var step = StepFor(kind, size, widthPx, heightPx);
        int w = Math.Max(walker ? 98 : 48, (widthPx + step - 1) / step), h = Math.Max(walker ? 80 : 48, (heightPx + step - 1) / step);
        using var scene = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        var words = new Words();
        var title = kind switch { HomeWidgetKind.Walker => "Pokéwalker", HomeWidgetKind.Partner => "Partner", _ => "Day Care" };
        using (var c = new SKCanvas(scene))
        using (var p = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill })
        {
            Rect(c, p, Backdrop, 0, 0, w, h);
            switch (size)
            {
                case HomeWidgetSizeHint.Small:
                {
                    const int strip = 13;
                    Art(c, p, 0, 0, w, h - strip);
                    Panel(c, p, 0, h - strip, w, strip);
                    Rect(c, p, Ink, 0, h - 1, w, 1);
                    if (walker)
                    {
                        words.Add(title, 3, h - 3.5f, PanelText, w * 0.62f);
                        words.Add(Mark, w - 3, h - 3.5f, SubText, w * 0.35f, right: true);
                    }
                    else
                    {
                        TitleTab(c, p, words, title, 0, 0, w - 4);
                        words.Add(Mark, 3, h - 3.5f, SubText, w - 6);
                    }
                    break;
                }
                case HomeWidgetSizeHint.Wide:
                {
                    var region = walker ? Math.Clamp(h, 104, w / 2) : Math.Min(w / 2, Math.Max(Math.Max(h, 60), w * 2 / 5));
                    Art(c, p, 0, 0, region, h);
                    if (walker)
                    {
                        var px = region + 2;
                        Panel(c, p, px, 2, w - px - 2, h - 4);
                        TitleTab(c, p, words, title, px + 1, 3, w - px - 4);
                        words.Add(Mark, px + 4, 24, SubText, w - px - 10);
                    }
                    else
                    {
                        Panel(c, p, region, 0, w - region, h);
                        Rect(c, p, Ink, 0, h - 1, w, 1);
                        TitleTab(c, p, words, title, region + 1, 1, w - region - 4);
                        words.Add(Mark, region + 4, 22, SubText, w - region - 10);
                    }
                    break;
                }
                default:
                {
                    const int panel = 15;
                    if (walker)
                    {
                        Art(c, p, 0, 0, w, h - panel - 2);
                        Panel(c, p, 2, h - panel - 2, w - 4, panel);
                        words.Add(Mark, 6, h - panel + 7, SubText, w - 14);
                        TitleTab(c, p, words, title, 0, 0, w / 2);
                    }
                    else
                    {
                        Art(c, p, 0, 0, w, h - panel);
                        Panel(c, p, 0, h - panel, w, panel);
                        Rect(c, p, Ink, 0, h - 1, w, 1);
                        words.Add(Mark, 4, h - panel + 9, SubText, w - 10);
                        TitleTab(c, p, words, title, 0, 0, w - 4);
                    }
                    break;
                }
            }
        }
        return Finish(scene, step, words, face);

        void Art(SKCanvas c, SKPaint p, int x, int y, int aw, int ah)
        {
            switch (kind)
            {
                case HomeWidgetKind.Walker:
                {
                    using var blank = BlankLcd();
                    var lx = x + (aw - WalkerLcd.Width) / 2;
                    var ly = shell is not null && ah >= WalkerDevice.Height + 2 ? y + (ah - WalkerDevice.Height) / 2 + WalkerDevice.Screen.Top
                        : y + Math.Max(3, (ah - WalkerLcd.Height) / 2 + 2);
                    Device(c, p, shell, blank, lx, ly);
                    break;
                }
                case HomeWidgetKind.Partner:
                    Stage(c, p, null, true, PartnerMood.Awake, ParkDayPhase.Day, x, y, aw, ah, 0, bare: true);
                    break;
                default:
                    Nest(c, p, new EggWidgetView(EggStage.None, "", "", "", 0f, ""), ParkDayPhase.Day, x, y, aw, ah, 0, 3);
                    break;
            }
        }
    }

    /// <summary>
    /// A painted layout placed in a launcher tile of another shape: centred, at its own pixel scale (so
    /// its whole steps stay whole), on the widgets' own background. A new bitmap of the tile's proportions.
    /// </summary>
    public static SKBitmap InTile(SKBitmap picture, int tileWidth, int tileHeight)
    {
        var (w, h, x, y) = WidgetLayout.Canvas(picture.Width, picture.Height, tileWidth, tileHeight);
        var output = new SKBitmap(w, h, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(output);
        canvas.Clear(Backdrop);
        canvas.DrawBitmap(picture, x, y);
        return output;
    }

    /// <summary>The LCD switched on, with nothing on it yet.</summary>
    private static SKImage BlankLcd()
    {
        using var bitmap = new SKBitmap(WalkerLcd.Width, WalkerLcd.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(WalkerPaint.LcdTones[0]);
        return SKImage.FromBitmap(bitmap);
    }
}
