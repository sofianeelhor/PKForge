using System.Text.RegularExpressions;

namespace PKForge.Domain;

/// <summary>
/// Which engine route opens a save. The bytes always have the final word: a route is
/// only honoured when the save's layout supports it (see <see cref="IFormatAwareSaveEngine"/>),
/// because opening a CFRU save with the stock Gen 3 engine corrupts it on the first write.
/// </summary>
public enum SaveFormat
{
    // Persisted in the identity store. Never renumber existing entries.
    /// <summary>Let the bytes decide (the historical behaviour).</summary>
    Auto = 0,
    /// <summary>Stock PKHeX layouts: retail games and hacks that keep a retail save layout.</summary>
    Standard = 1,
    /// <summary>The Unbound CFRU layout (sector footers stamped 0x01121999).</summary>
    Unbound = 2,
    /// <summary>The Radical Red CFRU layout (FireRed envelope, CFRU checksum windows).</summary>
    RadicalRed = 3,
    /// <summary>The GS Chronicles CFRU layout (sector footers stamped 0x66290096, 25 boxes).</summary>
    GsChronicles = 4,
}

/// <summary>
/// The save layout PKForge can actually prove from bytes. Several games share each one
/// (Emerald Rogue writes an Emerald layout; many FireRed hacks write the CFRU layout), so a
/// family is evidence about the engine, never about which game the player is running.
/// </summary>
public enum SaveLayoutFamily
{
    Other = 0,
    Emerald = 1,
    RubySapphire = 2,
    FireRedLeafGreen = 3,
    /// <summary>CFRU with Unbound's sector stamp.</summary>
    UnboundCfru = 4,
    /// <summary>CFRU checksum windows without Unbound's stamp (Radical Red and its cousins).</summary>
    Cfru = 5,
    /// <summary>CFRU with GS Chronicles' sector stamp.</summary>
    GsChroniclesCfru = 6,
    /// <summary>Diamond and Pearl: one save format, the edition is not in the bytes.</summary>
    DiamondPearl = 7,
}

/// <summary>
/// What the engine detected from the save's bytes, and nothing more: the label is the
/// engine's own game name, so a ROM hack on a retail layout shows as the game it is built
/// on until the player says otherwise (see <see cref="SaveIdentity"/>).
/// </summary>
/// <param name="Label">The shelf label ("Pokémon Emerald", "Pokémon Radical Red").</param>
/// <param name="Family">The save layout, which decides the "Set game" choices.</param>
/// <param name="Format">The engine route the bytes call for.</param>
/// <param name="SuggestedName">A name prefill for Rename, lifted from the file/ROM name. Never shown on its own.</param>
/// <param name="ArtLabel">The label box art is looked up by.</param>
public sealed record SaveIdentityGuess(
    string Label,
    SaveLayoutFamily Family,
    SaveFormat Format,
    string? SuggestedName = null,
    string? ArtLabel = null);

/// <summary>
/// One answer to "this game is…". Choices are offered per layout family, so the user can
/// only pick a game whose engine can safely read the bytes.
/// </summary>
public sealed record SaveGameChoice(string Id, string Label, SaveFormat Format, string? ArtLabel, bool IsHack);

/// <summary>The user's own identity for one save file, keyed by its stable document id.</summary>
/// <param name="DocumentId">The platform document id (SAF URI on Android).</param>
/// <param name="DisplayName">Custom name; null shows the game label.</param>
/// <param name="ColorKey">A <see cref="SaveIdentityPalette"/> key; null uses the console era color.</param>
/// <param name="GameChoiceId">A <see cref="SaveGameChoice.Id"/>; null keeps the detected game.</param>
/// <param name="Hidden">Kept off Home and every save picker until the player shows it again.</param>
/// <param name="AcceptedHackRisk">The player chose "Edit at my own risk" for a save the engine
/// flagged as a suspected ROM hack (<see cref="LayoutRiskKind.SuspectedHack"/>).</param>
public sealed record SaveIdentity(
    string DocumentId,
    string? DisplayName = null,
    string? ColorKey = null,
    string? GameChoiceId = null,
    bool Hidden = false,
    bool AcceptedHackRisk = false)
{
    /// <summary>True when the user has customised nothing.</summary>
    public bool IsEmpty => DisplayName is null && ColorKey is null && GameChoiceId is null && !Hidden && !AcceptedHackRisk;
}

