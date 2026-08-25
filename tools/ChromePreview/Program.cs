using PKForge.Chrome;
using SkiaSharp;

// Renders the PKSM-style chrome to PNG contact sheets so the design can be iterated
// off-device. Usage: dotnet run --project tools/ChromePreview
var root = FindRepoRoot();
var art = new PksmArt();
foreach (var f in Directory.GetFiles(Path.Combine(root, "src/PKForge.App/Resources/UI/pksm"), "*.png"))
    art.Supply(Path.GetFileName(f), File.ReadAllBytes(f));

// a handful of real sprites so the mock reads like a storage screen
string[] spriteIds = ["b_645.png", "b_25.png", "b_149.png", "b_143.png", "b_9.png", "b_658.png", "b_445.png", "b_376.png"];
var spriteDir = Path.Combine(root, "external/PKHeX/PKHeX.Drawing.PokeSprite/Resources/img/Big Pokemon Sprites");
foreach (var id in spriteIds)
    art.Supply("mon_" + id, File.ReadAllBytes(Path.Combine(spriteDir, id)));

using var typeface = SKTypeface.FromFile(Path.Combine(root, "src/PKForge.App/Resources/Fonts/NDS12.ttf"));
SKFont Font(float size = 24) => new(typeface, size);

var outDir = Path.Combine(root, "tools/ChromePreview/out");
Directory.CreateDirectory(outDir);

RenderMenu(Path.Combine(outDir, "menu.png"));
RenderStorage(Path.Combine(outDir, "storage.png"));
RenderEditor(Path.Combine(outDir, "editor.png"));
RenderEvents(Path.Combine(outDir, "events.png"));
RenderBag(Path.Combine(outDir, "bag.png"));
Console.WriteLine($"wrote previews to {outDir}");

// ---------- mock screens (1280x675, the Thor's landscape shape) ----------

void RenderMenu(string path)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;
    c.DrawRect(All(1280, 675), new SKPaint { Color = Pksm.Housing, IsAntialias = true });
    // home cards row: the three destinations as icon tiles
    string[] cards = ["pkf_bank.png", "icon_events.png", "icon_settings.png"];
    string[] names = ["Bank", "Events", "Settings"];
    for (var i = 0; i < 3; i++)
    {
        var r = new SKRect(760 + i * 170, 60, 760 + i * 170 + 150, 148);
        using (var f = new SKPaint { Color = Pksm.Paper, IsAntialias = true })
            c.DrawRoundRect(r, 6, 6, f);
        using (var e = new SKPaint { Color = Pksm.Chrome, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 })
            c.DrawRoundRect(r, 6, 6, e);
        if (i == 0)
            using (var g = new SKPaint { Color = Pksm.FocusGold, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3 })
                c.DrawRoundRect(SKRect.Inflate(r, 3, 3), 8, 8, g);
        art.DrawScaled(c, cards[i], r.MidX - 36, r.Top + 14, 2);
        PksmPaint.CenterText(c, names[i], r.MidX, r.Bottom - 26, Font(22), Pksm.Ink, SKColors.White.WithAlpha(0x60), SKTextAlign.Center);
    }

    var win = new SKRect(360, 130, 920, 545);
    PksmPaint.Panel(c, win);
    PksmPaint.HeaderStrip(c, new SKRect(win.Left + 12, win.Top + 12, win.Right - 12, win.Top + 58), "SETTINGS", Font(24));
    var rows = new[] { ("icon_editor.png", "Edit Pokémon"), ("icon_item.png", "Items"), ("icon_storage.png", "Boxes"), ("icon_party.png", "Party"), ("icon_settings.png", "More...") };
    for (var i = 0; i < rows.Length; i++)
    {
        var row = new SKRect(win.Left + 14, win.Top + 72 + i * 56, win.Right - 14, win.Top + 128 + i * 56);
        PksmPaint.StripeRow(c, row, i == 1);
        art.DrawScaled(c, rows[i].Item1, row.Left + 14, row.MidY - 32, 2);
        PksmPaint.CenterText(c, rows[i].Item2, row.Left + 110, row.MidY, Font(24), Pksm.Ink, SKColors.White.WithAlpha(0x60));
        if (i == 1) PksmPaint.Pointer(c, new SKPoint(row.Left + 84, row.MidY - 14), 14);
    }
    PksmPaint.HintBar(c, new SKRect(0, 611, 1280, 675), [("A", "Select"), ("B", "Back")], Font(24));
    Save(s, path);
}

