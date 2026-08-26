using PKForge.Chrome;
using SkiaSharp;

// Renders the Gen-5 chrome to PNG previews so the design can be iterated off-device
// against the reference screenshots. Usage: dotnet run --project tools/ChromePreview
var root = FindRepoRoot();
var art = new PksmArt();
foreach (var f in Directory.GetFiles(Path.Combine(root, "src/PKForge.App/Resources/UI/pksm"), "*.png"))
    art.Supply(Path.GetFileName(f), File.ReadAllBytes(f));

string[] spriteIds = ["b_645.png", "b_25.png", "b_149.png", "b_143.png", "b_9.png", "b_658.png", "b_445.png", "b_376.png",
    "b_16.png", "b_64.png", "b_130.png", "b_50.png", "b_113.png"];
var spriteDir = Path.Combine(root, "external/PKHeX/PKHeX.Drawing.PokeSprite/Resources/img/Big Pokemon Sprites");
foreach (var id in spriteIds)
    art.Supply("mon_" + id, File.ReadAllBytes(Path.Combine(spriteDir, id)));

using var typeface = SKTypeface.FromFile(Path.Combine(root, "src/PKForge.App/Resources/Fonts/NDS12.ttf"));
SKFont Font(float size = 24) => new(typeface, size);

var ballDir = Path.Combine(root, "external/PKHeX/PKHeX.Drawing.PokeSprite/Resources/img/ball");
foreach (var f in Directory.GetFiles(ballDir, "*.png"))
    art.Supply("ball_" + Path.GetFileName(f), File.ReadAllBytes(f));

var outDir = Path.Combine(root, "tools/ChromePreview/out");
Directory.CreateDirectory(outDir);

(string Mon, string Name, int Lv, int Ball, int Hp, int Max, bool Male)[] partyA =
[
    ("b_25.png", "Pikachu", 20, 4, 45, 45, true),
    ("b_16.png", "Pidgeotto", 20, 2, 57, 57, true),
    ("b_64.png", "Kadabra", 20, 4, 46, 46, true),
    ("b_130.png", "Gyarados", 20, 5, 71, 71, true),
    ("b_50.png", "Diglett", 20, 1, 36, 36, true),
    ("b_113.png", "Chansey", 20, 3, 131, 131, false),
];

RenderMenu(Path.Combine(outDir, "menu.png"));
RenderStorage(Path.Combine(outDir, "storage.png"));
RenderEditor(Path.Combine(outDir, "editor.png"));
RenderEvents(Path.Combine(outDir, "events.png"));
RenderBag(Path.Combine(outDir, "bag.png"));
RenderParty(Path.Combine(outDir, "party_gen4.png"), gen4: true);
RenderParty(Path.Combine(outDir, "party_gen5.png"), gen4: false);
RenderPartyC(Path.Combine(outDir, "party_design.png"));
RenderPartyNavy(Path.Combine(outDir, "party_navy.png"));
Console.WriteLine($"wrote previews to {outDir}");

// ---------- mock screens (1280x675, the Thor's landscape shape) ----------

/// <summary>The B/W housing: dark grey with the faint grid.</summary>
static void Grid(SKCanvas c, SKRect r)
{
    using var bg = new SKPaint { Color = Pksm.Housing };
    c.DrawRect(r, bg);
    using var dot = new SKPaint { Color = Pksm.HousingDot };
    for (var y = 7f; y < r.Height; y += 14)
        for (var x = 7f; x < r.Width; x += 14)
            c.DrawRect(x, y, x + 2, y + 2, dot);
}

void StatusStrip(SKCanvas c, SKRect bar)
{
    using var p = new SKPaint { Color = Pksm.Paper };
    c.DrawRect(bar, p);
    c.DrawRect(new SKRect(bar.Left, bar.Bottom - 2, bar.Right, bar.Bottom), new SKPaint { Color = Pksm.PaperEdge });
    PksmPaint.CenterText(c, "PKForge", bar.Left + 24, bar.MidY, Font(22), Pksm.Ink, SKColors.White.WithAlpha(0x60));
    PksmPaint.CenterText(c, "OFFLINE", bar.Right - 130, bar.MidY, Font(18), Pksm.InkSoft, SKColors.White.WithAlpha(0x50));
    c.DrawRect(new SKRect(bar.Right - 80, bar.MidY - 6, bar.Right - 56, bar.MidY + 6), new SKPaint { Color = new SKColor(0x5F, 0xD0, 0x6A) });
}

