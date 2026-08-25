using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The PKForge design tokens: the PKSM language — a cool light grey world, white panels
/// with soft borders, PKSM-blue buttons wearing white rims, indigo selection, the red
/// triangle cursor. One system:
/// - housing is a light cool grey (never warm cream, never dark);
/// - panels and cards are white with soft grey borders;
/// - the ONE accent family is blue (headers, buttons, focus); indigo selection kin to the icons;
/// - red is reserved for cursors/destructive, gold only ever marks a shiny mon;
/// - per-screen worlds (storage wallpaper, summary blue, dex cyan, gift pink, bag navy)
///   stay saturated like the games, with white cards on top.
/// </summary>
public static class Pksm
{
    // ---- Housing (cool light grey, the 3DS/PKSM shell) ----
    public static readonly SKColor Housing = new(0xDC, 0xE3, 0xEB);
    public static readonly SKColor HousingLine = new(0xC9, 0xD3, 0xDD);   // the faint grid
    public static readonly SKColor HousingDot = new(0xB9, 0xC6, 0xD3);

    // ---- Panels ----
    public static readonly SKColor Paper = new(0xFC, 0xFD, 0xFE);
    public static readonly SKColor PaperShade = new(0xEF, 0xF2, 0xF6);
    public static readonly SKColor PaperEdge = new(0xC4, 0xCD, 0xD7);     // soft grey border
    public static readonly SKColor PaperEdgeDeep = new(0xA8, 0xB4, 0xC2);

    // ---- The blue accent family (PKSM's button blue) ----
    public static readonly SKColor HeaderBlue = new(0x2E, 0x5A, 0x94);    // header strips
    public static readonly SKColor ButtonBlueLight = new(0x6D, 0x9B, 0xE8);
    public static readonly SKColor ButtonBlue = new(0x3D, 0x74, 0xC4);    // button fill
    public static readonly SKColor ButtonBlueDeep = new(0x2A, 0x54, 0x94);

    // ---- Selection (kin to the icon set) ----
    public static readonly SKColor SelectFill = new(0xC5, 0xCA, 0xE9);    // indigo light
    public static readonly SKColor SelectBorder = new(0x5C, 0x6B, 0xC0);  // indigo
    public static readonly SKColor SelectInk = new(0x1A, 0x23, 0x7E);     // text on selection

    // ---- Ink ----
    public static readonly SKColor Ink = new(0x3C, 0x46, 0x52);
    public static readonly SKColor InkSoft = new(0x74, 0x81, 0x90);

    // ---- Icon set ----
    public static readonly SKColor IndigoLight = new(0xC5, 0xCA, 0xE9);
    public static readonly SKColor Indigo = new(0x5C, 0x6B, 0xC0);
    public static readonly SKColor IndigoDeep = new(0x28, 0x35, 0x93);
    public static readonly SKColor IndigoInk = new(0x1A, 0x23, 0x7E);

    // ---- Worlds (per-screen, from the games) ----
    public static readonly SKColor SummaryBg = new(0x7F, 0xA0, 0xD2);
    public static readonly SKColor SummaryPanel = new(0xFF, 0xFF, 0xFF);
    public static readonly SKColor SummaryStripe = new(0xE8, 0xEF, 0xF9);
    public static readonly SKColor DexCyan = new(0x07, 0xA5, 0xB8);
    public static readonly SKColor DexGrid = new(0x14, 0x98, 0xEB);
    public static readonly SKColor StorageGreen = new(0x99, 0xE3, 0x95);
    public static readonly SKColor StoragePanel = new(0x74, 0xCA, 0x6D);
    public static readonly SKColor StorageMenuBlue = new(0x3D, 0x74, 0xC4);
    public static readonly SKColor StorageMenuBlueDeep = new(0x2A, 0x54, 0x94);
    public static readonly SKColor RecessBlue = new(0x3F, 0x9D, 0xDF);

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
    public static readonly SKColor CursorRed = new(0xE8, 0x48, 0x3C);     // pointer + destructive
    public static readonly SKColor FocusBlue = SelectBorder;

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
