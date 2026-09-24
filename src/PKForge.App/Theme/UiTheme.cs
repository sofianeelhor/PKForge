using CommunityToolkit.Mvvm.ComponentModel;
using PKForge.Chrome;
using SkiaSharp;

namespace PKForge.App.Theme;

/// <summary>
/// MAUI-facing design tokens. The single source of truth for colors is <see cref="Pksm"/>
/// (PKForge.Chrome); this maps those SKColors to MAUI Colors so views never hardcode values.
/// Design language: the PKSM/DS-era storage world rebuilt in the logo's dark pixel grid,
/// with layered navy panels, cobalt structure, cyan focus light, and adaptive pale ink.
/// </summary>
public static class UiTokens
{
    private static Color As(SKColor c) => Color.FromRgb(c.Red, c.Green, c.Blue);

    // ---- Surfaces ----
    public static readonly Color Paper = As(Pksm.Paper);              // white content cards on worlds
    public static readonly Color PaperShade = As(Pksm.PaperShade);
    public static readonly Color Shell = As(Pksm.Paper);               // white chrome windows
    public static readonly Color ShellEdge = As(Pksm.PaperEdge);        // soft grey border
    public static readonly Color ShellPress = As(Pksm.PaperShade);
    public static readonly Color Housing = As(Pksm.Housing);           // the grey grid page backdrop

    // Legacy names still referenced by views; do not add uses.
    public static readonly Color LcdBg = As(Pksm.PaperShade);
    public static readonly Color LcdText = As(Pksm.Ink);
    public static readonly Color LcdFrame = As(Pksm.PaperEdge);
    public static readonly Color Navy0 = As(Pksm.Paper);
    public static readonly Color Navy1 = As(Pksm.Ink);
    public static readonly Color Blueprint = As(Pksm.SelectBorder);

    // ---- Ink ----
    public static readonly Color Ink0 = As(Pksm.Ink);
    public static readonly Color Ink1 = As(Pksm.InkSoft);
    public static readonly Color InkSoft = As(Pksm.InkSoft);
    public static readonly Color SelectInk = As(Pksm.SelectInk);
    public static readonly Color SelectBorder = As(Pksm.SelectBorder);
    public static readonly Color SelectFill = As(Pksm.SelectFill);

    // ---- Chrome accents ----
    public static readonly Color Maroon = As(Pksm.HeaderBlue);       // legacy name: logo-navy header strips
    public static readonly Color MaroonDeep = As(Pksm.ButtonBlueDeep);
    public static readonly Color Indigo = As(Pksm.Indigo);
    public static readonly Color IndigoLight = As(Pksm.IndigoLight);
    public static readonly Color IndigoInk = As(Pksm.IndigoInk);
    public static readonly Color MenuBlue = As(Pksm.StorageMenuBlue);
    public static readonly Color MenuBlueDeep = As(Pksm.StorageMenuBlueDeep);

    // ---- Button language ----
    public static readonly Color ChoiceFill = As(Pksm.Paper);
    public static readonly Color ChoiceFillPress = As(Pksm.SelectFill);
    public static readonly Color ChoiceRim = As(Pksm.ButtonBlue);
    public static readonly Color ChoiceRimDeep = As(Pksm.ButtonBlueDeep);
    public static readonly Color Cyan = As(Pksm.ButtonBlue);
    public static readonly Color Blue = As(Pksm.StorageMenuBlue);

    // ---- Worlds ----
    public static readonly Color SummaryBg = As(Pksm.SummaryBg);
    public static readonly Color RibbonGold = As(Pksm.RibbonGold);
    public static readonly Color GiftPink = As(Pksm.GiftPink);
    public static readonly Color GiftPinkLight = As(Pksm.GiftPinkLight);
    public static readonly Color GiftRed = As(Pksm.GiftRed);
    public static readonly Color BagNavy = As(Pksm.BagNavy);
    public static readonly Color BagNavyDeep = As(Pksm.BagNavyDeep);
    public static readonly Color BagCyan = As(Pksm.BagCyan);
    public static readonly Color BagCyanEdge = As(Pksm.BagCyanEdge);

    // ---- Signal (functional, reserved) ----
    public static readonly Color Green = As(Pksm.Legal);
    public static readonly Color Yellow = As(Pksm.ShinyGold);
    public static readonly Color Gold = As(Pksm.ShinyGold);   // shiny mark only
    public static readonly Color RedOrange = As(Pksm.Illegal);
    public static readonly Color Ok = Green;
    public static readonly Color Warn = As(Pksm.ShinyGold);
    public static readonly Color Bad = As(Pksm.Illegal);
    public static readonly Color DefaultAccent = Cyan;