void RenderMenu(string path)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;
    Grid(c, All(1280, 675));
    StatusStrip(c, new SKRect(0, 0, 1280, 34));

    // home tiles: glossy black buttons with periwinkle icons, 2x3 like the B/W menu
    (string icon, string label)[] tiles = [("pkf_bank.png", "BANK"), ("icon_events.png", "EVENTS"), ("icon_settings.png", "SETTINGS"), ("icon_storage.png", "STORAGE"), ("pkf_pokedex.png", "POKéDEX"), ("icon_item.png", "ITEMS")];
    for (var i = 0; i < tiles.Length; i++)
    {
        var col = i % 2; var row = i / 2;
        var r = new SKRect(80 + col * 250, 90 + row * 130, 80 + col * 250 + 220, 90 + row * 130 + 106);
        if (i == 0) PksmPaint.SelectedButton(c, r); else PksmPaint.BlackButton(c, r);
        art.DrawScaled(c, tiles[i].icon, r.Left + 18, r.MidY - 32, 2);
        PksmPaint.CenterText(c, tiles[i].label, r.Left + 104, r.MidY, Font(24), SKColors.White, SKColors.Black.WithAlpha(0x60));
    }

    // a menu window on the right
    var win = new SKRect(640, 90, 1220, 480);
    PksmPaint.DarkWindow(c, win, 8);
    PksmPaint.HeaderStrip(c, new SKRect(win.Left + 12, win.Top + 12, win.Right - 12, win.Top + 54), "SETTINGS", Font(22));
    var rows = new[] { ("icon_editor.png", "Edit Pokémon"), ("icon_item.png", "Items"), ("icon_storage.png", "Boxes"), ("icon_party.png", "Party"), ("icon_settings.png", "More") };
    for (var i = 0; i < rows.Length; i++)
    {
        var row = new SKRect(win.Left + 14, win.Top + 68 + i * 58, win.Right - 14, win.Top + 126 + i * 58);
        if (i == 1) PksmPaint.SelectedButton(c, row, 5); else PksmPaint.BlackButton(c, row, 5);
        art.DrawScaled(c, rows[i].Item1, row.Left + 16, row.MidY - 26, 2);
        PksmPaint.CenterText(c, rows[i].Item2, row.Left + 100, row.MidY, Font(24), SKColors.White, SKColors.Black.WithAlpha(0x60));
        if (i == 1) PksmPaint.Pointer(c, new SKPoint(row.Left - 26, row.MidY - 14), 14);
    }

    PksmPaint.HintBar(c, new SKRect(0, 625, 1280, 675), [("A", "Confirm"), ("B", "Back"), ("Y", "Swap")], Font(24));
    Save(s, path);
}

void RenderStorage(string path)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;
    Grid(c, All(1280, 675));
    StatusStrip(c, new SKRect(0, 0, 1280, 34));

    // the box world sits on the dark chrome like a game screen
    var world = new SKRect(40, 60, 960, 600);
    var green = Pksm.BoxWallpapers[0];
    PksmPaint.Wallpaper(c, world, green);
    PksmPaint.FrameBrackets(c, world);

    for (var i = 0; i < 30; i++)
    {
        var col = i % 6; var row = i / 6;
        var slot = new SKRect(world.Left + 28 + col * 146, world.Top + 40 + row * 82, world.Left + 28 + col * 146 + 128, world.Top + 40 + row * 82 + 72);
        var hasMon = i is 1 or 2 or 7 or 9 or 14 or 16 or 21 or 28;
        PksmPaint.Slot(c, slot, green, !hasMon);
        if (hasMon)
        {
            var mon = spriteIds[Array.IndexOf(new[] { 1, 2, 7, 9, 14, 16, 21, 28 }, i)];
            var b = art.Get("mon_" + mon);
            if (b is not null)
            {
                var scale = Math.Min(118f / b.Width, 66f / b.Height);
                var w = b.Width * scale; var h = b.Height * scale;
                c.DrawBitmap(b, new SKRect(slot.MidX - w / 2, slot.Bottom - h, slot.MidX + w / 2, slot.Bottom), new SKPaint());
            }
        }
        if (i == 9) PksmPaint.Selection(c, slot);
    }

    // dark box banner with white chevrons + red triangle
    PksmPaint.BoxNameBar(c, new SKRect(340, 66, 660, 104), "BOX 1", Font(22), true, true);
    PksmPaint.Pointer(c, new SKPoint(310, 76), 16);

    // vertical stack in recessed blue
    string[] ops = ["VIEW", "CLEAR", "RELEASE", "TOOLS", "SAVE"];
    for (var i = 0; i < ops.Length; i++)
    {
        var b = new SKRect(world.Right - 72, 120 + i * 84, world.Right - 4, 180 + i * 84);
        PksmPaint.StackButton(c, b, i == 0);
        PksmPaint.CenterText(c, ops[i], b.MidX, b.MidY, Font(18), SKColors.White, SKColors.Black.WithAlpha(0x50), SKTextAlign.Center);
    }

    // white info card on the right
    var info = new SKRect(990, 60, 1240, 600);
    PksmPaint.Panel(c, info);
    PksmPaint.HeaderStrip(c, new SKRect(info.Left + 10, info.Top + 10, info.Right - 10, info.Top + 54), "LANDORUS", Font(22));
    string[] facts = ["#645", "Lv.55", "GROUND", "FLYING", "OT Bernardo", "ID 35053", "Adamant", "IV 31/29/31", "20/28/31"];
    for (var i = 0; i < facts.Length; i++)
        PksmPaint.CenterText(c, facts[i], info.Left + 20, info.Top + 92 + i * 46, Font(24), Pksm.Ink, SKColors.White.WithAlpha(0x60));

    PksmPaint.HintBar(c, new SKRect(0, 625, 1280, 675), [("LR", "Box"), ("A", "Select"), ("B", "Back"), ("X", "Tools")], Font(24));
    Save(s, path);
}

