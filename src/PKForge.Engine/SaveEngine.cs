using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>Adapts the pinned PKHeX.Core save parser without leaking engine types.</summary>
public sealed class SaveEngine : IFormatAwareSaveEngine
{
    static SaveEngine() => LegalityRules.Apply();

    /// <summary>Pokémon History for the sessions this engine opens: the store while the player has
    /// it on, else null. Unset (tests, tools), transfers behave exactly as they always have.</summary>
    public Func<IPokemonHistoryStore?>? History { get; init; }

    private SaveEngineSession Attach(SaveEngineSession session)
    {
        session.HistorySource = History;
        return session;
    }

    /// <summary>
    /// Opens through the user's chosen route, but only when the bytes carry that layout:
    /// the stock engine on a CFRU save (or a CFRU engine on a retail one) would corrupt
    /// the file on the first write.
    /// </summary>
    public ISaveEngineSession OpenSession(ReadOnlyMemory<byte> bytes, string? displayName, SaveFormat format)
    {
        if (format == SaveFormat.Auto) return OpenSession(bytes, displayName);
        var route = SaveParser.HackRoute(RetroArchSaveContainer.Decode(bytes.Span));
        if (format == SaveFormat.Standard && route is null)
            return Attach(new SaveEngineSession(bytes, displayName));
        if (route == format)
            return OpenHack(route.Value, bytes, displayName);
        throw new InvalidDataException(
            $"This save was set to open as {FormatName(format)}, but its bytes carry the {LayoutName(route)} layout. " +
            "Change the game from the save's menu on the home screen. The file was not touched.");
    }

    private static string LayoutName(SaveFormat? route) => route switch
    {
        SaveFormat.Unbound => "Unbound",
        SaveFormat.GsChronicles => "GS Chronicles",
        SaveFormat.RadicalRed => "CFRU (Radical Red)",
        SaveFormat.RechargedYellow => "Recharged Yellow",
        SaveFormat.Quetzal => "Quetzal",
        SaveFormat.SoulGold => "Soul Gold",
        SaveFormat.Expansion => "pokeemerald-expansion",
        SaveFormat.OriginHeartGold => "Origin HeartGold",
        SaveFormat.EliteRedux => "Elite Redux",
        SaveFormat.FireRedExtended => "Fire Red Extended",
        SaveFormat.CrystalAdvanceRedux => "Crystal Advance Redux",
        _ => "standard",
    };

    /// <summary>The dedicated engine for a ROM-hack route from <see cref="SaveParser.HackRoute"/>.</summary>
    private static ISaveEngineSession OpenHack(SaveFormat route, ReadOnlyMemory<byte> bytes, string? displayName) => route switch
    {
        SaveFormat.Unbound => OpenUnbound(bytes, displayName),
        SaveFormat.GsChronicles => OpenGsChronicles(bytes, displayName),
        SaveFormat.RadicalRed => OpenRadicalRed(bytes, displayName),
        SaveFormat.RechargedYellow => new RechargedYellow.RechargedYellowEngineSession(bytes, displayName),
        SaveFormat.Quetzal => new Quetzal.QuetzalEngineSession(bytes, displayName),
        SaveFormat.SoulGold => new SoulGold.SoulGoldEngineSession(bytes, displayName),
        SaveFormat.Expansion => OpenExpansionFamily(bytes, displayName),
        SaveFormat.OriginHeartGold => new OriginHeartGold.OriginHeartGoldEngineSession(bytes, displayName),
        SaveFormat.EliteRedux => new EliteRedux.EliteReduxEngineSession(bytes, displayName),
        SaveFormat.FireRedExtended or SaveFormat.CrystalAdvanceRedux => new FireRedHacks.FireRedHackEngineSession(bytes, displayName),
        _ => throw new ArgumentOutOfRangeException(nameof(route), route, null),
    };

