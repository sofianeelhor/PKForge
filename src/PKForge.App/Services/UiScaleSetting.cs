namespace PKForge.App.Services;

/// <summary>
/// How big the interface draws, for phones, foldables and odd screens. Auto keeps the
/// Thor exactly as designed and only widens the painted screens on a screen wider than 16:9
/// (a 20:9 phone), so their bottom is not cut. A percentage also scales every MAUI layout
/// (applied when PKForge starts) and the painted screens (applied at once).
/// </summary>
public static class UiScaleSetting
{
    private const string Key = "ui_scale";

    /// <summary>The choices in order; 0 is Auto.</summary>
    public static readonly int[] Choices = [0, 90, 100, 115, 130];

    /// <summary>The chosen percentage, or 0 for Auto.</summary>
    public static int Percent
    {
        get
        {
            var value = Preferences.Default.Get(Key, 0);
            return Array.IndexOf(Choices, value) >= 0 ? value : 0;
        }
        set => Preferences.Default.Set(Key, Array.IndexOf(Choices, value) >= 0 ? value : 0);
    }

    /// <summary>The scale the setting asks for: 1 at Auto.</summary>
    public static double Scale => Percent == 0 ? 1.0 : Percent / 100.0;

    /// <summary>The factor applied to Android's density for the MAUI layouts: 1 at Auto.</summary>
    public static float LayoutFactor => Percent is 0 or 100 ? 1f : Percent / 100f;

    /// <summary>The scale MAUI started with this run (the density is read once, at launch).</summary>
    public static float AppliedLayoutFactor { get; set; } = 1f;

    public static string Name(int percent) => percent == 0 ? "Auto" : $"{percent}%";

    public static string Describe(int percent) => percent switch
    {
        0 => "As designed. A wide phone gets wider screens, never cut ones.",
        < 100 => "Smaller, more on screen. Menus change on next start.",
        100 => "As designed. Menus change on next start.",
        _ => "Bigger text and buttons. Menus change on next start.",
    };

    /// <summary>Sets the painted screens' factor for a window of this size.</summary>
    public static void ApplyToPainters(double width, double height) =>
        PKForge.Chrome.UiScale.Factor = PKForge.Chrome.UiScale.FactorFor(width, height, Scale);
}