    public static readonly Color WorldText = As(Pksm.Ink);
    public static readonly Color WorldTextMuted = As(Pksm.InkSoft);
    public static readonly Color OnAccent = As(Pksm.LogoVoid);
    public static readonly Color Scrim = Color.FromArgb("#CC14121D");

    // ---- Skia twins for the grid renderers ----
    public static readonly SKColor SkPaper = Pksm.Paper;
    public static readonly SKColor SkChrome = Pksm.PaperEdge;
    public static readonly SKColor SkInk = Pksm.Ink;
    public static readonly SKColor SkMenuBlue = Pksm.StorageMenuBlue;
    public static readonly SKColor SkFocusGold = Pksm.FocusBlue;
    public static readonly SKColor SkShinyGold = Pksm.ShinyGold;
    public static readonly SKColor SkCursorRed = Pksm.CursorRed;
    public static readonly SKColor SkDefaultAccent = Pksm.SelectBorder;
    public static readonly SKColor SkLcdBg = Pksm.PaperShade;
    public static readonly SKColor SkLcdText = Pksm.Ink;
    public static readonly SKColor SkLcdTileEdge = Pksm.PaperEdge;
    public static readonly SKColor SkEmptyMark = Pksm.PaperShade;

    // ---- The approved summary language, as tokens (one source for every screen) ----

    // Type scale (dp). Nothing readable goes under TextSmall; no tracking, no forced caps.
    public const double TextSmall = 12.5;   // captions, detail lines, secondary facts
    public const double TextBody = 13.5;    // body copy, row values, buttons
    public const double TextLabel = 14;     // row labels, hint labels
    public const double TextTitle = 15;     // header strips, menu rows
    public const double TextHeading = 17;   // screen / hero names

    // Spacing rhythm (dp): 4 / 8 / 12 / 16 - pick by relationship, not by habit.
    public const double Space1 = 4;
    public const double Space2 = 8;
    public const double Space3 = 12;
    public const double Space4 = 16;

    // Shape.
    public const double PanelRadius = 6;    // device panels
    public const double ControlRadius = 4;  // strips, rows, tabs, buttons
    public const double PanelEdge = 2;      // the cobalt bezel
    public const double ControlEdge = 1.5;  // menu-button edge

    public static readonly Color PanelTop = As(PksmPaint.Lighter(Pksm.Paper, 0.05f));       // faint top light
    public static readonly Color PanelShadow = As(Pksm.LogoVoid);                            // hard pixel drop
    public static readonly Color StripTop = As(PksmPaint.Lighter(Pksm.HeaderBlue, 0.16f));
    public static readonly Color StripBottom = As(PksmPaint.Darker(Pksm.HeaderBlue, 0.1f));
    public static readonly Color ButtonTop = As(PksmPaint.Lighter(Pksm.LogoDeck, 0.04f));
    public static readonly Color ButtonBottom = As(PksmPaint.Darker(Pksm.LogoDeck, 0.05f));
    public static readonly Color ButtonEdge = As(Pksm.LogoGrid);                             // cobalt
    public static readonly Color Outline = As(Pksm.ButtonBlueDeep);                          // void outline
    public static readonly Color Rim = Color.FromRgba(0xF4, 0xF8, 0xFF, 0x70);                // the pale focus rim
    public static readonly Color RowStripe = Color.FromRgba(Pksm.PaperShade.Red, Pksm.PaperShade.Green, Pksm.PaperShade.Blue, (byte)0x90);
    public static readonly Color Divider = Color.FromRgba(Pksm.PaperEdge.Red, Pksm.PaperEdge.Green, Pksm.PaperEdge.Blue, (byte)0x70);
    public static readonly Color Well = As(Pksm.PaperShade);                                 // recessed field / inset

    // Toned per-context accents (the summary bands): quiet surfaces, never neon edges.
    public static readonly Color AccentInfo = As(Pksm.BandInfo);
    public static readonly Color AccentStats = As(Pksm.BandStats);
    public static readonly Color AccentMoves = As(Pksm.BandMoves);
    public static readonly Color AccentOrigin = As(Pksm.BandOrigin);
    public static readonly Color AccentLegal = As(Pksm.BandLegal);
    public static readonly Color AccentDanger = As(PksmPaint.Tone(Pksm.Illegal));
    public static readonly Color AccentGift = As(PksmPaint.Tone(Pksm.GiftRed));
    public static readonly Color AccentNeutral = As(Pksm.HeaderBlue);