void RenderStorage(string path)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;
    var green = Pksm.BoxWallpapers[0];
    PksmPaint.Wallpaper(c, All(1280, 675), green);

    // grid area framed by crosshair; stack buttons live INSIDE the frame's right rail
    var grid = new SKRect(48, 100, 1000, 560);
    PksmPaint.Crosshair(c, grid);

    for (var i = 0; i < 30; i++)
    {
        var col = i % 6; var row = i / 6;
        var slot = new SKRect(grid.Left + 26 + col * 146, grid.Top + 34 + row * 82, grid.Left + 26 + col * 146 + 128, grid.Top + 34 + row * 82 + 72);
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

    // vertical blue stack inside the right rail
    string[] ops = ["VIEW", "CLEAR", "RELASE", "TOOLS", "SAVE"];
    for (var i = 0; i < ops.Length; i++)
    {
        var b = new SKRect(grid.Right - 76, 130 + i * 84, grid.Right - 8, 190 + i * 84);
        PksmPaint.StackButton(c, b, i == 0);
        PksmPaint.CenterText(c, ops[i].Replace("RELASE", "RELEASE"), b.MidX, b.MidY, Font(18), Pksm.Paper, SKColors.Black.WithAlpha(0x50), SKTextAlign.Center);
    }

    // box name bar over the grid
    PksmPaint.BoxNameBar(c, new SKRect(360, 52, 640, 90), "BOX 1", Font(22), true, true);

    // side info panel clear of the grid
    var info = new SKRect(1030, 60, 1240, 560);
    PksmPaint.Panel(c, info);
    PksmPaint.HeaderStrip(c, new SKRect(info.Left + 10, info.Top + 10, info.Right - 10, info.Top + 54), "LANDORUS", Font(22));
    string[] facts = ["#645", "Lv.55", "GROUND", "FLYING", "OT Bernardo", "ID 35053", "Adamant", "IV 31/29/31", "20/28/31"];
    for (var i = 0; i < facts.Length; i++)
        PksmPaint.CenterText(c, facts[i], info.Left + 20, info.Top + 92 + i * 44, Font(24), Pksm.Ink, SKColors.White.WithAlpha(0x60));

    PksmPaint.HintBar(c, new SKRect(0, 611, 1280, 675), [("LR", "Box"), ("A", "Select"), ("B", "Back"), ("X", "Tools"), ("START", "Menu")], Font(24));
    Save(s, path);
}

void RenderEditor(string path)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;
    c.DrawRect(All(1280, 675), Paint(Pksm.SummaryBg));

    PksmPaint.HeaderStrip(c, new SKRect(40, 26, 640, 74), "POKéMON INFORMATION", Font(24));

    // left column: attributes on white panel
    var left = new SKRect(40, 96, 620, 600);
    PksmPaint.Panel(c, left);
    string[] attrs = ["Nickname  Ampharos", "OT  PKSM", "Nature  Modest", "Ability  Static", "Item  None", "TID/SID  12345/54321", "Friendship  177"];
    for (var i = 0; i < attrs.Length; i++)
    {
        var row = new SKRect(left.Left + 10, left.Top + 16 + i * 48, left.Right - 10, left.Top + 64 + i * 48);
        PksmPaint.StripeRow(c, row, i == 2);
        PksmPaint.CenterText(c, attrs[i], row.Left + 18, row.MidY, Font(24), Pksm.Ink, SKColors.White.WithAlpha(0x50));
    }

    // STATS/MOVES/SAVE choice stack under the attributes
    for (var i = 0; i < 3; i++)
    {
        var b = new SKRect(left.Left + 20, left.Top + 372 + i * 72, left.Left + 190, left.Top + 432 + i * 72);
        PksmPaint.ChoiceButton(c, b, pressed: false, focused: i == 0);
        PksmPaint.CenterText(c, new[] { "STATS", "MOVES", "SAVE" }[i], b.MidX, b.MidY, Font(24), Pksm.Ink, SKColors.White.WithAlpha(0x60), SKTextAlign.Center);
    }

    // right column: stats panel with IV · EV · stat rows
    var right = new SKRect(660, 96, 1240, 600);
    PksmPaint.Panel(c, right);
    PksmPaint.HeaderStrip(c, new SKRect(right.Left + 10, right.Top + 10, right.Right - 10, right.Top + 54), "STATS", Font(22));
    string[] stats = ["HP        31 · 252 · 384", "Attack    30 ·   0 · 166", "Defense   31 ·   0 · 206", "Sp. Atk   31 · 252 · 361", "Sp. Def   31 ·   0 · 216", "Speed     31 ·   4 · 147"];
    for (var i = 0; i < stats.Length; i++)
        PksmPaint.CenterText(c, stats[i], right.Left + 28, right.Top + 100 + i * 48, Font(24), Pksm.Ink, SKColors.White.WithAlpha(0x50));

    PksmPaint.HintBar(c, new SKRect(0, 611, 1280, 675), [("A", "Edit"), ("B", "Back"), ("X", "Legalize")], Font(24));
    Save(s, path);
}

