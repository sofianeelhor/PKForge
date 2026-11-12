namespace PKForge.Domain;

/// <summary>
/// The Home shelf's filter keys ("all", "release", "az", "gen1".."gen9", a console bucket,
/// "vanilla", "hacks"), their chip captions and what each lets through. Keys are persisted,
/// so existing ones are never renamed.
/// </summary>
public static class ShelfFilter
{
    public const string Default = "all";

    private static readonly string[] Keys =
    [
        "all", "release", "az", "vanilla", "hacks",
        "gb", "gbmono", "gbc", "gba", "ds", "3ds", "switch",
        "gen1", "gen2", "gen3", "gen4", "gen5", "gen6", "gen7", "gen8", "gen9",
    ];

    /// <summary>A persisted key, or <see cref="Default"/> when it is unknown (older or newer app).</summary>
    public static string Normalize(string? key) => key is not null && Keys.Contains(key) ? key : Default;

    /// <summary>The chip text after "Filter · ".</summary>
    public static string CaptionOf(string? key) => Normalize(key) switch
    {
        "release" => "Release",
        "az" => "A-Z",
        "vanilla" => "Vanilla",
        "hacks" => "ROM hacks",
        "gb" => "Game Boy",
        "gbmono" => "GB",
        "gbc" => "GBC",
        "gba" => "GBA",
        "ds" => "DS",
        "3ds" => "3DS",
        "switch" => "Switch",
        var k when k.StartsWith("gen", StringComparison.Ordinal) => $"Gen {Roman(k[3] - '0')}",
        _ => "All",
    };

    /// <summary>True when a cartridge of this game stays on the shelf under the filter.</summary>
    public static bool Matches(string? key, int generation, string gameLabel, bool isRomHack) => Normalize(key) switch
    {
        "vanilla" => !isRomHack,
        "hacks" => isRomHack,
        "gb" => ConsoleOf(generation, gameLabel) is "Game Boy" or "Game Boy Color",
        "gbmono" => ConsoleOf(generation, gameLabel) == "Game Boy",
        "gbc" => ConsoleOf(generation, gameLabel) == "Game Boy Color",
        "gba" => ConsoleOf(generation, gameLabel) == "GBA",
        "ds" => ConsoleOf(generation, gameLabel) == "DS",
        "3ds" => ConsoleOf(generation, gameLabel) == "3DS",
        "switch" => ConsoleOf(generation, gameLabel) == "Switch",
        var k when k.StartsWith("gen", StringComparison.Ordinal) => generation == k[3] - '0',
        _ => true,
    };

    /// <summary>
    /// The console a game shipped on. Gen I is the Game Boy (Yellow included), Gen II the
    /// Game Boy Color (Gold/Silver also boot on a Game Boy, Crystal does not; both are GBC
    /// games). Let's Go is a Switch game despite being generation 7 with the 3DS pair.
    /// </summary>
    public static string ConsoleOf(int generation, string gameLabel) =>
        gameLabel.StartsWith("Pokémon Let's Go", StringComparison.Ordinal)
            ? "Switch"
            : generation switch
            {
                1 => "Game Boy",
                2 => "Game Boy Color",
                3 => "GBA",
                4 or 5 => "DS",
                6 or 7 => "3DS",
                _ => "Switch",
            };

    /// <summary>
    /// A ROM hack: the player marked it as one (any "hack-" choice, or a hack title such as
    /// Unbound), or the bytes are a hack-only layout (CFRU, Recharged Yellow,
    /// Quetzal, Soul Gold, pokeemerald-expansion, Origin HeartGold).
    /// </summary>
    public static bool IsRomHack(ResolvedSaveIdentity? identity, SaveIdentityGuess? guess)
    {
        if (identity?.GameChoiceId is { } id)
            return id.StartsWith("hack-", StringComparison.Ordinal) || IsHackFormat(SaveIdentityRules.FormatOfChoice(id));
        return IsHackFormat(identity?.Format ?? guess?.Format ?? SaveFormat.Auto);
    }

    private static bool IsHackFormat(SaveFormat format) =>
        format is SaveFormat.Unbound or SaveFormat.RadicalRed or SaveFormat.GsChronicles
            or SaveFormat.RechargedYellow or SaveFormat.Quetzal or SaveFormat.SoulGold or SaveFormat.Expansion
            or SaveFormat.OriginHeartGold or SaveFormat.EliteRedux
            or SaveFormat.FireRedExtended or SaveFormat.CrystalAdvanceRedux;

    private static string Roman(int n) => n switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V", 6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX", _ => n.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
