using PKForge.Chrome;
using SkiaSharp;

// Renders the Gen-5 chrome to PNG previews so the design can be iterated off-device
// against the reference screenshots. Usage: dotnet run --project tools/ChromePreview
var root = FindRepoRoot();
var art = new PksmArt();
foreach (var f in Directory.GetFiles(Path.Combine(root, "src/PKForge.App/Resources/UI/pksm"), "*.png"))
    art.Supply(Path.GetFileName(f), File.ReadAllBytes(f));

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

/// <summary>The B/W housing: dark grey with the faint grid.</summary>
static void Grid(SKCanvas c, SKRect r)
{
    using var bg = new SKPaint { Color = Pksm.Housing };
    c.DrawRect(r, bg);
    using var line = new SKPaint { Color = Pksm.HousingLine, StrokeWidth = 1 };
    for (float x = 0; x < r.Width; x += 26) c.DrawLine(x, 0, x, r.Height, line);
    for (float y = 0; y < r.Height; y += 26) c.DrawLine(0, y, r.Width, y, line);
}

void StatusStrip(SKCanvas c, SKRect bar)
{
    using var p = new SKPaint { Color = Pksm.Strip };
    c.DrawRect(bar, p);
    PksmPaint.CenterText(c, "PKForge", bar.Left + 24, bar.MidY, Font(22), SKColors.White, SKColors.Black.WithAlpha(0x50));
    PksmPaint.CenterText(c, "OFFLINE", bar.Right - 130, bar.MidY, Font(18), new SKColor(0x8A, 0x95, 0xA0), SKColors.Black.WithAlpha(0x40));
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
