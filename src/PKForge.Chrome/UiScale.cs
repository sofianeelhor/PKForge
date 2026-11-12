namespace PKForge.Chrome;

/// <summary>
/// How big the painted screens draw. They lay out on a design canvas <see cref="DesignWidth"/>
/// wide and scale it to the canvas width, so the design height is whatever the canvas leaves.
/// The factor grows or shrinks that design canvas: above 1 everything reads bigger (less fits),
/// below 1 smaller (more fits). At 1 it is exactly the Thor's 1872 design pixels.
/// </summary>
public static class UiScale
{
    /// <summary>The Thor's design width, the one every painted screen was drawn on.</summary>
    public const float ThorDesignWidth = 1872f;

    /// <summary>
    /// The least design height a bigger setting may leave on a full-screen canvas: the screens
    /// were drawn for about 1050, and below 900 their lists and footers start to crowd.
    /// </summary>
    public const float MinDesignHeight = 900f;

    /// <summary>
    /// The narrowest design canvas a bigger setting may leave: the screens keep fixed-width
    /// lists on the left, and under 1600 the panel beside them starts to crowd.
    /// </summary>
    public const float MinDesignWidth = 1600f;

    private static float _factor = 1f;

    /// <summary>The applied factor (1 on the Thor at Auto). Set by the app from the window and the setting.</summary>
    public static float Factor
    {
        get => _factor;
        set => _factor = float.IsFinite(value) && value > 0.1f ? value : 1f;
    }

    /// <summary>The width painted screens lay out on: 1872 at factor 1.</summary>
    public static float DesignWidth => _factor == 1f ? ThorDesignWidth : ThorDesignWidth / _factor;

    /// <summary>
    /// The factor for a window of this size at the chosen <paramref name="scale"/> (1 = as designed).
    /// A window wider than 16:9 (a 20:9 phone) first widens the design canvas so the screens keep
    /// the Thor's design height instead of losing their bottom; a bigger setting is then held back
    /// so the design height stays at least <see cref="MinDesignHeight"/>. A 16:9 or taller window
    /// at 1 gets exactly 1.
    /// </summary>
    public static float FactorFor(double width, double height, double scale)
    {
        if (width <= 0 || height <= 0 || !double.IsFinite(scale) || scale <= 0) return 1f;
        var ratio = Math.Min(width, height) / Math.Max(width, height);
        var aspect = ratio * 1920.0 / 1080.0;
        if (aspect > 0.995) aspect = 1.0; // 16:9 and taller, or a few pixels off it
        if (scale <= 1) return (float)(aspect * scale);
        var roomiest = Math.Max(aspect, ratio * ThorDesignWidth / MinDesignHeight);
        return (float)Math.Min(Math.Min(aspect * scale, roomiest), Math.Max(aspect, ThorDesignWidth / MinDesignWidth));
    }
}