void RenderEvents(string path)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;
    c.DrawRect(All(1280, 675), Paint(Pksm.GiftPink));
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
        PksmPaint.CenterText(c, mrows[i], row.Left + 20, row.MidY, Font(24), Pksm.Ink, SKColors.White.WithAlpha(0x50));
    }

    string[] langs = ["JPN", "ENG", "FRE", "ITA", "GER", "SPA", "KOR", "CHS", "CHT"];
    for (var i = 0; i < langs.Length; i++)
    {
        var r = new SKRect(680 + (i % 5) * 116, 430 + (i / 5) * 56, 680 + (i % 5) * 116 + 104, 430 + (i / 5) * 56 + 44);
        PksmPaint.LangChip(c, r, i == 0, Pksm.GiftPinkLight, Pksm.GiftRed);
        PksmPaint.CenterText(c, langs[i], r.MidX, r.MidY, Font(20), Pksm.Paper, SKColors.Black.WithAlpha(0x40), SKTextAlign.Center);
    }
    PksmPaint.HintBar(c, new SKRect(0, 611, 1280, 675), [("LR", "Card"), ("A", "Receive"), ("B", "Back"), ("START", "Inject")], Font(24));
    Save(s, path);
}

void RenderBag(string path)
{
    using var s = Surface(1280, 675);
    var c = s.Canvas;
    c.DrawRect(All(1280, 675), Paint(Pksm.BagNavy));

    string[] cats = ["Items", "Key Items", "TMs", "Medicine", "Berries", "Z-Crystals", "Rotom"];
    for (var i = 0; i < cats.Length; i++)
        PksmPaint.BagPill(c, new SKRect(40, 36 + i * 84, 300, 36 + i * 84 + 64), i == 0);

    var list = new SKRect(360, 36, 1240, 588);
    c.DrawRoundRect(list, 14, 14, Paint(Pksm.BagNavyDeep));
    string[] items = ["Dive Ball x 8", "Mystic Water x 1", "Quick Ball x 5", "Nest Ball x 3", "Thunder Stone x 1", "Spell Tag x 1"];
    for (var i = 0; i < items.Length; i++)
    {
        var y = list.Top + 56 + i * 88;
        if (i == 0)
            c.DrawRoundRect(new SKRect(list.Left + 10, y - 38, list.Right - 10, y + 42), 10, 10, Paint(Pksm.BagCyan.WithAlpha(0x38)));
        PksmPaint.CountButton(c, new SKPoint(list.Left + 72, y + 2), 27, true);
        PksmPaint.CountButton(c, new SKPoint(list.Right - 72, y + 2), 27, false);
        PksmPaint.CenterText(c, items[i], list.MidX, y + 2, Font(26), Pksm.Paper, SKColors.Black.WithAlpha(0x50), SKTextAlign.Center);
    }
    PksmPaint.HintBar(c, new SKRect(0, 611, 1280, 675), [("UD", "Browse"), ("A", "Add"), ("B", "Back")], Font(24));
    Save(s, path);
}

// ---------- helpers ----------

static SKPaint Paint(SKColor c) => new() { Color = c, IsAntialias = true };
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
