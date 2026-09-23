using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Adapts the pinned PKHeX.Core save parser without leaking engine types.</summary>
public sealed class SaveEngine : IFormatAwareSaveEngine
{
    /// <summary>
    /// Opens through the user's chosen route, but only when the bytes carry that layout:
    /// the stock engine on a CFRU save (or a CFRU engine on a retail one) would corrupt
    /// the file on the first write.
    /// </summary>
    public ISaveEngineSession OpenSession(ReadOnlyMemory<byte> bytes, string? displayName, SaveFormat format)
    {
        if (format == SaveFormat.Auto) return OpenSession(bytes, displayName);
        var decoded = RetroArchSaveContainer.Decode(bytes.Span);
        var unbound = SaveParser.IsPokemonUnbound(decoded);
        var cfru = !unbound && SaveParser.IsPokemonRadicalRed(decoded);
        return format switch
        {
            SaveFormat.Unbound when unbound => OpenUnbound(bytes, displayName),
            SaveFormat.RadicalRed when cfru => OpenRadicalRed(bytes, displayName),
            SaveFormat.Standard when !unbound && !cfru => new SaveEngineSession(bytes, displayName),
            _ => throw new InvalidDataException(
                $"This save was set to open as {FormatName(format)}, but its bytes carry the {(unbound ? "Unbound" : cfru ? "CFRU (Radical Red)" : "standard")} layout. " +
                "Change the game from the save's menu on the home screen. The file was not touched."),
        };
    }

    private static string FormatName(SaveFormat format) => format switch
    {
        SaveFormat.Unbound => "Unbound",
        SaveFormat.RadicalRed => "Radical Red / CFRU",
        _ => "a standard game",
    };

    public SaveSnapshot Open(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        if (SaveParser.IsPokemonUnbound(RetroArchSaveContainer.Decode(bytes.Span)))
            return OpenUnbound(bytes, displayName).Snapshot;
        if (SaveParser.IsPokemonRadicalRed(RetroArchSaveContainer.Decode(bytes.Span)))
            return OpenRadicalRed(bytes, displayName).Snapshot;
        if (!SaveParser.TryGetSaveFile(bytes.ToArray(), out var save) || save is null)
            throw new InvalidDataException("The selected bytes are not a recognized save file.");

        var slots = new List<SlotSummary>(save.BoxCount * save.BoxSlotCount);
        for (var box = 0; box < save.BoxCount; box++)
        {
            for (var slot = 0; slot < save.BoxSlotCount; slot++)
            {
                var entity = save.GetBoxSlotAtIndex(box, slot);
                slots.Add(new SlotSummary(box, slot, entity.Species == 0 ? null : entity.Species,
                    entity.IsNicknamed ? entity.Nickname : null, entity.IsShiny,
                    entity.Species == 0 || entity.Valid, entity.Form, entity.IsEgg));
            }
        }

        return new SaveSnapshot(save.Context.ToString(), save.Generation, bytes.ToArray(), slots, displayName);
    }