    private static string FormatName(SaveFormat format) => format switch
    {
        SaveFormat.Unbound => "Unbound",
        SaveFormat.RadicalRed => "Radical Red / CFRU",
        SaveFormat.GsChronicles => "GS Chronicles",
        SaveFormat.RechargedYellow => "Recharged Yellow",
        SaveFormat.Quetzal => "Quetzal",
        SaveFormat.Expansion => "pokeemerald-expansion",
        SaveFormat.SoulGold => "Soul Gold",
        SaveFormat.OriginHeartGold => "Origin HeartGold",
        SaveFormat.EliteRedux => "Elite Redux",
        SaveFormat.FireRedExtended => "Fire Red Extended",
        SaveFormat.CrystalAdvanceRedux => "Crystal Advance Redux",
        _ => "a standard game",
    };

    public SaveSnapshot Open(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        if (SaveParser.HackRoute(RetroArchSaveContainer.Decode(bytes.Span)) is { } route)
            return OpenHack(route, bytes, displayName).Snapshot;
        if (!SaveParser.TryGetSaveFile(bytes.ToArray(), out var save) || save is null)
            throw SaveParser.NotReadable(bytes.Span);

        var slots = new List<SlotSummary>(save.BoxCount * save.BoxSlotCount);
        for (var box = 0; box < save.BoxCount; box++)
        {
            for (var slot = 0; slot < save.BoxSlotCount; slot++)
            {
                var entity = save.GetBoxSlotAtIndex(box, slot);
                slots.Add(new SlotSummary(box, slot, entity.Species == 0 ? null : entity.Species,
                    entity.IsNicknamed ? entity.Nickname : null, entity.IsShiny,
                    entity.Species == 0 || entity.Valid, entity.Form, entity.IsEgg, entity.HeldItem, EntitySprite.Traits(entity)));
            }
        }

        return new SaveSnapshot(save.Context.ToString(), save.Generation, bytes.ToArray(), slots, displayName);
    }