/// <summary>Persists <see cref="SaveIdentity"/> per document.</summary>
public interface ISaveIdentityStore
{
    SaveIdentity? Get(string documentId);

    /// <summary>Replaces the stored identity (an empty identity removes the entry).</summary>
    void Set(SaveIdentity identity);

    /// <summary>Forgets the user's name, color, game choice, hidden flag and ROM-hack risk acceptance ("reset to detected").</summary>
    void Reset(string documentId);

    /// <summary>Raised with the document id after any change.</summary>
    event Action<string>? Changed;
}

/// <summary>
/// An engine that can open a save through an explicit route. Implementations must refuse
/// (throw <see cref="InvalidDataException"/>) a route the bytes cannot support rather
/// than guess: the route comes from the user, the layout from the file.
/// </summary>
public interface IFormatAwareSaveEngine : ISaveEngine
{
    ISaveEngineSession OpenSession(ReadOnlyMemory<byte> bytes, string? displayName, SaveFormat format);
}

public static class SaveEngineFormatExtensions
{
    /// <summary>Opens through <paramref name="format"/> when the engine supports routes; Auto otherwise.</summary>
    public static ISaveEngineSession OpenSession(this ISaveEngine engine, ReadOnlyMemory<byte> bytes, string? displayName, SaveFormat format) =>
        format != SaveFormat.Auto && engine is IFormatAwareSaveEngine routed
            ? routed.OpenSession(bytes, displayName, format)
            : engine.OpenSession(bytes, displayName);
}

/// <summary>Curated shelf colors (keys persist; the App maps them to theme colors).</summary>
public static class SaveIdentityPalette
{
    public static readonly IReadOnlyList<(string Key, string Name)> Swatches =
    [
        ("navy", "Navy"), ("sky", "Sky"), ("teal", "Teal"), ("leaf", "Leaf"),
        ("gold", "Gold"), ("amber", "Amber"), ("coral", "Coral"), ("berry", "Berry"),
        ("violet", "Violet"), ("slate", "Slate"),
    ];

    public static bool IsKnown(string? key) => key is not null && Swatches.Any(s => s.Key == key);
}

/// <summary>Turns the engine's byte-level detection into a shelf identity. File names never decide it.</summary>
public static class SaveIdentityRules
{
    /// <summary>Builds the detected identity for one parsed save.</summary>
    /// <param name="engineGame">The engine's game name ("Emerald", "Radical Red", "Generation 3"), or null.</param>
    /// <param name="generation">Parsed generation.</param>
    /// <param name="fileName">The save's own file name: only a Rename prefill.</param>
    /// <param name="romFileName">A ROM beside the save with the same stem: only a Rename prefill.</param>
    /// <param name="fallbackLabel">The platform label when the engine has no name (Switch title, 3DS folder).</param>
    public static SaveIdentityGuess Guess(string? engineGame, int generation, string fileName, string? romFileName, string fallbackLabel)
    {
        var suggested = SuggestedNameOf(romFileName ?? fileName);
        if (engineGame is null or "" || engineGame.StartsWith("Generation", StringComparison.Ordinal))
            return new SaveIdentityGuess(fallbackLabel, SaveLayoutFamily.Other, SaveFormat.Auto, suggested, fallbackLabel);

        var label = $"Pokémon {engineGame}";
        var format = engineGame switch
        {
            "Unbound" => SaveFormat.Unbound,
            "Radical Red" => SaveFormat.RadicalRed,
            "GS Chronicles" => SaveFormat.GsChronicles,
            _ => SaveFormat.Standard,
        };
        return new SaveIdentityGuess(label, FamilyOf(engineGame), format, suggested, label);
    }

    public static SaveLayoutFamily FamilyOf(string engineGame) => engineGame switch
    {
        "Emerald" => SaveLayoutFamily.Emerald,
        "Ruby" or "Sapphire" or "Ruby / Sapphire" => SaveLayoutFamily.RubySapphire,
        "FireRed" or "LeafGreen" or "FireRed / LeafGreen" => SaveLayoutFamily.FireRedLeafGreen,
        "Diamond" or "Pearl" or "Diamond / Pearl" => SaveLayoutFamily.DiamondPearl,
        "Unbound" => SaveLayoutFamily.UnboundCfru,
        "Radical Red" => SaveLayoutFamily.Cfru,
        "GS Chronicles" => SaveLayoutFamily.GsChroniclesCfru,
        _ => SaveLayoutFamily.Other,
    };