void RenderEditor(string path)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;
    Grid(c, All(1280, 675));
    StatusStrip(c, new SKRect(0, 0, 1280, 34));

    // the summary world: cyan panel like the Kalos dex top screen
    var world = new SKRect(40, 56, 1240, 600);
    c.DrawRoundRect(world, 10, 10, new SKPaint { Color = Pksm.DexCyan, IsAntialias = true });
    PksmPaint.HeaderStrip(c, new SKRect(60, 76, 620, 122), "POKéMON INFORMATION", Font(24));

    var left = new SKRect(60, 140, 620, 580);
    PksmPaint.Panel(c, left);
    string[] attrs = ["Nickname  Ampharos", "OT  PKSM", "Nature  Modest", "Ability  Static", "Item  None", "TID/SID  12345/54321", "Friendship  177"];
    for (var i = 0; i < attrs.Length; i++)
    {
        var row = new SKRect(left.Left + 10, left.Top + 16 + i * 48, left.Right - 10, left.Top + 64 + i * 48);
        PksmPaint.StripeRow(c, row, i == 2);
        PksmPaint.CenterText(c, attrs[i], row.Left + 18, row.MidY, Font(24), i == 2 ? SKColors.White : Pksm.Ink, SKColors.White.WithAlpha(0x50));
    }

    for (var i = 0; i < 3; i++)
    {
        var b = new SKRect(left.Left + 20, left.Top + 372 + i * 72, left.Left + 190, left.Top + 432 + i * 72);
        PksmPaint.ChoiceButton(c, b, pressed: false, focused: i == 0);
        PksmPaint.CenterText(c, new[] { "STATS", "MOVES", "SAVE" }[i], b.MidX, b.MidY, Font(24), SKColors.White, SKColors.Black.WithAlpha(0x60), SKTextAlign.Center);
    }

    var right = new SKRect(660, 140, 1220, 580);
    PksmPaint.Panel(c, right);
    PksmPaint.HeaderStrip(c, new SKRect(right.Left + 10, right.Top + 10, right.Right - 10, right.Top + 54), "STATS", Font(22));
    string[] stats = ["HP        31 · 252 · 384", "Attack    30 ·   0 · 166", "Defense   31 ·   0 · 206", "Sp. Atk   31 · 252 · 361", "Sp. Def   31 ·   0 · 216", "Speed     31 ·   4 · 147"];
    for (var i = 0; i < stats.Length; i++)
        PksmPaint.CenterText(c, stats[i], right.Left + 28, right.Top + 100 + i * 48, Font(24), Pksm.Ink, SKColors.White.WithAlpha(0x50));

    PksmPaint.HintBar(c, new SKRect(0, 625, 1280, 675), [("A", "Edit"), ("B", "Back"), ("X", "Legalize")], Font(24));
    Save(s, path);
}

void RenderEvents(string path)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;
    c.DrawRect(All(1280, 675), new SKPaint { Color = Pksm.GiftPink, IsAntialias = true });
    var rnd = new Random(7);
    for (var i = 0; i < 26; i++)
        PksmPaint.Sparkle(c, new SKPoint(20 + rnd.Next(1240), 20 + rnd.Next(580)), 3 + rnd.Next(9));

    PksmPaint.HeaderStrip(c, new SKRect(40, 24, 760, 72), "まぼろしのポケモン マーシャドー", Font(24));

    var card = new SKRect(60, 104, 640, 560);
    PksmPaint.Panel(c, card);
    string[] rows = ["Species  Marshadow", "Level  50", "OT  テンセイざん", "TID/SID  39899/3481", "Game  SM", "Date  1/1/2000", "Item  Marshadium Z"];
    for (var i = 0; i < rows.Length; i++)
        PksmPaint.CenterText(c, rows[i], card.Left + 26, card.Top + 56 + i * 46, Font(24), Pksm.Ink, SKColors.White.WithAlpha(0x60));

    var moves = new SKRect(680, 104, 1240, 384);
    PksmPaint.Panel(c, moves);
    PksmPaint.HeaderStrip(c, new SKRect(moves.Left + 10, moves.Top + 10, moves.Right - 10, moves.Top + 54), "MOVES", Font(22));
    string[] mrows = ["Spectral Thief", "Close Combat", "Force Palm", "Shadow Ball"];
    for (var i = 0; i < mrows.Length; i++)
    {
        var row = new SKRect(moves.Left + 12, moves.Top + 70 + i * 48, moves.Right - 12, moves.Top + 118 + i * 48);
        PksmPaint.StripeRow(c, row, i == 0);
        PksmPaint.CenterText(c, mrows[i], row.Left + 20, row.MidY, Font(24), i == 0 ? SKColors.White : Pksm.Ink, SKColors.White.WithAlpha(0x50));
    }

    string[] langs = ["JPN", "ENG", "FRE", "ITA", "GER", "SPA", "KOR", "CHS", "CHT"];
    for (var i = 0; i < langs.Length; i++)
    {
        var r = new SKRect(680 + (i % 5) * 116, 430 + (i / 5) * 56, 680 + (i % 5) * 116 + 104, 430 + (i / 5) * 56 + 44);
        PksmPaint.LangChip(c, r, i == 0, Pksm.GiftPinkLight, Pksm.GiftRed);
        PksmPaint.CenterText(c, langs[i], r.MidX, r.MidY, Font(20), SKColors.White, SKColors.Black.WithAlpha(0x40), SKTextAlign.Center);
    }
    PksmPaint.HintBar(c, new SKRect(0, 625, 1280, 675), [("LR", "Card"), ("A", "Receive"), ("B", "Back"), ("START", "Inject")], Font(24));
    Save(s, path);
}

// ---------- party mocks: the two reference eras, real sprites ----------


