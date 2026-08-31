using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The PKForge design tokens: the PKSM language rebuilt inside the new logo's dark
/// pixel-console world. Navy grid fields, layered cobalt panels, cyan focus light, the red
/// triangle cursor. One system:
/// - housing is the logo's dark grid field;
/// - panels and cards are layered navy with cobalt edges;
/// - the ONE accent family comes directly from the PKForge logo (navy, cobalt, cyan);
/// - red is reserved for cursors/destructive, gold only ever marks a shiny mon;
/// - per-screen worlds (storage wallpaper, summary blue, dex cyan, gift plum, bag navy)
///   stay recognizable while sharing the same dark chrome and pale ink.
/// </summary>
public static class Pksm
{
    // ---- Raw brand colors (the five opaque colors in pkforge.png) ----
    public static readonly SKColor LogoVoid = new(0x14, 0x12, 0x1D);
    public static readonly SKColor LogoDeep = new(0x17, 0x1B, 0x32);
    public static readonly SKColor LogoDeck = new(0x1B, 0x24, 0x47);
    public static readonly SKColor LogoGrid = new(0x2B, 0x4E, 0x95);
    public static readonly SKColor LogoBlue = new(0x27, 0x89, 0xCD);
    public static readonly SKColor LogoCyan = new(0x42, 0xBF, 0xE8);

    // ---- Housing (the logo's pixel-grid field) ----
    public static readonly SKColor Housing = LogoDeck;
    public static readonly SKColor HousingLine = LogoGrid;
    public static readonly SKColor HousingDot = LogoCyan;

    // ---- Panels ----
    public static readonly SKColor Paper = LogoDeck;
    public static readonly SKColor PaperShade = LogoDeep;
    public static readonly SKColor PaperEdge = LogoGrid;
    public static readonly SKColor PaperEdgeDeep = LogoCyan;

    // ---- The logo accent family (sampled from pkforge.png) ----
    public static readonly SKColor HeaderBlue = LogoGrid;
    public static readonly SKColor ButtonBlueLight = LogoCyan;
    public static readonly SKColor ButtonBlue = LogoBlue;
    public static readonly SKColor ButtonBlueDeep = LogoVoid;

    // ---- Selection (kin to the icon set) ----
    public static readonly SKColor SelectFill = new(0x2B, 0x4E, 0x95);    // cobalt highlight
    public static readonly SKColor SelectBorder = new(0x42, 0xBF, 0xE8);  // cyan focus edge
    public static readonly SKColor SelectInk = new(0xF4, 0xF8, 0xFF);     // text on selection

    // ---- Ink ----
    public static readonly SKColor Ink = new(0xF4, 0xF8, 0xFF);
    public static readonly SKColor InkSoft = new(0xA8, 0xBA, 0xDC);

    // ---- Icon set ----
    public static readonly SKColor IndigoLight = new(0x27, 0x89, 0xCD);
    public static readonly SKColor Indigo = new(0x42, 0xBF, 0xE8);
    public static readonly SKColor IndigoDeep = new(0x2B, 0x4E, 0x95);
    public static readonly SKColor IndigoInk = new(0xF4, 0xF8, 0xFF);

    // ---- Worlds (per-screen, from the games) ----
    public static readonly SKColor SummaryBg = new(0x17, 0x1B, 0x32);
    public static readonly SKColor SummaryPanel = new(0x1B, 0x24, 0x47);
    public static readonly SKColor SummaryStripe = new(0x2B, 0x4E, 0x95);
    public static readonly SKColor DexCyan = new(0x27, 0x89, 0xCD);
    public static readonly SKColor DexGrid = new(0x42, 0xBF, 0xE8);
    public static readonly SKColor StorageGreen = new(0x1B, 0x31, 0x46);
    public static readonly SKColor StoragePanel = new(0x1E, 0x46, 0x59);
    public static readonly SKColor StorageMenuBlue = LogoGrid;
    public static readonly SKColor StorageMenuBlueDeep = LogoVoid;
    public static readonly SKColor RecessBlue = new(0x2B, 0x4E, 0x95);

    // ---- Events (mystery-gift pink) ----
    public static readonly SKColor GiftPink = new(0x45, 0x24, 0x46);
    public static readonly SKColor GiftPinkLight = new(0x8E, 0x45, 0x70);
    public static readonly SKColor GiftRed = new(0xE5, 0x68, 0x86);

    // ---- Bag (inventory navy) ----
    public static readonly SKColor BagNavy = new(0x1B, 0x24, 0x47);
    public static readonly SKColor BagNavyDeep = new(0x14, 0x12, 0x1D);
    public static readonly SKColor BagCyan = new(0x27, 0x89, 0xCD);
    public static readonly SKColor BagCyanEdge = new(0x42, 0xBF, 0xE8);
    public static readonly SKColor BagSelected = new(0x2B, 0x4E, 0x95);

    // ---- Reserved signals ----
    public static readonly SKColor Legal = new(0x54, 0xD6, 0x8A);
    public static readonly SKColor Illegal = new(0xF0, 0x68, 0x68);
    public static readonly SKColor ShinyGold = new(0xF2, 0xC1, 0x4E);     // ONLY the shiny mark
    public static readonly SKColor CursorRed = new(0xF0, 0x68, 0x68);     // pointer + destructive
    public static readonly SKColor FocusBlue = SelectBorder;

    /// <summary>Per-box wallpapers in the storage world: dark tinted worlds, cycling.</summary>
    public static readonly SKColor[] BoxWallpapers =
    [
        new(0x1B, 0x31, 0x46), // jade navy
        new(0x1B, 0x2E, 0x58), // cobalt navy
        new(0x3B, 0x32, 0x38), // amber dusk
        new(0x42, 0x28, 0x3C), // coral dusk
        new(0x31, 0x29, 0x55), // violet navy
        new(0x18, 0x3A, 0x4A), // aqua navy
        new(0x40, 0x28, 0x4D), // rose navy
        new(0x2A, 0x3B, 0x38), // leaf navy
    ];

    public static SKColor WallpaperShade(SKColor c)
    {
        var r = Math.Min(255, (int)(c.Red * 1.24f));
        var g = Math.Min(255, (int)(c.Green * 1.24f));
        var b = Math.Min(255, (int)(c.Blue * 1.24f));
        return new SKColor((byte)r, (byte)g, (byte)b);
    }

    /// <summary>Dark readable ink over a wallpaper (edge labels on colored worlds).</summary>
    public static SKColor InkOver(SKColor wallpaper)
    {
        var lum = (0.299f * wallpaper.Red + 0.587f * wallpaper.Green + 0.114f * wallpaper.Blue) / 255f;
        return lum >= 0.62f ? new SKColor(0x14, 0x12, 0x1D) : Ink;
    }
}