    /// <summary>A signal colour toned into the navy world (for filled accent surfaces).</summary>
    public static Color Tone(Color signal)
    {
        var sk = PksmPaint.Tone(new SKColor((byte)(signal.Red * 255), (byte)(signal.Green * 255), (byte)(signal.Blue * 255)));
        return As(sk);
    }

    /// <summary>Readable text colour for a signal used AS text on navy (chips become plain coloured text).</summary>
    public static Color TextTone(Color signal) =>
        signal.GetLuminosity() < 0.62f ? signal.WithLuminosity(0.66f) : signal;

    /// <summary>MAUI color for a box wallpaper index (cycled like the games' PC boxes).</summary>
    public static Color Wallpaper(int boxIndex) => As(Pksm.BoxWallpapers[((boxIndex % Pksm.BoxWallpapers.Length) + Pksm.BoxWallpapers.Length) % Pksm.BoxWallpapers.Length]);
}

/// <summary>The 18 Pokémon type colors (PKHeX type IDs 0–17), adaptive-theme source.</summary>
public static class TypePalette
{
    private static readonly string[] Colors =
    [
        "#A8A77A", "#C22E28", "#A98FF3", "#A33EA1", "#E2BF65", "#B6A136",
        "#A6B91A", "#735797", "#B7B7CE", "#EE8130", "#6390F0", "#7AC74C",
        "#F7D02C", "#F95587", "#96D9D6", "#6F35FC", "#705746", "#D685AD",
    ];

    public static Color ForType(int typeId) =>
        (uint)typeId < (uint)Colors.Length ? Color.FromArgb(Colors[typeId]) : UiTokens.DefaultAccent;

    public static Color ForegroundForType(int typeId)
    {
        var color = ForType(typeId);
        var luminance = 0.299f * color.Red + 0.587f * color.Green + 0.114f * color.Blue;
        return luminance >= 0.58f ? UiTokens.OnAccent : Microsoft.Maui.Graphics.Colors.White;
    }
}

/// <summary>Owns the adaptive accent derived from the selected Pokémon's type(s).</summary>
public partial class ThemeService : ObservableObject
{
    [ObservableProperty] private Color _accent = UiTokens.DefaultAccent;
    [ObservableProperty] private SKColor _skAccent = UiTokens.SkDefaultAccent;

    public void ApplyTypes(IReadOnlyList<int>? types)
    {
        var baseColor = types is { Count: > 0 } ? TypePalette.ForType(types[0]) : UiTokens.DefaultAccent;
        var adjusted = WithSaturation(baseColor, 0.85f);
        Accent = adjusted;
        SkAccent = new SKColor(
            (byte)(adjusted.Red * 255), (byte)(adjusted.Green * 255), (byte)(adjusted.Blue * 255));
    }

    /// <summary>Scales HSL saturation so theme color never competes with legality status colors.</summary>
    private static Color WithSaturation(Color color, float factor)
    {
        var r = color.Red; var g = color.Green; var b = color.Blue;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2f;
        if (Math.Abs(max - min) < 1e-6f) return color;
        var saturation = lightness < 0.5f
            ? (max - min) / (max + min)
            : (max - min) / (2f - max - min);
        saturation = Math.Clamp(saturation * factor, 0f, 1f);

        var hue = GetHue(r, g, b, max, min);
        var q = lightness < 0.5f
            ? lightness * (1f + saturation)
            : lightness + saturation - lightness * saturation;
        var p = 2f * lightness - q;
        return Color.FromRgb(
            HueToRgb(p, q, hue + 1f / 3f),
            HueToRgb(p, q, hue),
            HueToRgb(p, q, hue - 1f / 3f));
    }

    private static float GetHue(float r, float g, float b, float max, float min)
    {
        var delta = max - min;
        float hue;
        if (Math.Abs(max - r) < 1e-6f) hue = (g - b) / delta % 6f;
        else if (Math.Abs(max - g) < 1e-6f) hue = (b - r) / delta + 2f;
        else hue = (r - g) / delta + 4f;
        hue /= 6f;
        return hue < 0f ? hue + 1f : hue;
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0f) t += 1f;
        if (t > 1f) t -= 1f;
        if (t < 1f / 6f) return p + (q - p) * 6f * t;
        if (t < 1f / 2f) return q;
        if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
        return p;
    }
}

/// <summary>Maps legality badge glyphs to status colors (green/red are status, never decoration).</summary>
public sealed class LegalityColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        (value as string) switch
        {
            "✓" => UiTokens.Ok,
            "✗" => UiTokens.Bad,
            _ => UiTokens.Ink1,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}