void RenderParty(string path, bool gen4)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;
    Grid(c, All(1280, 675));
    StatusStrip(c, new SKRect(0, 0, 1280, 34));

    // box banner
    PksmPaint.BoxNameBar(c, new SKRect(340, 52, 660, 90), "PARTY", Font(22), true, true);

    const float pad = 56, gap = 14;
    var cw = (1000 - pad - gap) / 2f;
    var ch = 148f;
    for (var i = 0; i < 6; i++)
    {
        var col = i % 2; var row = i / 2;
        var r = new SKRect(pad + col * (cw + gap), 116 + row * (ch + gap), pad + col * (cw + gap) + cw, 116 + row * (ch + gap) + ch);
        if (gen4) CardGen4(c, r, partyA[i], i == 0);
        else CardGen5(c, r, partyA[i], i == 0);
    }

    // side panel hint that chrome stays
    var info = new SKRect(1040, 116, 1240, 560);
    PksmPaint.Panel(c, info);
    PksmPaint.HeaderStrip(c, new SKRect(info.Left + 10, info.Top + 10, info.Right - 10, info.Top + 54), "PIKACHU", Font(22));

    PksmPaint.HintBar(c, new SKRect(0, 625, 1280, 675), [("LR", "Box"), ("A", "Select"), ("B", "Back"), ("START", "Menu")], Font(24));
    Save(s, path);
}

void CardGen4(SKCanvas c, SKRect r, (string Mon, string Name, int Lv, int Ball, int Hp, int Max, bool Male) m, bool selected)
{
    var green = new SKColor(0x57, 0xB9, 0x5A);
    var edge = new SKColor(0x2E, 0x8F, 0x3E);
    var shade = new SKColor(0x1E, 0x5A, 0x28);
    using (var f = new SKPaint { Color = green, IsAntialias = true }) c.DrawRoundRect(r, 8, 8, f);
    using (var e = new SKPaint { Color = edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f }) c.DrawRoundRect(r, 8, 8, e);
    if (selected) PksmPaint.Selection(c, r);

    // sprite, left, bottom-aligned
    var bmp = art.Get("mon_" + m.Mon);
    if (bmp is not null)
    {
        var max = r.Height - 14;
        var scale = Math.Min((r.Width * 0.34f) / bmp.Width, max / bmp.Height);
        var w = bmp.Width * scale; var h = bmp.Height * scale;
        c.DrawBitmap(bmp, new SKRect(r.Left + 14, r.Bottom - 8 - h, r.Left + 14 + w, r.Bottom - 8), new SKPaint());
    }
    // ball badge, top-left corner
    var ballBmp = art.Get($"ball__ball{m.Ball}.png") ?? art.Get("ball__ball1.png");
    if (ballBmp is not null)
        c.DrawBitmap(ballBmp, new SKRect(r.Left + 4, r.Top - 6, r.Left + 30, r.Top + 20), new SKPaint());

    var tx = r.Left + r.Width * 0.36f;
    void ShadowText(string text, float x, float y, float size, SKColor color, SKTextAlign align = SKTextAlign.Left)
    {
        using var shadow = new SKPaint { Color = shade, IsAntialias = true };
        using var fg = new SKPaint { Color = color, IsAntialias = true };
        var f = Font(size);
        c.DrawText(text, x + 2, y + 2, align, f, shadow);
        c.DrawText(text, x, y, align, f, fg);
    }
    ShadowText(m.Name, tx, r.Top + 44, 26, SKColors.White);
    ShadowText($"Lv.{m.Lv}", tx, r.Top + 78, 20, SKColors.White);
    // gender small, right of name
    DrawGenderGlyph(c, new SKPoint(tx + 150, r.Top + 34), 7, m.Male);

    // HP: label + bar + numbers
    ShadowText("HP", tx, r.Top + 116, 15, SKColors.White);
    var bar = new SKRect(tx + 34, r.Top + 104, r.Right - 16, r.Top + 118);
    using (var track = new SKPaint { Color = shade, IsAntialias = true }) c.DrawRoundRect(bar, 3, 3, track);
    var ratio = m.Hp / (float)m.Max;
    using (var fill = new SKPaint { Color = new SKColor(0x7C, 0xE0, 0x7F), IsAntialias = true })
        c.DrawRoundRect(new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * ratio, bar.Bottom), 3, 3, fill);
    ShadowText($"{m.Hp}/ {m.Max}", r.Right - 16, r.Bottom - 12, 20, SKColors.White, SKTextAlign.Right);
}

