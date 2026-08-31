using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Adapts the pinned PKHeX.Core save parser without leaking engine types.</summary>
public sealed class SaveEngine : ISaveEngine
{
    public SaveSnapshot Open(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        if (SaveParser.IsPokemonUnbound(bytes.Span))
            return OpenUnbound(bytes, displayName).Snapshot;
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
                    entity.Species == 0 || entity.Valid, entity.Form));
            }
        }

        return new SaveSnapshot(save.Context.ToString(), save.Generation, bytes.ToArray(), slots, displayName);
    }

    public ISaveEngineSession OpenSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        if (SaveParser.IsPokemonUnbound(bytes.Span))
            return OpenUnbound(bytes, displayName);
        return new SaveEngineSession(bytes, displayName);
    }

    private static Unbound.UnboundEngineSession OpenUnbound(ReadOnlyMemory<byte> bytes, string? displayName) =>
        new(bytes, displayName);

    public ReadOnlyMemory<byte> Serialize(SaveSnapshot snapshot) => snapshot.OriginalBytes.ToArray();

    public bool Validate(ReadOnlyMemory<byte> bytes) => SaveParser.TryGetSaveFile(bytes.ToArray(), out _);

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

    public SaveDescription? TryDescribe(ReadOnlyMemory<byte> bytes)
    {
        var raw = bytes.ToArray();
        if (!SaveParser.TryGetSaveFile(raw, out var save) || save is null)
            return null;

        if (SaveParser.IsLuminescentPlatinum(raw))
            return new SaveDescription("Luminescent Platinum", save.Generation, save.OT, save.PlayTimeString);

        // Compass keeps the vanilla S/V format. The fork's canonical marker (the
        // TrainerSeed table, present in every Compass version) tells it apart from
        // retail Scarlet/Violet - the v2.1 settings blocks are NOT a safe marker,
        // pre-2.1 saves carry none of them.
        if (save is SAV9SV sv && CompassBlockKeys.IsCompassSave(sv))
            return new SaveDescription("Compass", save.Generation, save.OT, save.PlayTimeString);

        if (SaveParser.IsPokemonUnbound(raw))
            return new SaveDescription("Unbound", save.Generation, save.OT, save.PlayTimeString);

        var strings = GameInfo.GetStrings("en");
        var versionIndex = (int)save.Version;
        var gameName = versionIndex > 0 && versionIndex < strings.gamelist.Length && strings.gamelist[versionIndex].Length > 0
            ? strings.gamelist[versionIndex]
            : $"Generation {save.Generation}";
        return new SaveDescription(gameName, save.Generation, save.OT, save.PlayTimeString);
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
