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
    /// <summary>The Recharged Yellow layout (retail Emerald envelope, repacked SaveBlock1).</summary>
    RechargedYellow = 5,
    /// <summary>The Pokémon Quetzal layout (16 sectors/slot, 104-byte party, packed PC).</summary>
    Quetzal = 6,
    /// <summary>A pokeemerald-expansion-family layout (Heart &amp; Soul 2.0, standard expansion and the hacks it names, Perfect Emerald, Team Rocket Edition).</summary>
    Expansion = 7,
    /// <summary>The Pokémon Soul Gold layout (96-byte party, BX storage generations).</summary>
    SoulGold = 8,
    /// <summary>The Pokémon Origin HeartGold layout (HGSS envelope, larger blocks, 24 boxes).</summary>
    OriginHeartGold = 10,
    /// <summary>The Pokémon Elite Redux layout (one rotating 20-sector slot, 52-byte packed Pokémon).</summary>
    EliteRedux = 9,
    /// <summary>Fire Red Extended: FireRed's blocks in 0xFF0 chunks, CFRU checksum windows, encrypted PK3.</summary>
    FireRedExtended = 11,
    /// <summary>Crystal Advance Redux: FireRed's blocks in 0xFF0 chunks, one 0xF80 window, plain PK3.</summary>
    CrystalAdvanceRedux = 12,
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
    /// <summary>Red, Blue, Green and Japanese Blue: one save format. Yellow is a different save.</summary>
    RedBlue = 8,
    /// <summary>Pokémon Recharged Yellow: retail Emerald envelope, repacked SaveBlock1.</summary>
    RechargedYellow = 9,
    /// <summary>Pokémon Quetzal: 16 sectors/slot and its own party/PC codecs.</summary>
    Quetzal = 10,
    /// <summary>pokeemerald-expansion family: 14-sector envelope with the packed mon codec.</summary>
    Expansion = 11,
    /// <summary>Pokémon Soul Gold: expansion envelope, 96-byte party, BX storage.</summary>
    SoulGold = 12,
    /// <summary>Pokémon Origin HeartGold: HeartGold's envelope with its own block sizes and 24 boxes.</summary>
    OriginHeartGold = 14,
    /// <summary>Pokémon Elite Redux: one rotating 20-sector slot and its own Pokémon record.</summary>
    EliteRedux = 13,
    /// <summary>Pokémon Fire Red Extended (FireRed blocks, CFRU windows, encrypted PK3).</summary>
    FireRedExtended = 15,
    /// <summary>Pokémon Crystal Advance Redux (FireRed blocks, flat 0xF80 window, plain PK3).</summary>
    CrystalAdvanceRedux = 16,
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
    bool AcceptedHackRisk = false,
    bool KeepShown = false,
    string? ArtKey = null)
{
    // ArtKey: the player's own cartridge, logo and hero images (see CustomGameArt); it
    // follows the save through renames and game choices.

    // KeepShown: the player put a backup copy (DetectedSave.IsCopy) on the shelf; it stays
    // there instead of starting in Hidden saves again at the next scan.

    /// <summary>True when the user has customised nothing.</summary>
    public bool IsEmpty => DisplayName is null && ColorKey is null && GameChoiceId is null && !Hidden && !AcceptedHackRisk && !KeepShown && ArtKey is null;
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
            "Recharged Yellow" => SaveFormat.RechargedYellow,
            "Quetzal" => SaveFormat.Quetzal,
            "Heart and Soul 2.0" or "pokeemerald-expansion" or "Perfect Emerald" or "Team Rocket Edition" or "Emerald Exceeded"
                or "Lazarus" or "Lucid" or "Emerald Enhanced" => SaveFormat.Expansion,
            "Soul Gold" => SaveFormat.SoulGold,
            "Origin HeartGold" => SaveFormat.OriginHeartGold,
            "Elite Redux" => SaveFormat.EliteRedux,
            "Fire Red Extended" => SaveFormat.FireRedExtended,
            "Crystal Advance Redux" => SaveFormat.CrystalAdvanceRedux,
            _ => SaveFormat.Standard,
        };
        var family = FamilyOf(engineGame);
        // Red/Blue/Green share one layout and no art of their own: leave the sticker blank
        // until the player picks a cartridge, so an unchosen save is never shown as Green.
        var art = family == SaveLayoutFamily.RedBlue ? null : label;
        return new SaveIdentityGuess(label, family, format, suggested, art);
    }

    public static SaveLayoutFamily FamilyOf(string engineGame) => engineGame switch
    {
        "Emerald" => SaveLayoutFamily.Emerald,
        "Ruby" or "Sapphire" or "Ruby / Sapphire" => SaveLayoutFamily.RubySapphire,
        "FireRed" or "LeafGreen" or "FireRed / LeafGreen" => SaveLayoutFamily.FireRedLeafGreen,
        "Diamond" or "Pearl" or "Diamond / Pearl" => SaveLayoutFamily.DiamondPearl,
        "Red / Blue" => SaveLayoutFamily.RedBlue,
        "Unbound" => SaveLayoutFamily.UnboundCfru,
        "Radical Red" => SaveLayoutFamily.Cfru,
        "GS Chronicles" => SaveLayoutFamily.GsChroniclesCfru,
        "Recharged Yellow" => SaveLayoutFamily.RechargedYellow,
        "Quetzal" => SaveLayoutFamily.Quetzal,
        "Heart and Soul 2.0" or "pokeemerald-expansion" or "Perfect Emerald" or "Team Rocket Edition" or "Emerald Exceeded"
            or "Lazarus" or "Lucid" or "Emerald Enhanced" => SaveLayoutFamily.Expansion,
        "Soul Gold" => SaveLayoutFamily.SoulGold,
        "Origin HeartGold" => SaveLayoutFamily.OriginHeartGold,
        "Elite Redux" => SaveLayoutFamily.EliteRedux,
        "Fire Red Extended" => SaveLayoutFamily.FireRedExtended,
        "Crystal Advance Redux" => SaveLayoutFamily.CrystalAdvanceRedux,
        _ => SaveLayoutFamily.Other,
    };

    /// <summary>
    /// The edition a file's own name points at ("Pokemon - Blue Version (USA).sav" → Blue), as
    /// an index into <paramref name="choices"/>, or -1. Only a whole word counts, so "FireRed"
    /// never reads as Red. The edition picker starts on it; the player still confirms.
    /// </summary>
    public static int EditionFromFileName(IReadOnlyList<SaveGameChoice> choices, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return -1;
        var words = Regex.Split(Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant(), "[^a-z]+")
            .Where(w => w.Length > 0).ToHashSet(StringComparer.Ordinal);
        var matches = new List<int>();
        for (var i = 0; i < choices.Count; i++)
        {
            if (choices[i].IsHack) continue;
            // "Pokémon Blue (INT)" → "blue", "Pokémon FireRed" → "firered".
            var name = Regex.Replace(choices[i].Label, @"\([^)]*\)", "").Trim();
            var key = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.ToLowerInvariant();
            if (key is { Length: > 0 } && words.Contains(key)) matches.Add(i);
        }
        if (matches.Count == 0) return -1;
        // Blue is two cartridges: a Japanese file name picks the Japanese one.
        var japanese = words.Overlaps(["japan", "jp", "jpn", "j"]);
        return matches.FirstOrDefault(i => choices[i].Label.Contains("(JP)", StringComparison.Ordinal) == japanese, matches[0]);
    }

    /// <summary>True when the file's own name says Yellow (a Red/Blue-layout save whose bytes do not).</summary>
    public static bool FileNameSaysYellow(string? fileName) =>
        !string.IsNullOrWhiteSpace(fileName)
        && Regex.IsMatch(Path.GetFileNameWithoutExtension(fileName), @"(?i)(^|[^a-z])yellow([^a-z]|$)");

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
        // Idea from community PR #60 by gui-camargo (issue #53). International Blue and
        // Japanese Green share one PKHeX version id (GN); the shelf still tells them apart.
        // Art labels are the bundled asset names, not the menu text.
        SaveLayoutFamily.RedBlue =>
        [
            new("red", "Pokémon Red", SaveFormat.Standard, "Pokémon Red", false),
            new("blue-int", "Pokémon Blue (INT)", SaveFormat.Standard, "Pokémon Blue", false),
            new("green-jp", "Pokémon Green (JP)", SaveFormat.Standard, "Pokémon Blue [INT]/Green [JP]", false),
            new("blue-jp", "Pokémon Blue (JP)", SaveFormat.Standard, "Pokémon Blue [JP]", false),
            new("hack-rb", "Red/Blue-based ROM hack", SaveFormat.Standard, null, true),
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
        SaveLayoutFamily.RechargedYellow =>
        [
            new("recharged-yellow", "Pokémon Recharged Yellow", SaveFormat.RechargedYellow, "Pokémon Recharged Yellow", true),
        ],
        SaveLayoutFamily.Quetzal =>
        [
            new("quetzal", "Pokémon Quetzal", SaveFormat.Quetzal, "Pokémon Quetzal", true),
        ],
        SaveLayoutFamily.Expansion =>
        [
            new("expansion", "pokeemerald-expansion hack", SaveFormat.Expansion, null, true),
        ],
        SaveLayoutFamily.SoulGold =>
        [
            new("soulgold", "Pokémon Soul Gold", SaveFormat.SoulGold, "Pokémon Soul Gold", true),
        ],
        SaveLayoutFamily.OriginHeartGold =>
        [
            new("originheartgold", "Pokémon Origin HeartGold", SaveFormat.OriginHeartGold, "Pokémon Origin HeartGold", true),
        ],
        SaveLayoutFamily.EliteRedux =>
        [
            new("eliteredux", "Pokémon Elite Redux", SaveFormat.EliteRedux, "Pokémon Elite Redux", true),
        ],
        SaveLayoutFamily.FireRedExtended =>
        [
            new("firered-extended", "Pokémon Fire Red Extended", SaveFormat.FireRedExtended, "Pokémon Fire Red Extended", true),
        ],
        SaveLayoutFamily.CrystalAdvanceRedux =>
        [
            new("crystal-advance-redux", "Pokémon Crystal Advance Redux", SaveFormat.CrystalAdvanceRedux, "Pokémon Crystal Advance Redux", true),
        ],
        _ =>
        [
            new("retail", detectedLabel, SaveFormat.Auto, detectedLabel, false),
            new("hack-generic", $"ROM hack ({detectedLabel.Replace("Pokémon ", "", StringComparison.Ordinal)} layout)", SaveFormat.Auto, null, true),
        ],
    };

    /// <summary>
    /// True for a save of a shared-format pair (Ruby/Sapphire, FireRed/LeafGreen, Diamond/Pearl,
    /// Red/Blue/Green)
    /// that its own Pokémon could not place (the guess is still the pair) and the player has
    /// not said which edition it is yet.
    /// </summary>
    public static bool NeedsEditionChoice(SaveIdentityGuess guess, string? gameChoiceId) =>
        gameChoiceId is null && guess.Label.Contains(" / ", StringComparison.Ordinal)
        && guess.Family is SaveLayoutFamily.RubySapphire or SaveLayoutFamily.FireRedLeafGreen
            or SaveLayoutFamily.DiamondPearl or SaveLayoutFamily.RedBlue;

    /// <summary>Every choice across families, for resolving a persisted id.</summary>
    public static SaveGameChoice? FindChoice(string? id, SaveLayoutFamily family, string detectedLabel) =>
        id is null ? null : ChoicesFor(family, detectedLabel).FirstOrDefault(c => c.Id == id);

    /// <summary>The engine route persisted for a choice id, independent of any scan.</summary>
    public static SaveFormat FormatOfChoice(string? id) => id switch
    {
        "unbound" or "hack-cfru-unbound" => SaveFormat.Unbound,
        "radicalred" or "hack-cfru" => SaveFormat.RadicalRed,
        "gschronicles" => SaveFormat.GsChronicles,
        "recharged-yellow" => SaveFormat.RechargedYellow,
        "quetzal" => SaveFormat.Quetzal,
        "expansion" => SaveFormat.Expansion,
        "soulgold" => SaveFormat.SoulGold,
        "originheartgold" => SaveFormat.OriginHeartGold,
        "eliteredux" => SaveFormat.EliteRedux,
        "firered-extended" => SaveFormat.FireRedExtended,
        "crystal-advance-redux" => SaveFormat.CrystalAdvanceRedux,
        "emerald" or "hack-emerald" or "ruby" or "sapphire" or "hack-rs" or "firered" or "leafgreen" or "hack-frlg"
            or "diamond" or "pearl" or "hack-dp"
            or "red" or "blue-int" or "green-jp" or "blue-jp" or "hack-rb" => SaveFormat.Standard,
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
    bool IsHidden = false,
    string? ArtKey = null);

public static class SaveIdentityResolver
{
    /// <summary>Merges detection with the user's identity. The user always wins: a backup
    /// copy (<paramref name="isCopy"/>) is hidden only until they choose to show it.</summary>
    public static ResolvedSaveIdentity Resolve(SaveIdentityGuess guess, SaveIdentity? identity, bool isCopy = false)
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
            identity?.Hidden == true || (isCopy && identity?.KeepShown != true),
            CustomGameArt.IsValidKey(identity?.ArtKey) ? identity!.ArtKey : null);
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
            identity?.IsRenamed == true ? "renamed" : "",
            identity?.ArtKey ?? "");
    }

    /// <summary>Groups in first-seen order; each group's live saves come first, most recently played
    /// first, then any backup copy the player put back on Home (a copy written to more recently
    /// must not become the tile's first pick).</summary>
    public static IReadOnlyList<IReadOnlyList<DetectedSave>> Group(IEnumerable<DetectedSave> saves) =>
        [.. saves
            .GroupBy(GroupKey, StringComparer.Ordinal)
            .Select(g => (IReadOnlyList<DetectedSave>)[.. g.OrderBy(s => s.IsCopy).ThenByDescending(s => s.LastModified ?? DateTimeOffset.MinValue)])];
}