void CardGen5(SKCanvas c, SKRect r, (string Mon, string Name, int Lv, int Ball, int Hp, int Max, bool Male) m, bool selected)
{
    var body = new SKColor(0x23, 0x27, 0x2E);
    var frame = new SKColor(0x3D, 0x45, 0x50);
    // angular notched frame: cut the top-right corner
    var path = new SKPath();
    path.MoveTo(r.Left + 6, r.Top);
    path.LineTo(r.Right - 18, r.Top);
    path.LineTo(r.Right, r.Top + 18);
    path.LineTo(r.Right, r.Bottom - 6);
    path.LineTo(r.Right - 6, r.Bottom);
    path.LineTo(r.Left + 6, r.Bottom);
    path.LineTo(r.Left, r.Bottom - 6);
    path.LineTo(r.Left, r.Top + 6);
    path.Close();
    using (var f = new SKPaint { Color = frame, IsAntialias = true }) c.DrawPath(path, f);
    var inner = new SKPath();
    var i2 = SKRect.Inflate(r, -2.5f, -2.5f);
    inner.MoveTo(i2.Left + 6, i2.Top);
    inner.LineTo(i2.Right - 18, i2.Top);
    inner.LineTo(i2.Right, i2.Top + 18);
    inner.LineTo(i2.Right, i2.Bottom - 6);
    inner.LineTo(i2.Right - 6, i2.Bottom);
    inner.LineTo(i2.Left + 6, i2.Bottom);
    inner.LineTo(i2.Left, i2.Bottom - 6);
    inner.LineTo(i2.Left, i2.Top + 6);
    inner.Close();
    using (var f = new SKPaint { Color = body, IsAntialias = true }) c.DrawPath(inner, f);
    if (selected) PksmPaint.Selection(c, r);

    // ball in a circle badge overlapping the top-left
    var badge = new SKRect(r.Left - 10, r.Top - 10, r.Left + 34, r.Top + 34);
    using (var b1 = new SKPaint { Color = SKColors.White, IsAntialias = true }) c.DrawOval(badge, b1);
    using (var b2 = new SKPaint { Color = frame, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f }) c.DrawOval(badge, b2);
    var ballBmp = art.Get($"ball__ball{m.Ball}.png") ?? art.Get("ball__ball1.png");
    if (ballBmp is not null)
        c.DrawBitmap(ballBmp, new SKRect(r.Left - 4, r.Top - 4, r.Left + 28, r.Top + 28), new SKPaint());

    // sprite overlapping the badge, bottom-left
    var bmp = art.Get("mon_" + m.Mon);
    if (bmp is not null)
    {
        var max = r.Height - 20;
        var scale = Math.Min((r.Width * 0.34f) / bmp.Width, max / bmp.Height);
        var w = bmp.Width * scale; var h = bmp.Height * scale;
        c.DrawBitmap(bmp, new SKRect(r.Left + 8, r.Bottom - 6 - h, r.Left + 8 + w, r.Bottom - 6), new SKPaint());
    }

    var tx = r.Left + r.Width * 0.38f;
    void T(string text, float x, float y, float size, SKColor color, SKTextAlign align = SKTextAlign.Left)
    {
        using var fg = new SKPaint { Color = color, IsAntialias = true };
        c.DrawText(text, x, y, align, Font(size), fg);
    }
    T(m.Name, tx, r.Top + 42, 26, SKColors.White);
    DrawGenderGlyph(c, new SKPoint(tx + 170, r.Top + 32), 7, m.Male);
    T($"Lv.{m.Lv}", tx, r.Top + 72, 19, new SKColor(0xB9, 0xC6, 0xD3));
    T("HP", tx, r.Top + 108, 14, new SKColor(0x8A, 0x99, 0xA8));
    var bar = new SKRect(tx + 34, r.Top + 98, r.Right - 16, r.Top + 112);
    using (var track = new SKPaint { Color = new SKColor(0x0E, 0x12, 0x14), IsAntialias = true }) c.DrawRoundRect(bar, 3, 3, track);
    var ratio = m.Hp / (float)m.Max;
    using (var fill = new SKPaint { Color = new SKColor(0x3F, 0xE0, 0x7F), IsAntialias = true })
        c.DrawRoundRect(new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * ratio, bar.Bottom), 3, 3, fill);
    T($"{m.Hp}/ {m.Max}", r.Right - 16, r.Bottom - 12, 20, SKColors.White, SKTextAlign.Right);
}

// ---------- design variant: sprite hero on a platform, type-ribbon identity ----------

void RenderPartyC(string path)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;
    Grid(c, All(1280, 675));
    StatusStrip(c, new SKRect(0, 0, 1280, 34));
    PksmPaint.BoxNameBar(c, new SKRect(340, 52, 660, 90), "PARTY", Font(22), true, true);

    (string Mon, string Name, int Lv, int Ball, int Hp, int Max, bool Male, SKColor Type)[] party =
    [
        ("b_25.png", "Pikachu", 20, 4, 45, 45, true, new SKColor(0xF7, 0xD0, 0x2C)),
        ("b_16.png", "Pidgeotto", 20, 2, 57, 57, true, new SKColor(0xA8, 0xA8, 0x78)),
        ("b_64.png", "Kadabra", 20, 4, 46, 46, true, new SKColor(0xF9, 0x55, 0x87)),
        ("b_130.png", "Gyarados", 20, 5, 71, 71, true, new SKColor(0x63, 0x90, 0xF0)),
        ("b_50.png", "Diglett", 20, 1, 36, 36, true, new SKColor(0xE2, 0xBF, 0x65)),
        ("b_113.png", "Chansey", 20, 3, 131, 131, false, new SKColor(0xA8, 0xA8, 0x78)),
    ];

    const float pad = 56, gap = 14;
    var cw = (1000 - pad - gap) / 2f;
    var ch = 152f;
    for (var i = 0; i < 6; i++)
    {
        var col = i % 2; var row = i / 2;
        var r = new SKRect(pad + col * (cw + gap), 116 + row * (ch + gap), pad + col * (cw + gap) + cw, 116 + row * (ch + gap) + ch);
        CardDesign(c, r, party[i], i == 0);
    }

    var info = new SKRect(1040, 116, 1240, 560);
    PksmPaint.Panel(c, info);
    PksmPaint.HeaderStrip(c, new SKRect(info.Left + 10, info.Top + 10, info.Right - 10, info.Top + 54), "PIKACHU", Font(22));
    PksmPaint.HintBar(c, new SKRect(0, 625, 1280, 675), [("LR", "Box"), ("A", "Select"), ("B", "Back"), ("START", "Menu")], Font(24));
    Save(s, path);
}