    public ISaveEngineSession OpenSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        if (SaveParser.HackRoute(RetroArchSaveContainer.Decode(bytes.Span)) is { } route)
            return OpenHack(route, bytes, displayName);
        return Attach(new SaveEngineSession(bytes, displayName));
    }

    /// <summary>The expansion-family route: Emerald Exceeded keeps the expansion's tables but
    /// a Pokémon record of its own, so it has its own session.</summary>
    private static ISaveEngineSession OpenExpansionFamily(ReadOnlyMemory<byte> bytes, string? displayName) =>
        Exceeded.ExceededFormat.Matches(SramPadding.Trim(RetroArchSaveContainer.Decode(bytes.Span)))
            ? new Exceeded.ExceededEngineSession(bytes, displayName)
            : new Expansion.ExpansionEngineSession(bytes, displayName);

    private static Unbound.UnboundEngineSession OpenUnbound(ReadOnlyMemory<byte> bytes, string? displayName) =>
        new(bytes, displayName);

    private static GsChronicles.GsChroniclesEngineSession OpenGsChronicles(ReadOnlyMemory<byte> bytes, string? displayName) =>
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
            if (SaveParser.HackRoute(decoded) is not null) return true;
            return SaveParser.TryGetSaveFile(bytes.ToArray(), out _);
        }
        catch (InvalidDataException) { return false; }
    }

    public string? DescribeLayoutRisk(ReadOnlyMemory<byte> bytes) => WriteSafety.DescribeLayoutRisk(bytes);

    public LayoutRisk? AssessLayoutRisk(ReadOnlyMemory<byte> bytes) => WriteSafety.AssessLayoutRisk(bytes);

    public string? CheckWriteSafety(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> candidate, WriteScope? scope) =>
        WriteSafety.CheckWriteSafety(original, candidate, scope);

    public BankEntryInfo? TryDescribeEntity(byte[] bytes, string sourceName, string? format = null)
    {
        // A CFRU mon is described from its own game's tables: most of its species have no PK3 at all.
        if (RadicalRed.CfruEntity.Recognize(format) is { } name)
            return RadicalRed.CfruEntity.Describe(bytes, name, sourceName);
        var entity = EntityBytes.Parse(bytes, format);
        if (entity is null || entity.Species == 0) return null;
        return new BankEntryInfo(entity.Species, entity.Form, entity.IsShiny,
            entity.IsNicknamed ? entity.Nickname : GameInfo.GetStrings("en").specieslist[entity.Species],
            entity.CurrentLevel, entity.Format, sourceName, EntityBytes.FormatOf(entity), entity.HeldItem, EntitySprite.Traits(entity));
    }

    public ISaveEngineSession? OpenEntitySession(byte[] entityBytes, string? displayName = null, string? format = null)
    {
        // A CFRU mon is read in its own game's session, never as its PK3 view: an edit
        // saved from that view would replace the exact record with the lossy conversion.
        if (RadicalRed.CfruEntity.Recognize(format) is { } cfru)
            return RadicalRed.CfruEntity.Host(entityBytes, cfru, displayName);
        var entity = EntityBytes.Parse(entityBytes, format);
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
        return new SaveEngineSession(blank, displayName, entity is RK4 ? entity.Data.ToArray() : null);
    }

    /// <summary>One representative game per generation for blank generation contexts.</summary>
    private static GameVersion VersionFor(int generation) => generation switch
    {
        // Not Blue (Japan): BU only exists in Japanese, so its blank save took the Japanese
        // layout while the session labelled it English and reparsed it as international.
        1 => GameVersion.RD,
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
        InitializeGameBoyLists(blank);
        return new SaveEngineSession(blank, displayName);
    }

    /// <summary>A blank save of one exact game (tools/DexGen builds the Legends: Arceus,
    /// Brilliant Diamond, Let's Go and Z-A bundles from these; a generation alone cannot
    /// tell those games apart from Sword/Shield, Sun/Moon or Scarlet/Violet).</summary>
    public ISaveEngineSession OpenBlankSession(GameVersion version, string? displayName = null)
    {
        var blank = BlankSaveFile.Get(version, "PKForge", LanguageID.English);
        InitializeGameBoyLists(blank);
        return new SaveEngineSession(blank, displayName);
    }

    /// <summary>A blank Game Boy save is all zeros, and the parser recognizes Gen 1/2 saves (and
    /// tells their layouts apart) only by the empty-list terminators of the party and the
    /// current box: once exported, a blank save reopened as nothing. The terminators a real
    /// cartridge writes are put in place; PKHeX then keeps the lists valid on every write.</summary>
    private static void InitializeGameBoyLists(SaveFile blank)
    {
        if (blank is SAV1 { Japanese: false })
        {
            var offsets = SAV1Offsets.INT;
            SeedEmptyList(blank.Data, offsets.Party);
            SeedEmptyList(blank.Data, offsets.CurrentBox);
        }
        else if (blank is SAV2 { Japanese: false, Korean: false } sav2)
        {
            var offsets = new SAV2Offsets(sav2);
            SeedEmptyList(blank.Data, offsets.Party);
            SeedEmptyList(blank.Data, offsets.CurrentBox);
        }

        static void SeedEmptyList(Span<byte> data, int offset)
        {
            if (offset < 0 || data[offset] != 0) return;
            data[offset + 1] = 0xFF;
        }
    }

    public SaveDescription? TryDescribe(ReadOnlyMemory<byte> bytes, string? displayName = null)
    {
        var raw = RetroArchSaveContainer.Decode(bytes.Span);
        // These hacks are read by their own engine: stock PKHeX either rejects them
        // (Quetzal's 16 sections) or reads a retail Emerald that is not there.
        switch (SaveParser.HackRoute(raw))
        {
            case SaveFormat.Quetzal:
            {
                using var quetzal = new Quetzal.QuetzalEngineSession(bytes, displayName);
                var trainer = quetzal.GetTrainer();
                return new SaveDescription("Quetzal", 3, trainer.Name, quetzal.PlayTimeText(), TrainerId: trainer.TID);
            }
            case SaveFormat.SoulGold:
            {
                using var soulgold = new SoulGold.SoulGoldEngineSession(bytes, displayName);
                var trainer = soulgold.GetTrainer();
                return new SaveDescription("Soul Gold", 3, trainer.Name, soulgold.PlayTimeText(), TrainerId: trainer.TID);
            }
            case SaveFormat.Expansion when Exceeded.ExceededFormat.Matches(SramPadding.Trim(raw)):
            {
                using var exceeded = new Exceeded.ExceededEngineSession(bytes, displayName);
                var trainer = exceeded.GetTrainer();
                return new SaveDescription(exceeded.GameNames[0], 3, trainer.Name, exceeded.PlayTimeText(), TrainerId: trainer.TID);
            }
            case SaveFormat.Expansion:
            {
                using var expansion = new Expansion.ExpansionEngineSession(bytes, displayName);
                var trainer = expansion.GetTrainer();
                return new SaveDescription(expansion.GameNames[0], 3, trainer.Name, expansion.PlayTimeText(), TrainerId: trainer.TID);
            }
            case SaveFormat.OriginHeartGold:
            {
                using var origin = new OriginHeartGold.OriginHeartGoldEngineSession(bytes, displayName);
                var trainer = origin.GetTrainer();
                return new SaveDescription(OriginHeartGold.OriginHeartGoldEngineSession.GameName, 4, trainer.Name, origin.PlayTimeText(), TrainerId: trainer.TID);
            }
            case SaveFormat.EliteRedux:
            {
                using var eliteRedux = new EliteRedux.EliteReduxEngineSession(bytes, displayName);
                var trainer = eliteRedux.GetTrainer();
                return new SaveDescription(EliteRedux.EliteReduxEngineSession.GameName, 3, trainer.Name, eliteRedux.PlayTimeText(), TrainerId: trainer.TID);
            }
            case SaveFormat.FireRedExtended or SaveFormat.CrystalAdvanceRedux:
            {
                using var fireRed = new FireRedHacks.FireRedHackEngineSession(bytes, displayName);
                var trainer = fireRed.GetTrainer();
                return new SaveDescription(fireRed.GameNames[0], 3, trainer.Name, fireRed.PlayTimeText(), TrainerId: trainer.TID);
            }
        }
        if (!SaveParser.TryGetSaveFile(raw, out var save) || save is null)
            return null;
        // The trainer ID tells two playthroughs under one name apart (the Saves list groups by it).
        return Describe(raw, save) with { TrainerId = save.TID16 };
    }

    public string? DescribeRejection(ReadOnlyMemory<byte> bytes)
    {
        byte[] data;
        try { data = SramPadding.Trim(RetroArchSaveContainer.Decode(bytes.Span)); }
        catch (InvalidDataException error) { return error.Message; }
        if (data.Length == 0) return "empty file";
        // A cartridge that was never saved in game holds erased SRAM, not a save.
        if (data.AsSpan().IndexOfAnyExcept(data[0]) < 0)
            return $"{data.Length} bytes all 0x{data[0]:X2}: blank memory, never saved in game";
        if (data.Length != 0x8000) return $"{data.Length} bytes: not a known save size once padding is set aside";
        // Gen 1 international: the game itself trusts one checksum over 0x2598-0x3522.
        byte sum = 0;
        for (var i = 0x2598; i < 0x3523; i++) sum += data[i];
        return (byte)~sum == data[0x3523]
            ? "Game Boy size and a valid Gen 1 checksum, but its party or box list is not a valid list"
            : "Game Boy size, but neither a Gen 1 nor a Gen 2 layout (checksum and lists do not match)";
    }

    private static SaveDescription Describe(byte[] raw, SaveFile save)
    {

        if (SaveParser.IsLuminescentPlatinum(raw))
            return new SaveDescription("Luminescent Platinum", save.Generation, save.OT, PlayTime(save), LanguageTag(save));

        // Compass keeps the vanilla S/V format. The fork's canonical marker (the
        // TrainerSeed table, present in every Compass version) tells it apart from
        // retail Scarlet/Violet - the v2.1 settings blocks are NOT a safe marker,
        // pre-2.1 saves carry none of them.
        if (save is SAV9SV sv && CompassBlockKeys.IsCompassSave(sv))
            return new SaveDescription("Compass", save.Generation, save.OT, PlayTime(save), LanguageTag(save));

        if (SaveParser.IsPokemonUnbound(raw))
            return new SaveDescription("Unbound", save.Generation, save.OT, PlayTime(save), LanguageTag(save));

        if (SaveParser.IsPokemonGsChronicles(raw))
            return new SaveDescription("GS Chronicles", save.Generation, save.OT, PlayTime(save), LanguageTag(save));

        // Radical Red keeps the FireRed envelope; the CFRU window signature tells it
        // apart from every stock FRLG save (see RadicalRedFormat.IsRadicalRed).
        if (SaveParser.IsPokemonRadicalRed(raw))
            return new SaveDescription("Radical Red", save.Generation, save.OT, PlayTime(save), LanguageTag(save));

        // Recharged Yellow keeps retail Gen 3 identity bytes at the front of SaveBlock2,
        // so the parsed save's OT and play time are already the hack's own.
        if (SaveParser.IsPokemonRechargedYellow(raw))
            return new SaveDescription("Recharged Yellow", save.Generation, save.OT, PlayTime(save), LanguageTag(save));

        // A hack recognized from its bytes but with no editor (R.O.W.E., Fire Red Extended…):
        // named for what it is, never "Emerald" or "FireRed". The layout guard keeps it read-only.
        if (save is SAV3 && RecognizedHacks.Identify(raw) is { } hack)
            return new SaveDescription(hack.Name, save.Generation, save.OT, PlayTime(save), LanguageTag(save));

        // GameCube-only versions sit outside the handheld game-name table.
        var sideGameName = save switch
        {
            SAV3Colosseum => "Colosseum",
            SAV3XD => "XD: Gale of Darkness",
            SAV3RSBox => "Box: Ruby & Sapphire",
            _ => null,
        };
        if (sideGameName is not null)
            return new SaveDescription(sideGameName, save.Generation, save.OT, PlayTime(save), LanguageTag(save));

        // These editions share one layout: the player's own Pokémon name the cartridge when
        // they agree, otherwise the pair is shown and the player picks once.
        if (save is SAV3RS or SAV3FRLG or SAV4DP)
        {
            var edition = SaveParser.EditionFromOwnPokemon(save);
            var editionName = edition is { } v ? GameInfo.GetStrings("en").gamelist[(int)v] : null;
            var pair = save switch { SAV3RS => "Ruby / Sapphire", SAV3FRLG => "FireRed / LeafGreen", _ => "Diamond / Pearl" };
            return new SaveDescription(editionName ?? pair, save.Generation, save.OT, PlayTime(save), LanguageTag(save));
        }

        // Red, international Blue, Japanese Green and Japanese Blue are one save (Yellow is
        // its own file). The bytes cannot pick a cartridge; the player does, once.
        if (save is SAV1 { Version: GameVersion.RB })
            return new SaveDescription("Red / Blue", save.Generation, save.OT, PlayTime(save), LanguageTag(save));

        var strings = GameInfo.GetStrings("en");
        var versionIndex = (int)save.Version;
        var gameName = versionIndex > 0 && versionIndex < strings.gamelist.Length && strings.gamelist[versionIndex].Length > 0
            ? strings.gamelist[versionIndex]
            : EditionPair(save.Version, strings) ?? $"Generation {save.Generation}";
        return new SaveDescription(gameName, save.Generation, save.OT, PlayTime(save), LanguageTag(save));
    }

    /// <summary>
    /// The editions a combined version stands for ("Diamond / Pearl"), for saves whose bytes
    /// cannot tell the two apart (PKHeX reports Diamond and Pearl saves as DP), or null.
    /// </summary>
    internal static string? EditionPair(GameVersion version, GameStrings strings)
    {
        var editions = Enum.GetValues<GameVersion>()
            .Where(single => single != version && single.IsValidSavedVersion() && version.Contains(single))
            .Select(single => (int)single < strings.gamelist.Length ? strings.gamelist[(int)single] : "")
            .Where(name => name.Length > 0)
            .Distinct()
            // PKHeX's enum order is not the box order (White precedes Black); every pair a
            // save can actually report (Diamond/Pearl, Gold/Silver, Black/White) reads alphabetically.
            .Order(StringComparer.Ordinal)
            .ToArray();
        return editions.Length >= 2 ? string.Join(" / ", editions) : null;
    }

    /// <summary>
    /// The cartridge language the save itself records ("FR"), for telling saves of one game
    /// apart. Gen 1-3 saves only record Japanese vs. international, so only JA is trusted there.
    /// </summary>
    /// <summary>Play time as "h:mm:ss". PKHeX joins it with "ː" (a letter, safe in file names), which
    /// the pixel font does not have; PKForge shows it as text, so it gets the plain colon.</summary>
    internal static string PlayTime(SaveFile save) => save.PlayTimeString.Replace('ː', ':');

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
}