    /// <summary>
    /// The "this game is…" answers a save can take. Only games whose engine reads this
    /// layout are offered: a CFRU save can never be relabelled as retail FireRed.
    /// </summary>
    public static IReadOnlyList<SaveGameChoice> ChoicesFor(SaveLayoutFamily family, string detectedLabel) => family switch
    {
        SaveLayoutFamily.Emerald =>
        [
            new("emerald", "Pokémon Emerald", SaveFormat.Standard, "Pokémon Emerald", false),
            new("hack-emerald", "Emerald-based ROM hack", SaveFormat.Standard, null, true),
        ],
        SaveLayoutFamily.RubySapphire =>
        [
            new("ruby", "Pokémon Ruby", SaveFormat.Standard, "Pokémon Ruby", false),
            new("sapphire", "Pokémon Sapphire", SaveFormat.Standard, "Pokémon Sapphire", false),
            new("hack-rs", "Ruby/Sapphire-based ROM hack", SaveFormat.Standard, null, true),
        ],
        SaveLayoutFamily.FireRedLeafGreen =>
        [
            new("firered", "Pokémon FireRed", SaveFormat.Standard, "Pokémon FireRed", false),
            new("leafgreen", "Pokémon LeafGreen", SaveFormat.Standard, "Pokémon LeafGreen", false),
            new("hack-frlg", "FireRed-based ROM hack", SaveFormat.Standard, null, true),
        ],
        SaveLayoutFamily.DiamondPearl =>
        [
            new("diamond", "Pokémon Diamond", SaveFormat.Standard, "Pokémon Diamond", false),
            new("pearl", "Pokémon Pearl", SaveFormat.Standard, "Pokémon Pearl", false),
            new("hack-dp", "Diamond/Pearl-based ROM hack", SaveFormat.Standard, null, true),
        ],
        SaveLayoutFamily.UnboundCfru =>
        [
            new("unbound", "Pokémon Unbound", SaveFormat.Unbound, "Pokémon Unbound", true),
            new("hack-cfru-unbound", "Other CFRU hack (Unbound layout)", SaveFormat.Unbound, null, true),
        ],
        SaveLayoutFamily.Cfru =>
        [
            new("radicalred", "Pokémon Radical Red", SaveFormat.RadicalRed, "Pokémon Radical Red", true),
            new("hack-cfru", "Other CFRU hack (Radical Red layout)", SaveFormat.RadicalRed, null, true),
        ],
        // The 0x66290096 stamp is GS Chronicles' own build signature: no other game
        // writes it, so there is no "other hack" alternative to offer.
        SaveLayoutFamily.GsChroniclesCfru =>
        [
            new("gschronicles", "Pokémon GS Chronicles", SaveFormat.GsChronicles, "Pokémon GS Chronicles", true),
        ],
        _ =>
        [
            new("retail", detectedLabel, SaveFormat.Auto, detectedLabel, false),
            new("hack-generic", $"ROM hack ({detectedLabel.Replace("Pokémon ", "", StringComparison.Ordinal)} layout)", SaveFormat.Auto, null, true),
        ],
    };

    /// <summary>
    /// True for a save of a shared-format pair (Ruby/Sapphire, FireRed/LeafGreen, Diamond/Pearl)
    /// that its own Pokémon could not place (the guess is still the pair) and the player has
    /// not said which edition it is yet.
    /// </summary>
    public static bool NeedsEditionChoice(SaveIdentityGuess guess, string? gameChoiceId) =>
        gameChoiceId is null && guess.Label.Contains(" / ", StringComparison.Ordinal)
        && guess.Family is SaveLayoutFamily.RubySapphire or SaveLayoutFamily.FireRedLeafGreen or SaveLayoutFamily.DiamondPearl;

