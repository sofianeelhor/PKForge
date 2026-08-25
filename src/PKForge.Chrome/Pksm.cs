using SkiaSharp;

namespace PKForge.Chrome;

/// <summary>
/// The PKForge design tokens, sampled from the PKSM/DS-era reference: one palette for every
/// screen. Pages and the Chrome painters take colors from here and nowhere else.
/// </summary>
public static class Pksm
{
    // ---- Ink ----
    public static readonly SKColor Ink = new(0x3C, 0x3C, 0x3C);       // primary text
    public static readonly SKColor InkSoft = new(0x6E, 0x6E, 0x6E);   // secondary text
    public static readonly SKColor Paper = new(0xFC, 0xFC, 0xFB);     // panel white
    public static readonly SKColor PaperShade = new(0xF2, 0xEE, 0xE7);

    // ---- Warm-grey chrome (box-name bars, window borders: PKSM #D1CBC0 family) ----
    public static readonly SKColor Chrome = new(0xD1, 0xCB, 0xC0);
    public static readonly SKColor ChromeLight = new(0xE7, 0xE2, 0xDA);
    public static readonly SKColor ChromeDark = new(0xB4, 0xAD, 0xA1);

    // ---- Indigo icon set (PKSM's two-tone: #C5CAE9 on #1A237E) ----
    public static readonly SKColor IndigoLight = new(0xC5, 0xCA, 0xE9);
    public static readonly SKColor Indigo = new(0x5C, 0x6B, 0xC0);
    public static readonly SKColor IndigoDeep = new(0x28, 0x35, 0x93);
    public static readonly SKColor IndigoInk = new(0x1A, 0x23, 0x7E);

    // ---- Summary / editor (Gen 6 Pokémon Information) ----
    public static readonly SKColor SummaryBg = new(0x7F, 0xA0, 0xD2);
    public static readonly SKColor SummaryPanel = new(0xFF, 0xFF, 0xFF);
    public static readonly SKColor SummaryStripe = new(0xE8, 0xEF, 0xF9);
    public static readonly SKColor Maroon = new(0x4C, 0x12, 0x12);    // Gen-5 header red
    public static readonly SKColor MaroonDeep = new(0x39, 0x18, 0x18);

    // ---- Storage (the PC box world) ----
    public static readonly SKColor StorageGreen = new(0x99, 0xE3, 0x95);
    public static readonly SKColor StoragePanel = new(0x74, 0xCA, 0x6D);
    public static readonly SKColor StorageMenuBlue = new(0x3D, 0x74, 0xC4);  // View/Clear/... stack
    public static readonly SKColor StorageMenuBlueDeep = new(0x2A, 0x54, 0x94);

    // ---- Events (mystery-gift pink) ----
    public static readonly SKColor GiftPink = new(0xD3, 0x76, 0x6A);
    public static readonly SKColor GiftPinkLight = new(0xE0, 0x89, 0x7E);
    public static readonly SKColor GiftRed = new(0xFF, 0x4D, 0x50);

    // ---- Bag (inventory navy) ----
    public static readonly SKColor BagNavy = new(0x1B, 0x2C, 0x5D);
    public static readonly SKColor BagNavyDeep = new(0x13, 0x1F, 0x42);
    public static readonly SKColor BagCyan = new(0x35, 0xB8, 0xC8);
    public static readonly SKColor BagCyanEdge = new(0x9F, 0xD6, 0x50);      // yellow-green rim
    public static readonly SKColor BagSelected = new(0xE8, 0x9A, 0x2C);

    // ---- Buttons ----
    public static readonly SKColor ChoiceFill = new(0xF8, 0xF2, 0xD8);       // STATS/MOVES/SAVE cream
    public static readonly SKColor ChoiceFillPress = new(0xEF, 0xE3, 0xB4);
    public static readonly SKColor ChoiceRim = new(0x2E, 0x9E, 0xB8);        // cyan outline
    public static readonly SKColor ChoiceRimDeep = new(0x1D, 0x76, 0x8A);

    // ---- Signals (functional, reserved) ----
    public static readonly SKColor Legal = new(0x37, 0x9B, 0x4F);
    public static readonly SKColor Illegal = new(0xD3, 0x49, 0x3A);
    public static readonly SKColor ShinyGold = new(0xE3, 0xAE, 0x3C);
    public static readonly SKColor CursorRed = new(0xE8, 0x48, 0x3C);       // the red pointer
    public static readonly SKColor FocusGold = new(0xF2, 0xC1, 0x4E);        // selection frame

    /// <summary>Per-box wallpapers in the storage world: PKSM-style saturated flats, cycling.</summary>
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
    public static SKColor InkOver(SKColor wallpaper)
    {
        var lum = (0.299f * wallpaper.Red + 0.587f * wallpaper.Green + 0.114f * wallpaper.Blue) / 255f;
        return lum >= 0.6f ? new SKColor(0x33, 0x4A, 0x2E) : SKColors.White;
    }
}