void CardDesign(SKCanvas c, SKRect r, (string Mon, string Name, int Lv, int Ball, int Hp, int Max, bool Male, SKColor Type) m, bool selected)
{
    // white card with a 3px depth edge at the bottom
    using (var depth = new SKPaint { Color = Pksm.PaperEdgeDeep, IsAntialias = true })
        c.DrawRoundRect(new SKRect(r.Left, r.Top + 2, r.Right, r.Bottom + 2), 8, 8, depth);
    using (var fill = new SKPaint { Color = Pksm.Paper, IsAntialias = true })
        c.DrawRoundRect(r, 8, 8, fill);
    using (var edge = new SKPaint { Color = Pksm.PaperEdge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
        c.DrawRoundRect(r, 8, 8, edge);

    // type ribbon down the left edge (the mon's identity color, dampened)
    using (var rib = new SKPaint { Color = m.Type, IsAntialias = true })
    {
        c.Save();
        var clip = new SKRoundRect(r, 8);
        c.ClipRoundRect(clip, antialias: true);
        c.DrawRect(new SKRect(r.Left, r.Top, r.Left + 7, r.Bottom), rib);
        c.Restore();
    }

    // arena zone: pale tint inset with the platform ellipse the sprite stands on
    var arena = new SKRect(r.Left + 12, r.Top + 10, r.Left + r.Width * 0.40f, r.Bottom - 10);
    using (var zone = new SKPaint { Color = new SKColor(0xE2, 0xEF, 0xDC), IsAntialias = true })
        c.DrawRoundRect(arena, 6, 6, zone);
    var platform = new SKRect(arena.Left + arena.Width * 0.1f, arena.Bottom - arena.Height * 0.3f, arena.Right - arena.Width * 0.1f, arena.Bottom - arena.Height * 0.08f);
    using (var plat = new SKPaint { Color = new SKColor(0xBA, 0xD6, 0xB0), IsAntialias = true })
        c.DrawOval(platform, plat);

    var bmp = art.Get("mon_" + m.Mon);
    if (bmp is not null)
    {
        var scale = Math.Min((arena.Width * 1.02f) / bmp.Width, (arena.Height * 0.98f) / bmp.Height);
        var w = bmp.Width * scale; var h = bmp.Height * scale;
        c.DrawBitmap(bmp, new SKRect(arena.MidX - w / 2, platform.MidY - h + platform.Height * 0.45f, arena.MidX + w / 2, platform.MidY + platform.Height * 0.45f), new SKPaint());
    }

    var tx = r.Left + r.Width * 0.46f;
    void T(string text, float x, float y, float size, SKColor color, SKTextAlign align = SKTextAlign.Left, bool bold = true)
    {
        using var fg = new SKPaint { Color = color, IsAntialias = true };
        c.DrawText(text, x, y, align, Font(size), fg);
    }

    // name + gender
    T(m.Name, tx, r.Top + 42, 25, Pksm.Ink);
    DrawGenderGlyph(c, new SKPoint(tx + 160, r.Top + 33), 7, m.Male);

    // ball + level row
    var ballBmp = art.Get($"ball__ball{m.Ball}.png") ?? art.Get("ball__ball1.png");
    if (ballBmp is not null)
        c.DrawBitmap(ballBmp, new SKRect(tx, r.Top + 58, tx + 24, r.Top + 82), new SKPaint());
    T($"Lv.{m.Lv}", tx + 32, r.Top + 78, 19, Pksm.InkSoft);

    // HP: label + track + fill + numbers
    T("HP", tx, r.Top + 118, 14, Pksm.InkSoft);
    var bar = new SKRect(tx + 34, r.Top + 108, r.Right - 16, r.Top + 122);
    using (var track = new SKPaint { Color = new SKColor(0x2A, 0x3A, 0x2E), IsAntialias = true })
        c.DrawRoundRect(bar, 3, 3, track);
    var ratio = m.Hp / (float)m.Max;
    using (var fill = new SKPaint { Color = new SKColor(0x3F, 0xE0, 0x7F), IsAntialias = true })
        c.DrawRoundRect(new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * ratio, bar.Bottom), 3, 3, fill);
    T($"{m.Hp}/{m.Max}", r.Right - 16, r.Bottom - 12, 20, Pksm.Ink, SKTextAlign.Right);

    if (selected) PksmPaint.Selection(c, r);
}

// ---------- navy party deck: retro GBA beveled slots ----------

void RenderPartyNavy(string path)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;

    // dark navy world with a faint grid
    c.DrawRect(All(1280, 675), new SKPaint { Color = new SKColor(0x14, 0x1D, 0x3E), IsAntialias = true });
    using (var line = new SKPaint { Color = new SKColor(0x1D, 0x2A, 0x55), StrokeWidth = 1 })
    {
        for (float x = 0; x < 1280; x += 26) c.DrawLine(x, 0, x, 675, line);
        for (float y = 0; y < 675; y += 26) c.DrawLine(0, y, 1280, y, line);
    }
    StatusStrip(c, new SKRect(0, 0, 1280, 34));
    PksmPaint.BoxNameBar(c, new SKRect(340, 52, 660, 90), "PARTY", Font(22), true, true);

    (string Mon, string Name, int Lv, int Ball, int Hp, int Max, bool Male)[] party =
    [
        ("b_25.png", "Pikachu", 20, 4, 45, 45, true),
        ("b_16.png", "Pidgeotto", 20, 2, 57, 57, true),
        ("b_64.png", "Kadabra", 20, 4, 46, 46, true),
        ("b_130.png", "Gyarados", 20, 5, 71, 71, true),
        ("b_50.png", "Diglett", 20, 1, 36, 36, true),
        ("b_113.png", "Chansey", 20, 3, 0, 131, false), // fainted: the red-brown slot
    ];

    const float pad = 52, gap = 12;
    var cw = (1010 - pad - gap) / 2f;
    var ch = 146f;
    for (var i = 0; i < 6; i++)
    {
        var col = i % 2; var row = i / 2;
        var r = new SKRect(pad + col * (cw + gap), 112 + row * (ch + gap), pad + col * (cw + gap) + cw, 112 + row * (ch + gap) + ch);
        SlotNavy(c, r, party[i], i == 0);
    }

    PksmPaint.HintBar(c, new SKRect(0, 625, 1280, 675), [("LR", "Box"), ("A", "Select"), ("B", "Back"), ("START", "Menu")], Font(24));
    Save(s, path);
}