    public ISaveEngineSession OpenSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        if (SaveParser.IsPokemonUnbound(RetroArchSaveContainer.Decode(bytes.Span)))
            return OpenUnbound(bytes, displayName);
        if (SaveParser.IsPokemonRadicalRed(RetroArchSaveContainer.Decode(bytes.Span)))
            return OpenRadicalRed(bytes, displayName);
        return new SaveEngineSession(bytes, displayName);
    }

    private static Unbound.UnboundEngineSession OpenUnbound(ReadOnlyMemory<byte> bytes, string? displayName) =>
        new(bytes, displayName);

    private static RadicalRed.RadicalRedEngineSession OpenRadicalRed(ReadOnlyMemory<byte> bytes, string? displayName) =>
        new(bytes, displayName);

    public ReadOnlyMemory<byte> Serialize(SaveSnapshot snapshot) => snapshot.OriginalBytes.ToArray();

    public bool Validate(ReadOnlyMemory<byte> bytes)
    {
        // Route exactly like Open/OpenSession: SafeSaveWriter gates every write on this,
        // so a candidate is valid precisely when the engine would reopen it. Stock
        // PKHeX rejects Unbound's CFRU sector signature outright, and Radical Red only
        // separates from vanilla FRLG structurally — both (and the RetroArch containers
        // they ride in) must clear the romhack routes before the stock parser runs.
        try
        {
            var decoded = RetroArchSaveContainer.Decode(bytes.Span);
            if (SaveParser.IsPokemonUnbound(decoded)) return true;
            if (SaveParser.IsPokemonRadicalRed(decoded)) return true;
            return SaveParser.TryGetSaveFile(bytes.ToArray(), out _);
        }
        catch (InvalidDataException) { return false; }
    }

    public string? DescribeLayoutRisk(ReadOnlyMemory<byte> bytes) => WriteSafety.DescribeLayoutRisk(bytes);

    public string? CheckWriteSafety(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> candidate, WriteScope? scope) =>
        WriteSafety.CheckWriteSafety(original, candidate, scope);

    public BankEntryInfo? TryDescribeEntity(byte[] bytes, string sourceName)
    {
        var entity = EntityFormat.GetFromBytes(bytes);
        if (entity is null || entity.Species == 0) return null;
        return new BankEntryInfo(entity.Species, entity.Form, entity.IsShiny,
            entity.IsNicknamed ? entity.Nickname : GameInfo.GetStrings("en").specieslist[entity.Species],
            entity.CurrentLevel, entity.Format, sourceName);
    }

    public ISaveEngineSession? OpenEntitySession(byte[] entityBytes, string? displayName = null)
    {
        var entity = EntityFormat.GetFromBytes(entityBytes);
        if (entity is null || entity.Species == 0)
            return null; // genuinely not an editable Pokémon

        // A throwaway save of the mon's own generation gives the editor a real trainer
        // context (legality, ability tables, stat maths) without touching any game file.
        // Past this point the bytes ARE a mon, so failures throw with a reason the UI shows.
        SaveFile blank;
        try
        {
            var version = entity.Context.GetSingleGameVersion();
            if (entity is { Format: 1, Japanese: true }) version = GameVersion.BU;
            var language = (uint)entity.Language <= 12 ? (LanguageID)entity.Language : LanguageID.English;
            blank = BlankSaveFile.Get(version, entity.OriginalTrainerName, language);
        }
        catch (Exception error)
        {
            throw new InvalidOperationException($"No editable context for this {entity.Context} Pokémon: {error.Message}", error);
        }

        var converted = EntityConverter.ConvertToType(entity, blank.PKMType, out var result);
        if (converted is null)
            throw new InvalidOperationException($"This {entity.Context} Pokémon could not be loaded into a {blank.Version} editor ({result}).");
        converted.RefreshChecksum();
        // Surgical: seeding the editor must not dex-mark or rewrite handler data on
        // the loose entity being edited.
        blank.SetBoxSlotAtIndex(converted, 0, 0, EntityImportSettings.None);
        return new SaveEngineSession(blank, displayName);
    }

    /// <summary>One representative game per generation for blank generation contexts.</summary>
    private static GameVersion VersionFor(int generation) => generation switch
    {
        1 => GameVersion.BU,
        2 => GameVersion.C,
        3 => GameVersion.E,
        4 => GameVersion.Pt,
        5 => GameVersion.B2,
        6 => GameVersion.AS,
        7 => GameVersion.UM,
        8 => GameVersion.SW,
        9 => GameVersion.VL,
        _ => GameVersion.VL,
    };

    public ISaveEngineSession OpenBlankSession(int generation, string? displayName = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(generation, 9);
        var blank = BlankSaveFile.Get(VersionFor(generation), "PKForge", LanguageID.English);
        return new SaveEngineSession(blank, displayName);
    }

    public SaveDescription? TryDescribe(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        var raw = RetroArchSaveContainer.Decode(bytes.Span);
        if (!SaveParser.TryGetSaveFile(raw, out var save) || save is null)
            return null;

        if (SaveParser.IsLuminescentPlatinum(raw))
            return new SaveDescription("Luminescent Platinum", save.Generation, save.OT, save.PlayTimeString, LanguageTag(save));

        // Compass keeps the vanilla S/V format. The fork's canonical marker (the
        // TrainerSeed table, present in every Compass version) tells it apart from
        // retail Scarlet/Violet - the v2.1 settings blocks are NOT a safe marker,
        // pre-2.1 saves carry none of them.
        if (save is SAV9SV sv && CompassBlockKeys.IsCompassSave(sv))
            return new SaveDescription("Compass", save.Generation, save.OT, save.PlayTimeString, LanguageTag(save));

        if (SaveParser.IsPokemonUnbound(raw))
            return new SaveDescription("Unbound", save.Generation, save.OT, save.PlayTimeString, LanguageTag(save));

        // Radical Red keeps the FireRed envelope; the CFRU window signature tells it
        // apart from every stock FRLG save (see RadicalRedFormat.IsRadicalRed).
        if (SaveParser.IsPokemonRadicalRed(raw))
            return new SaveDescription("Radical Red", save.Generation, save.OT, save.PlayTimeString, LanguageTag(save));

        // GameCube-only versions sit outside the handheld game-name table.
        var sideGameName = save switch
        {
            SAV3Colosseum => "Colosseum",
            SAV3XD => "XD: Gale of Darkness",
            SAV3RSBox => "Box: Ruby & Sapphire",
            _ => null,
        };
        if (sideGameName is not null)
            return new SaveDescription(sideGameName, save.Generation, save.OT, save.PlayTimeString, LanguageTag(save));

        // The bytes cannot tell these editions apart; the user picks one per save.
        if (save is SAV3RS)
            return new SaveDescription("Ruby / Sapphire", save.Generation, save.OT, save.PlayTimeString, LanguageTag(save));
        if (save is SAV3FRLG)
            return new SaveDescription("FireRed / LeafGreen", save.Generation, save.OT, save.PlayTimeString, LanguageTag(save));

        var strings = GameInfo.GetStrings("en");
        var versionIndex = (int)save.Version;
        var gameName = versionIndex > 0 && versionIndex < strings.gamelist.Length && strings.gamelist[versionIndex].Length > 0
            ? strings.gamelist[versionIndex]
            : $"Generation {save.Generation}";
        return new SaveDescription(gameName, save.Generation, save.OT, save.PlayTimeString, LanguageTag(save));
    }

    /// <summary>
    /// The cartridge language the save itself records ("FR"), for telling saves of one game
    /// apart. Gen 1-3 saves only record Japanese vs. international, so only JA is trusted there.
    /// </summary>
    private static string? LanguageTag(SaveFile save)
    {
        var tag = (LanguageID)save.Language switch
        {
            LanguageID.Japanese => "JA",
            LanguageID.English => "EN",
            LanguageID.French => "FR",
            LanguageID.Italian => "IT",
            LanguageID.German => "DE",
            LanguageID.Spanish or LanguageID.SpanishL => "ES",
            LanguageID.Korean => "KO",
            LanguageID.ChineseS => "ZH",
            LanguageID.ChineseT => "ZH",
            _ => null,
        };
        return save.Generation <= 3 && tag != "JA" ? null : tag;
    }

    /// <summary>
    /// Unbound relocates the PC and extends FireRed's species table, so editing it with
    /// the stock FireRed engine would corrupt the save. It is recognized (and labeled)
    /// everywhere, but stays closed until the dedicated Unbound engine ships.
    /// </summary>
    private static InvalidOperationException UnboundNotEditable() => new(
        "Pokémon Unbound is recognized, but its CFRU save layout needs the Unbound editor " +
        "(coming soon). The save file was not touched.");
}
