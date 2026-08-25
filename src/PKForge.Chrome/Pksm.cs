using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The PKForge design tokens, sampled from the Gen-5 Black/White system menu and the
/// era's game screens. One system, no temperature mixing:
/// - chrome is DARK: grey grid housing, glossy black buttons, near-black strips, white text;
/// - selection is ONE language: navy fill with a light-blue border (the B/W menu's own);
/// - red is reserved for cursors and the X close; gold only ever marks a shiny mon;
/// - per-screen worlds (storage wallpaper, dex cyan, gift pink, bag navy) sit on top.
/// Pages and painters take colors from here and nowhere else.
/// </summary>
public static class Pksm
{
    // ---- Dark chrome (the Gen-5 system-menu base) ----
    public static readonly SKColor Housing = new(0x31, 0x31, 0x31);       // app bg
    public static readonly SKColor HousingLine = new(0x42, 0x42, 0x42);   // the faint grid
    public static readonly SKColor Chrome = new(0x4A, 0x4A, 0x4A);        // panel borders on dark
    public static readonly SKColor ChromeLight = new(0x5A, 0x5A, 0x5A);
    public static readonly SKColor ChromeDark = new(0x2A, 0x2A, 0x2A);
    public static readonly SKColor Strip = new(0x18, 0x18, 0x18);         // status/header strips
    public static readonly SKColor Panel = new(0x10, 0x10, 0x10);         // glossy black button body
    public static readonly SKColor PanelEdge = new(0x29, 0x29, 0x29);

    // ---- The one selection language (B/W menu selected button) ----
    public static readonly SKColor SelectFill = new(0x10, 0x39, 0x52);
    public static readonly SKColor SelectMid = new(0x21, 0x5A, 0x7B);
    public static readonly SKColor SelectBorder = new(0x84, 0xB5, 0xFF);

    // ---- Content cards on colored worlds (games put white cards on saturated fields) ----
    public static readonly SKColor Paper = new(0xFC, 0xFC, 0xFB);
    public static readonly SKColor PaperShade = new(0xEC, 0xEC, 0xE8);
    public static readonly SKColor PaperEdge = new(0xB4, 0xB4, 0xB4);     // neutral border on white

    // ---- Ink ----
    public static readonly SKColor Ink = new(0x3C, 0x3C, 0x3C);           // text on white cards
    public static readonly SKColor InkSoft = new(0x6E, 0x6E, 0x6E);

    // ---- Icon set ----
    public static readonly SKColor IndigoLight = new(0xC5, 0xCA, 0xE9);   // PKSM periwinkle (on dark)
    public static readonly SKColor Indigo = new(0x5C, 0x6B, 0xC0);
    public static readonly SKColor IndigoDeep = new(0x28, 0x35, 0x93);
    public static readonly SKColor IndigoInk = new(0x1A, 0x23, 0x7E);     // icon tint on white cards

    // ---- Worlds (per-screen, from the games) ----
    public static readonly SKColor SummaryBg = new(0x7F, 0xA0, 0xD2);     // Gen-6 summary blue
    public static readonly SKColor SummaryPanel = new(0xFF, 0xFF, 0xFF);
    public static readonly SKColor SummaryStripe = new(0xE8, 0xEF, 0xF9);
    public static readonly SKColor DexCyan = new(0x07, 0xA5, 0xB8);       // Kalos dex panel
    public static readonly SKColor DexGrid = new(0x14, 0x98, 0xEB);       // Kalos dex grid
    public static readonly SKColor StorageGreen = new(0x99, 0xE3, 0x95);
    public static readonly SKColor StoragePanel = new(0x74, 0xCA, 0x6D);
    public static readonly SKColor StorageMenuBlue = new(0x3D, 0x74, 0xC4);
    public static readonly SKColor StorageMenuBlueDeep = new(0x2A, 0x54, 0x94);
    public static readonly SKColor RecessBlue = new(0x3F, 0x9D, 0xDF);    // Gen-5 PC box right panel

    // ---- Events (mystery-gift pink) ----
    public static readonly SKColor GiftPink = new(0xD3, 0x76, 0x6A);
    public static readonly SKColor GiftPinkLight = new(0xE0, 0x89, 0x7E);
    public static readonly SKColor GiftRed = new(0xFF, 0x4D, 0x50);

    // ---- Bag (inventory navy) ----
    public static readonly SKColor BagNavy = new(0x1B, 0x2C, 0x5D);
    public static readonly SKColor BagNavyDeep = new(0x13, 0x1F, 0x42);
    public static readonly SKColor BagCyan = new(0x35, 0xB8, 0xC8);
    public static readonly SKColor BagCyanEdge = new(0x9F, 0xD6, 0x50);
    public static readonly SKColor BagSelected = new(0xE8, 0x9A, 0x2C);

    // ---- Reserved signals ----
    public static readonly SKColor Legal = new(0x37, 0x9B, 0x4F);
    public static readonly SKColor Illegal = new(0xD3, 0x49, 0x3A);
    public static readonly SKColor ShinyGold = new(0xE3, 0xAE, 0x3C);     // ONLY the shiny mark
    public static readonly SKColor CursorRed = new(0xE8, 0x48, 0x3C);     // pointer + X close
    public static readonly SKColor FocusBlue = SelectBorder;              // selection frames

    /// <summary>Per-box wallpapers in the storage world: saturated flats, cycling.</summary>
    public static readonly SKColor[] BoxWallpapers =
    [
        new(0x99, 0xE3, 0x95), // green
        new(0x8F, 0xC8, 0xEE), // blue
        new(0xF2, 0xC7, 0x7E), // sand
        new(0xF4, 0xA8, 0xA0), // coral
        new(0xC7, 0xA8, 0xE8), // lilac
        new(0xA8, 0xE0, 0xD4), // mint
        new(0xEE, 0xB6, 0xD4), // rose
        new(0xB8, 0xD8, 0xA8), // leaf
    ];

    public static SKColor WallpaperShade(SKColor c)
    {
        var r = Math.Max(0, (int)(c.Red * 0.82f));
        var g = Math.Max(0, (int)(c.Green * 0.82f));
        var b = Math.Max(0, (int)(c.Blue * 0.82f));
        return new SKColor((byte)r, (byte)g, (byte)b);
    }

    /// <summary>Dark readable ink over a wallpaper (edge labels on colored worlds).</summary>
    public static SKColor InkOver(SKColor wallpaper)
    {
        var lum = (0.299f * wallpaper.Red + 0.587f * wallpaper.Green + 0.114f * wallpaper.Blue) / 255f;
        return lum >= 0.6f ? new SKColor(0x33, 0x4A, 0x2E) : SKColors.White;
    }
}