void SlotNavy(SKCanvas c, SKRect r, (string Mon, string Name, int Lv, int Ball, int Hp, int Max, bool Male) m, bool selected)
{
    var fainted = m.Hp == 0;
    var body = fainted ? new SKColor(0x4A, 0x26, 0x20, 0xCC) : new SKColor(0x2A, 0x3A, 0x78, 0xC8);
    var topEdge = fainted ? new SKColor(0x7A, 0x44, 0x36, 0xE0) : new SKColor(0x5A, 0x70, 0xC8, 0xE0);
    var botEdge = new SKColor(0x0C, 0x14, 0x30, 0xE0);

    // beveled angular panel: chamfered corners, light top, dark bottom
    var path = new SKPath();
    path.MoveTo(r.Left + 10, r.Top);
    path.LineTo(r.Right - 26, r.Top);
    path.LineTo(r.Right, r.Top + 26);
    path.LineTo(r.Right, r.Bottom - 10);
    path.LineTo(r.Right - 10, r.Bottom);
    path.LineTo(r.Left + 10, r.Bottom);
    path.LineTo(r.Left, r.Bottom - 10);
    path.LineTo(r.Left, r.Top + 10);
    path.Close();
    using (var b = new SKPaint { Color = botEdge, IsAntialias = true })
        c.DrawPath(path, b);
    var inner = SKRect.Inflate(r, -2, -2.5f);
    var ipath = new SKPath();
    ipath.MoveTo(inner.Left + 10, inner.Top);
    ipath.LineTo(inner.Right - 26, inner.Top);
    ipath.LineTo(inner.Right, inner.Top + 26);
    ipath.LineTo(inner.Right, inner.Bottom - 10);
    ipath.LineTo(inner.Right - 10, inner.Bottom);
    ipath.LineTo(inner.Left + 10, inner.Bottom);
    ipath.LineTo(inner.Left, inner.Bottom - 10);
    ipath.LineTo(inner.Left, inner.Top + 10);
    ipath.Close();
    using (var b = new SKPaint { Color = body, IsAntialias = true })
        c.DrawPath(ipath, b);
    // the bevel light: top edge line
    using (var t = new SKPaint { Color = topEdge, IsAntialias = true, StrokeWidth = 3 })
        c.DrawLine(inner.Left + 10, inner.Top + 1.5f, inner.Right - 26, inner.Top + 1.5f, t);

    // selected: cyan frame
    if (selected)
    {
        using var sel = new SKPaint { Color = new SKColor(0x35, 0xB8, 0xC8), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        c.DrawPath(path, sel);
    }

    // sprite free in the slot, bottom-anchored; no pod, no box
    var bmp = art.Get("mon_" + m.Mon);
    if (bmp is not null)
    {
        var max = r.Height - 12;
        var scale = Math.Min((r.Width * 0.26f) / bmp.Width, max / bmp.Height);
        var w = bmp.Width * scale; var h = bmp.Height * scale;
        using var paint = new SKPaint();
        if (fainted) paint.ColorFilter = SKColorFilter.CreateBlendMode(new SKColor(0x70, 0x50, 0x48), SKBlendMode.SrcIn);
        c.DrawBitmap(bmp, new SKRect(r.Left + 14, r.Bottom - 4 - h, r.Left + 14 + w, r.Bottom - 4), paint);
    }

    var tx = r.Left + r.Width * 0.32f;
    void T(string text, float x, float y, float size, SKColor color, SKTextAlign align = SKTextAlign.Left)
    {
        using var fg = new SKPaint { Color = color, IsAntialias = true };
        c.DrawText(text, x, y, align, Font(size), fg);
    }

    var nameColor = fainted ? new SKColor(0xE0, 0xA9, 0x8A) : SKColors.White;
    T(m.Name, tx, r.Top + 42, 25, nameColor);
    DrawGenderGlyph(c, new SKPoint(tx + 165, r.Top + 33), 7, m.Male);
    var ballBmp = art.Get($"ball__ball{m.Ball}.png") ?? art.Get("ball__ball1.png");
    if (ballBmp is not null)
        c.DrawBitmap(ballBmp, new SKRect(tx, r.Top + 54, tx + 22, r.Top + 76), new SKPaint());
    T($"Lv.{m.Lv}", tx + 28, r.Top + 72, 19, fainted ? new SKColor(0xB0, 0x7A, 0x60) : new SKColor(0x8F, 0xA0, 0xC8));

    // HP: label + thin dark track + threshold fill + numbers right of bar
    T("HP", tx, r.Top + 112, 14, new SKColor(0x66, 0x7A, 0xB8));
    var bar = new SKRect(tx + 36, r.Top + 102, r.Right - 120, r.Top + 116);
    using (var track = new SKPaint { Color = new SKColor(0x0C, 0x14, 0x30), IsAntialias = true })
        c.DrawRoundRect(bar, 3, 3, track);
    var ratio = m.Max > 0 ? m.Hp / (float)m.Max : 0f;
    if (ratio > 0f)
    {
        var fill = ratio > 0.5f ? new SKColor(0x3F, 0xE0, 0x7F) : ratio > 0.2f ? new SKColor(0xE8, 0xC8, 0x4A) : new SKColor(0xE8, 0x58, 0x58);
        using var f = new SKPaint { Color = fill, IsAntialias = true };
        c.DrawRoundRect(new SKRect(bar.Left, bar.Top, bar.Left + bar.Width * ratio, bar.Bottom), 3, 3, f);
    }
    T($"{m.Hp}/{m.Max}", r.Right - 16, r.Top + 112, 20, nameColor, SKTextAlign.Right);
}

void DrawGenderGlyph(SKCanvas c, SKPoint center, float radius, bool male)
{
    var color = male ? new SKColor(0x4A, 0x8B, 0xF0) : new SKColor(0xF0, 0x7A, 0x9B);
    using var p = new SKPaint { Color = color, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.2f };
    if (male)
    {
        c.DrawCircle(center.X, center.Y + 1, radius, p);
        c.DrawLine(center.X + radius * 0.7f, center.Y - radius * 0.5f, center.X + radius * 1.5f, center.Y - radius * 1.3f, p);
        c.DrawLine(center.X + radius * 1.5f, center.Y - radius * 1.3f, center.X + radius * 0.75f, center.Y - radius * 1.3f, p);
        c.DrawLine(center.X + radius * 1.5f, center.Y - radius * 1.3f, center.X + radius * 1.5f, center.Y - radius * 0.55f, p);
    }
    else
    {
        c.DrawCircle(center.X, center.Y - 1, radius, p);
        c.DrawLine(center.X, center.Y + radius * 0.75f, center.X, center.Y + radius * 1.9f, p);
        c.DrawLine(center.X - radius * 0.55f, center.Y + radius * 1.3f, center.X + radius * 0.55f, center.Y + radius * 1.3f, p);
    }
}

void RenderBag(string path)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;
    c.DrawRect(All(1280, 675), new SKPaint { Color = Pksm.BagNavy, IsAntialias = true });

    string[] cats = ["Items", "Key Items", "TMs", "Medicine", "Berries", "Z-Crystals", "Rotom"];
    for (var i = 0; i < cats.Length; i++)
        PksmPaint.BagPill(c, new SKRect(40, 36 + i * 84, 300, 36 + i * 84 + 64), i == 0);

    var list = new SKRect(360, 36, 1240, 588);
    c.DrawRoundRect(list, 14, 14, new SKPaint { Color = Pksm.BagNavyDeep, IsAntialias = true });
    string[] items = ["Dive Ball x 8", "Mystic Water x 1", "Quick Ball x 5", "Nest Ball x 3", "Thunder Stone x 1", "Spell Tag x 1"];
    for (var i = 0; i < items.Length; i++)
    {
        var y = list.Top + 56 + i * 88;
        if (i == 0)
            c.DrawRoundRect(new SKRect(list.Left + 10, y - 38, list.Right - 10, y + 42), 10, 10, new SKPaint { Color = Pksm.BagCyan.WithAlpha(0x38), IsAntialias = true });
        PksmPaint.CountButton(c, new SKPoint(list.Left + 72, y + 2), 27, true);
        PksmPaint.CountButton(c, new SKPoint(list.Right - 72, y + 2), 27, false);
        PksmPaint.CenterText(c, items[i], list.MidX, y + 2, Font(26), SKColors.White, SKColors.Black.WithAlpha(0x50), SKTextAlign.Center);
    }
    PksmPaint.HintBar(c, new SKRect(0, 625, 1280, 675), [("UD", "Browse"), ("A", "Add"), ("B", "Back")], Font(24));
    Save(s, path);
}

// ---------- helpers ----------

static SKRect All(int w, int h) => new(0, 0, w, h);
static SKSurface Surface(int w, int h) => SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));

static void Save(SKSurface s, string path)
{
    using var img = s.Snapshot();
    using var data = img.Encode(SKEncodedImageFormat.Png, 100);
    using var fs = File.OpenWrite(path);
    data.SaveTo(fs);
}

static string FindRepoRoot()
{
    var dir = AppContext.BaseDirectory;
    while (dir is not null && !Directory.Exists(Path.Combine(dir, ".git")))
        dir = Path.GetDirectoryName(dir);
    return dir ?? throw new InvalidOperationException("repo root not found");
}
