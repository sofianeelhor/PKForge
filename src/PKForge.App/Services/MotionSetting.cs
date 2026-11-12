namespace PKForge.App.Services;

/// <summary>How much the interface moves.</summary>
public enum MotionLevel
{
    /// <summary>Every animation: glides, hops, walking dashes, notices that rise.</summary>
    Full,
    /// <summary>Only what carries meaning (a notice fading in and out); nothing loops or glides.</summary>
    Reduced,
    /// <summary>Nothing moves: things appear where they end up.</summary>
    Off,
}

/// <summary>
/// The player's choice of how much the interface animates, for those who dislike motion and
/// for devices where it costs frames. One setting, read by every animation in the app.
/// </summary>
public static class MotionSetting
{
    private const string Key = "motion_level";

    public static MotionLevel Level
    {
        get => (MotionLevel)Math.Clamp(Preferences.Default.Get(Key, (int)MotionLevel.Full), 0, 2);
        set => Preferences.Default.Set(Key, (int)value);
    }

    /// <summary>True when decorative motion plays (glides, pulses, walking dashes).</summary>
    public static bool Decorative => Level == MotionLevel.Full;

    /// <summary>True unless everything must simply appear.</summary>
    public static bool Any => Level != MotionLevel.Off;

    public static string Name(MotionLevel level) => level switch
    {
        MotionLevel.Reduced => "Reduced",
        MotionLevel.Off => "Off",
        _ => "Full",
    };

    public static string Describe(MotionLevel level) => level switch
    {
        MotionLevel.Reduced => "Notices still fade in and out. Nothing glides, hops or loops.",
        MotionLevel.Off => "Nothing moves. The lightest on the device.",
        _ => "Every animation: the hand glides, selections walk, notices rise.",
    };
}
