namespace PKForge.Domain;

/// <summary>Issue #48: PMD sprites come in very uneven sizes. Small and middling ones are drawn
/// as they are; past <see cref="Full"/> art pixels tall the extra height is compressed, so a big
/// species still stands taller than a small one, but no giant dwarfs the rest of the park.</summary>
public static class ParkSpriteScale
{
    /// <summary>Heights up to this are drawn 1:1.</summary>
    public const float Full = 34;
    /// <summary>How much of the height beyond <see cref="Full"/> is kept.</summary>
    public const float Kept = .35f;

    /// <param name="height">The visible height of the species' standing animation, in art pixels.</param>
    /// <returns>The factor to draw it at (1 or less), in steps of 1/16 so the same species always
    /// samples the same rows.</returns>
    public static float For(float height)
    {
        if (!float.IsFinite(height) || height <= Full) return 1;
        var target = Full + (height - Full) * Kept;
        return MathF.Max(.5f, MathF.Round(target / height * 16) / 16);
    }
}
