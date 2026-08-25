using CommunityToolkit.Mvvm.ComponentModel;
using PKForge.Chrome;
using SkiaSharp;

namespace PKForge.App.Theme;

/// <summary>
/// MAUI-facing design tokens. The single source of truth for colors is <see cref="Pksm"/>
/// (PKForge.Chrome); this maps those SKColors to MAUI Colors so views never hardcode values.
/// Design language: the PKSM/DS-era storage world — white panels with warm-grey borders,
/// maroon Gen-5 header strips, saturated per-box wallpapers, indigo icon set.
/// </summary>
public static class UiTokens
{
    private static Color As(SKColor c) => Color.FromRgb(c.Red, c.Green, c.Blue);

    // ---- Surfaces ----
    public static readonly Color Paper = As(Pksm.Paper);              // panel white
    public static readonly Color PaperShade = As(Pksm.PaperShade);
    public static readonly Color Shell = Paper;                        // legacy name kept for views
    public static readonly Color ShellEdge = As(Pksm.Chrome);          // warm-grey panel border
    public static readonly Color ShellPress = As(Pksm.ChromeLight);
    public static readonly Color Housing = As(Pksm.Housing);           // warm page backdrop (never blue)

    // Legacy names still referenced by views mid-migration to the PKSM language; do not add uses.
    public static readonly Color LcdBg = As(Pksm.PaperShade);
    public static readonly Color LcdText = As(Pksm.Ink);
    public static readonly Color LcdFrame = As(Pksm.Chrome);
    public static readonly Color Navy0 = As(Pksm.Paper);
    public static readonly Color Navy1 = As(Pksm.Ink);
    public static readonly Color Blueprint = As(Pksm.Indigo);

    // ---- Ink ----
    public static readonly Color Ink0 = As(Pksm.Ink);
    public static readonly Color Ink1 = As(Pksm.InkSoft);

    // ---- Chrome accents ----
    public static readonly Color Maroon = As(Pksm.Maroon);
    public static readonly Color MaroonDeep = As(Pksm.MaroonDeep);
    public static readonly Color Indigo = As(Pksm.Indigo);
    public static readonly Color IndigoLight = As(Pksm.IndigoLight);
    public static readonly Color IndigoInk = As(Pksm.IndigoInk);
    public static readonly Color MenuBlue = As(Pksm.StorageMenuBlue);
    public static readonly Color MenuBlueDeep = As(Pksm.StorageMenuBlueDeep);

    // ---- Button language ----
    public static readonly Color ChoiceFill = As(Pksm.ChoiceFill);
    public static readonly Color ChoiceFillPress = As(Pksm.ChoiceFillPress);
    public static readonly Color ChoiceRim = As(Pksm.ChoiceRim);
    public static readonly Color ChoiceRimDeep = As(Pksm.ChoiceRimDeep);
    public static readonly Color Cyan = As(Pksm.ChoiceRim);
    public static readonly Color Blue = As(Pksm.StorageMenuBlue);

    // ---- Worlds ----
    public static readonly Color SummaryBg = As(Pksm.SummaryBg);
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
    public static readonly Color Gold = As(Pksm.ShinyGold);
    public static readonly Color RedOrange = As(Pksm.Illegal);
    public static readonly Color Ok = Green;
    public static readonly Color Warn = As(Pksm.ShinyGold);
    public static readonly Color Bad = As(Pksm.Illegal);
    public static readonly Color DefaultAccent = Cyan;

    public static readonly Color Scrim = Color.FromArgb("#883F4954");

    // ---- Skia twins for the grid renderers ----
    public static readonly SKColor SkPaper = Pksm.Paper;
    public static readonly SKColor SkChrome = Pksm.Chrome;
    public static readonly SKColor SkInk = Pksm.Ink;
    public static readonly SKColor SkMenuBlue = Pksm.StorageMenuBlue;
    public static readonly SKColor SkFocusGold = Pksm.FocusGold;
    public static readonly SKColor SkShinyGold = Pksm.ShinyGold;
    public static readonly SKColor SkCursorRed = Pksm.CursorRed;
    public static readonly SKColor SkDefaultAccent = Pksm.ChoiceRim;
    public static readonly SKColor SkLcdBg = Pksm.PaperShade;
    public static readonly SKColor SkLcdText = Pksm.Ink;
    public static readonly SKColor SkLcdTileEdge = Pksm.Chrome;
    public static readonly SKColor SkEmptyMark = Pksm.ChromeLight;

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