    /// <summary>Every choice across families, for resolving a persisted id.</summary>
    public static SaveGameChoice? FindChoice(string? id, SaveLayoutFamily family, string detectedLabel) =>
        id is null ? null : ChoicesFor(family, detectedLabel).FirstOrDefault(c => c.Id == id);

    /// <summary>The engine route persisted for a choice id, independent of any scan.</summary>
    public static SaveFormat FormatOfChoice(string? id) => id switch
    {
        "unbound" or "hack-cfru-unbound" => SaveFormat.Unbound,
        "radicalred" or "hack-cfru" => SaveFormat.RadicalRed,
        "gschronicles" => SaveFormat.GsChronicles,
        "emerald" or "hack-emerald" or "ruby" or "sapphire" or "hack-rs" or "firered" or "leafgreen" or "hack-frlg"
            or "diamond" or "pearl" or "hack-dp" => SaveFormat.Standard,
        _ => SaveFormat.Auto,
    };

    /// <summary>
    /// A Rename prefill from a file/ROM name: "Pokemon - Emerald Rogue (v1.3) [!].srm" ->
    /// "Emerald Rogue". Bracketed tags and "Pokémon" dropped; null when nothing is left.
    /// </summary>
    public static string? SuggestedNameOf(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var title = Regex.Replace(stem, @"\([^)]*\)|\[[^\]]*\]", " ");
        title = Regex.Replace(title, @"[_\-]+", " ");
        title = Regex.Replace(title, @"(?i)\bpok[eé]mon\b", " ");
        title = Regex.Replace(title, @"\s+", " ").Trim();
        return title.Length is > 0 and <= 32 ? title : null;
    }
}

/// <summary>What the shelf shows for one save once detection and the user's identity are merged.</summary>
public sealed record ResolvedSaveIdentity(
    string DisplayName,
    string GameLabel,
    string? ArtLabel,
    string? ColorKey,
    SaveFormat Format,
    bool IsCustomized,
    string? GameChoiceId = null,
    bool IsRenamed = false,
    bool IsHidden = false);

public static class SaveIdentityResolver
{
    /// <summary>Merges detection with the user's identity. The user always wins.</summary>
    public static ResolvedSaveIdentity Resolve(SaveIdentityGuess guess, SaveIdentity? identity)
    {
        var choice = SaveIdentityRules.FindChoice(identity?.GameChoiceId, guess.Family, guess.Label);
        var gameLabel = choice?.Label ?? guess.Label;
        var renamed = identity?.DisplayName is { Length: > 0 };
        return new ResolvedSaveIdentity(
            renamed ? identity!.DisplayName! : gameLabel,
            gameLabel,
            choice is not null ? choice.ArtLabel : guess.ArtLabel,
            SaveIdentityPalette.IsKnown(identity?.ColorKey) ? identity!.ColorKey : null,
            choice?.Format is { } f && f != SaveFormat.Auto ? f : guess.Format,
            identity is { IsEmpty: false },
            choice?.Id,
            renamed,
            identity?.Hidden == true);
    }
}

/// <summary>
/// The Home shelf's grouping: one cartridge per game identity. Saves of one game in any
/// language, region or emulator share a tile; a save the player renamed, recolored or
/// re-identified carries a different identity and gets its own.
/// </summary>
public static class SaveShelf
{
    /// <summary>
    /// (display name, game label, route, art, color, generation, chosen game, renamed).
    /// Detection output only varies by game; everything the player sets splits the tile.
    /// </summary>
    public static string GroupKey(DetectedSave save)
    {
        var identity = save.Identity;
        return string.Join('\u001f',
            save.GameLabel,
            identity?.GameLabel ?? save.GameLabel,
            save.Format.ToString(),
            save.ArtLabel ?? "",
            identity?.ColorKey ?? "",
            save.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            identity?.GameChoiceId ?? "",
            identity?.IsRenamed == true ? "renamed" : "");
    }

    /// <summary>Groups in first-seen order; each group's most recently played save comes first.</summary>
    public static IReadOnlyList<IReadOnlyList<DetectedSave>> Group(IEnumerable<DetectedSave> saves) =>
        [.. saves
            .GroupBy(GroupKey, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<DetectedSave>)[.. g.OrderByDescending(s => s.LastModified ?? DateTimeOffset.MinValue)])];
}
