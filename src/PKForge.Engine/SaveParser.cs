using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Centralized save recognition for PKForge's supported save formats.
/// Luminescent Platinum keeps BDSP's physical layout and checksum scheme, but
/// brands known releases with a <c>FFFFxxxx</c> revision header. Keeping that
/// header intact is essential: replacing it with a retail BDSP revision would
/// silently change the save.
/// </summary>
internal static class SaveParser
{
    private const int LuminescentV11Size = 0xEDC20;
    private const int LuminescentV13Size = 0xEF0A4;

    internal static bool TryGetSaveFile(byte[] data, out SaveFile? save)
    {
        data = RetroArchSaveContainer.Decode(data);
        if (SaveUtil.TryGetSaveFile(data, out save))
            return true;

        // Emulator SRAM dumps carry trailing padding (VBA-M appends 8 KB of 0xFF, and
        // the flash's erased tail adds more), which PKHeX's exact-size check rejects.
        // The payload is untouched; the pad comes back on write.
        var trimmed = SramPadding.Trim(data);
        if (trimmed.Length != data.Length)
        {
            data = trimmed;
            if (SaveUtil.TryGetSaveFile(data, out save))
                return true;
        }

        // Keep the app's Luminescent entry point independent from the upstream
        // detector. This protects Android trimmed builds if a detector branch is
        // removed while the explicitly referenced save type remains available.
        if (IsLuminescentPlatinum(data))
        {
            save = new SAV8BSLuminescent(data);
            return true;
        }

        if (SAV3GCMemoryCard.IsMemoryCardSize(data.AsSpan()))
            throw new InvalidDataException("This is a whole GameCube memory card. Export the Colosseum or XD save as a .gci file using Dolphin's Memory Card Manager, or use Dolphin's GCI Folder slot mode and link that folder.");

        save = null;
        return false;
    }

    /// <summary>
    /// Why bytes no parser reads were refused: a recognized hack PKForge cannot open yet
    /// (Elite Redux, Origin HeartGold…) is named, anything else is "not a recognized save".
    /// </summary>
    internal static InvalidDataException NotReadable(ReadOnlySpan<byte> bytes)
    {
        RecognizedHacks.Hack? hack = null;
        try { hack = RecognizedHacks.Identify(RetroArchSaveContainer.Decode(bytes)); }
        catch (InvalidDataException) { }
        return new InvalidDataException(hack is null
            ? "The selected bytes are not a recognized save file."
            : RecognizedHacks.RefusalMessage(hack));
    }

    // Ruby and Sapphire have identical save layouts and no reliable version flag.
    // Only resolve an ambiguous RS save, and require a standalone name token.
    /// <summary>
    /// FR/LG, R/S and D/P share one save layout, so the file itself never names the edition.
    /// The game the player picked for this save wins; without a pick, the edition the
    /// player's own Pokémon were caught in decides (see <see cref="EditionFromOwnPokemon"/>).
    /// File names are never read.
    /// </summary>
    internal static void ApplyVersionHint(SaveFile save, string? chosenGame)
    {
        // Gen 1 has no version byte and no per-Pokémon origin: the picked cartridge is
        // applied in memory for this open (shelf, Pokédex origin); the file is not rewritten.
        if (save is SAV1)
        {
            if (Gen1Edition(chosenGame) is { } gen1)
                save.Version = gen1;
            return;
        }
        if (save is not (SAV3FRLG or SAV3RS)) return;
        var game = chosenGame?.Replace("Pokémon ", "", StringComparison.Ordinal).Trim();
        GameVersion? chosen = game switch
        {
            "FireRed" when save is SAV3FRLG => GameVersion.FR,
            "LeafGreen" when save is SAV3FRLG => GameVersion.LG,
            "Ruby" when save is SAV3RS => GameVersion.R,
            "Sapphire" when save is SAV3RS => GameVersion.S,
            _ => null,
        };
        // The edition is only a hint: whatever the Pokémon data holds, it never stops a save opening.
        GameVersion? owned = null;
        try { owned = chosen is null ? EditionFromOwnPokemon(save) : null; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IndexOutOfRangeException) { }
        if ((chosen ?? owned) is { } edition)
            save.Version = edition;
    }

    /// <summary>
    /// The Gen 1 cartridge the player picked (idea from community PR #60, gui-camargo).
    /// International Blue and Japanese Green share PKHeX's GN id. File names never match.
    /// </summary>
    private static GameVersion? Gen1Edition(string? chosenGame) =>
        chosenGame?.Replace("Pokémon ", "", StringComparison.Ordinal).Trim() switch
        {
            "Red" => GameVersion.RD,
            "Blue (INT)" or "Green (JP)" => GameVersion.GN,
            "Blue (JP)" => GameVersion.BU,
            _ => null,
        };

    /// <summary>
    /// The edition of a shared-layout save, read from the save's own Pokémon: every mon the
    /// player caught or hatched records the game it came from, and a mon whose trainer ID,
    /// secret ID and OT name are the save's own was obtained in this cartridge. Null when the
    /// save holds none of its own Pokémon, or they disagree (never guessed).
    /// </summary>
    internal static GameVersion? EditionFromOwnPokemon(SaveFile save)
    {
        GameVersion[] editions = save switch
        {
            SAV3FRLG => [GameVersion.FR, GameVersion.LG],
            SAV3RS => [GameVersion.R, GameVersion.S],
            SAV4DP => [GameVersion.D, GameVersion.P],
            _ => [],
        };
        if (editions.Length == 0) return null;

        var seen = new HashSet<GameVersion>();
        foreach (var pk in OwnedEntities(save))
        {
            if (pk.Species == 0 || !pk.ChecksumValid || pk.ID32 != save.ID32 || pk.OriginalTrainerName != save.OT)
                continue;
            if (Array.IndexOf(editions, pk.Version) >= 0) seen.Add(pk.Version);
        }
        return seen.Count == 1 ? seen.First() : null;
    }

    /// <summary>
    /// Party slots to read: the stored count is a raw byte an early or damaged save can set
    /// past the 6 slots that exist, and reading slot 7 runs off the party block.
    /// </summary>
    internal static int PartySlots(SaveFile save) => Math.Clamp(save.PartyCount, 0, 6);

    private static IEnumerable<PKM> OwnedEntities(SaveFile save)
    {
        if (save.HasParty)
            for (var i = 0; i < PartySlots(save); i++)
                yield return save.GetPartySlotAtIndex(i);
        if (save.HasBox)
            for (var box = 0; box < save.BoxCount; box++)
            for (var slot = 0; slot < save.BoxSlotCount; slot++)
                yield return save.GetBoxSlotAtIndex(box, slot);
    }

    internal static bool IsLuminescentPlatinum(ReadOnlySpan<byte> data)
    {
        if (data.Length < sizeof(uint))
            return false;

        if (data.Length is not (LuminescentV11Size or LuminescentV13Size))
            return false;

        // Luminescent reserves the high word and has shipped more than one low-word
        // revision. Match its own loader rather than rejecting a future revision.
        return (BinaryPrimitives.ReadUInt32LittleEndian(data) & 0xFFFF0000) == 0xFFFF0000;
    }

    /// <summary>
    /// Unbound (and its CFRU engine) stamps every GBA sector footer with 0x01121999
    /// where retail Pokémon saves carry the 0x080120xx signature family. Verified
    /// against a real Unbound v2.1.1.1 save (sectors 5-12 = the CFRU PC stream) and a
    /// real vanilla FireRed save (all sectors 0x08012025, checksums all valid).
    /// </summary>
    internal const uint UnboundSectorSignature = 0x0112_1999;

    /// <summary>
    /// True when the bytes are a Pokémon Unbound save. Unbound keeps FireRed's save
    /// envelope, so stock PKHeX parses it as plain SAV3FRLG with a wrong PC and party
    /// layout; detecting it here keeps the shelf honest and blocks unsafe edits until
    /// the dedicated Unbound engine exists.
    /// </summary>
    internal static bool IsPokemonUnbound(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x20_000)
            return false;

        // The 0x20000 file = 32 sectors of 0x1000; the signature is a u32 at 0xFF8 of each.
        var stamps = 0;
        for (var sector = 0; sector < 32; sector++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(sector * 0x1000 + 0xFF8)..]) == UnboundSectorSignature)
                stamps++;
        }
        return stamps >= 8; // a full main half stamps 14 sectors; extras never carry it
    }

    /// <summary>
    /// GS Chronicles' CUSTOM_FILE_SIGNATURE (src/config.h of github.com/G0LD/GS-Chronicles-Engine
    /// @7517ab2): its save.c stamps it on every section it writes (HandleWriteSector) and
    /// accepts it or the retail 0x08012025 on load. Verified on a real GS Chronicles save:
    /// all 28 slot sectors carry it, sectors 30/31 are raw (zeroed footers).
    /// </summary>
    internal const uint GsChroniclesSectorSignature = 0x6629_0096;

    /// <summary>
    /// True when the bytes are a Pokémon GS Chronicles save (a CFRU FireRed hack by Ruki
    /// Studios). Decided by the sector signature alone, exactly like Unbound: a full slot
    /// stamps 14 sectors, so 8 rules out a stray match while tolerating a torn backup slot.
    /// </summary>
    internal static bool IsPokemonGsChronicles(ReadOnlySpan<byte> data)
    {
        if (data.Length < 0x20_000)
            return false;
        var stamps = 0;
        for (var sector = 0; sector < 32; sector++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data[(sector * 0x1000 + 0xFF8)..]) == GsChroniclesSectorSignature)
                stamps++;
        }
        return stamps >= 8;
    }

    /// <summary>
    /// True when the bytes are a Pokémon Radical Red save. Radical Red keeps the retail
    /// FireRed envelope AND signature, so stock PKHeX parses it as plain SAV3FRLG with a
    /// wrong PC and party; the only safe separator is structural (CFRU checksum windows
    /// vs vanilla). Unbound must be tested FIRST wherever both apply - it shares the
    /// CFRU window table but stamps its own signature.
    /// </summary>
    internal static bool IsPokemonRadicalRed(ReadOnlySpan<byte> data) =>
        RadicalRed.RadicalRedFormat.IsRadicalRed(data);

    /// <summary>
    /// True when the bytes are a Pokémon Recharged Yellow save. The hack keeps the retail
    /// Emerald flash envelope and PK3 records but repacks SaveBlock1, so PKHeX reads the
    /// party at the wrong offset. Detection is structural (see RechargedYellowFormat);
    /// mGBA's 16-byte RTC footer is trimmed first.
    /// </summary>
    internal static bool IsPokemonRechargedYellow(ReadOnlySpan<byte> data)
    {
        if (data.Length < RechargedYellow.RechargedYellowFormat.FileSize)
            return false;
        var payload = SramPadding.Trim(data.ToArray());
        return RechargedYellow.RechargedYellowFormat.LooksLike(payload);
    }

    /// <summary>
    /// True when the bytes are a Pokémon Quetzal save: a complete 16-sector slot with
    /// valid checksums and a readable 104-byte party in its newest slot. Quetzal keeps
    /// the retail 0x08012025 signature, so the 16-id slot and the plaintext party are
    /// the discriminators.
    /// </summary>
    internal static bool IsPokemonQuetzal(ReadOnlySpan<byte> data)
    {
        if (data.Length < Quetzal.QuetzalFormat.FileSize)
            return false;
        return Quetzal.QuetzalFormat.LooksLike(data);
    }

    /// <summary>
    /// True when the bytes are a Pokémon Soul Gold save: BX storage markers or a
    /// 96-byte party at SB1+0x238 whose records pass the hack's sanity checks.
    /// Routed before the stock parser and before the generic expansion engine.
    /// </summary>
    internal static bool IsPokemonSoulGold(ReadOnlySpan<byte> data)
    {
        if (data.Length < SoulGold.SoulGoldFormat.FileSize)
            return false;
        return SoulGold.SoulGoldFormat.LooksLike(data);
    }

    /// <summary>
    /// True when the bytes are a save the generic pokeemerald-expansion engine can prove:
    /// Heart &amp; Soul 2.0, a standard 14-box expansion save, or Perfect Emerald.
    /// A retail save with a single injected foreign mon is NOT claimed.
    /// </summary>
    internal static bool IsPokemonExpansion(ReadOnlySpan<byte> data)
    {
        if (data.Length < Expansion.ExpansionFormat.FileSize)
            return false;
        var payload = SramPadding.Trim(data.ToArray());
        return Expansion.ExpansionFormat.LooksLike(payload) is not null || Exceeded.ExceededFormat.Matches(payload);
    }

    /// <summary>
    /// True when the bytes are a Pokémon Elite Redux save: one intact rotating 20-sector slot
    /// (ids 0-19, one counter, every checksum valid, nothing stamped after it) whose party
    /// decodes as the hack's own records. No retail save carries a section id past 13.
    /// </summary>
    internal static bool IsPokemonEliteRedux(ReadOnlySpan<byte> data) =>
        data.Length >= EliteRedux.EliteReduxFormat.FileSize && EliteRedux.EliteReduxFormat.LooksLike(data);

    /// <summary>
    /// True when the bytes are a Pokémon Origin HeartGold save: a 512 KB HeartGold envelope
    /// whose general and storage blocks carry the hack's sizes (0xF7CC / 0x18408) with a valid
    /// CRC. No retail Gen 4 game writes those sizes; stock PKHeX refuses the file.
    /// </summary>
    internal static bool IsPokemonOriginHeartGold(ReadOnlySpan<byte> data) =>
        OriginHeartGold.OriginHeartGoldFormat.LooksLike(data);

    /// <summary>
    /// True when the bytes are a Fire Red Extended or Crystal Advance Redux save: FireRed's
    /// game code, 0xFF0-chunked retail blocks, and Pokémon records only those hacks write
    /// (see FireRedHacks.FireRedHackFormat). Exclusive with Radical Red, which keeps
    /// plaintext CFRU records, and with retail FireRed, whose checksum windows differ.
    /// </summary>
    internal static bool IsPokemonFireRedHack(ReadOnlySpan<byte> data) => FireRedHackRoute(data) is not null;

    /// <summary>The route of a Fire Red Extended or Crystal Advance Redux save, or null.</summary>
    internal static SaveFormat? FireRedHackRoute(ReadOnlySpan<byte> data)
    {
        if (data.Length < FireRedHacks.FireRedHackFormat.FileSize)
            return null;
        return FireRedHacks.FireRedHackFormat.Identify(SramPadding.Trim(data.ToArray())) switch
        {
            FireRedHacks.FireRedHackGame.FireRedExtended => SaveFormat.FireRedExtended,
            FireRedHacks.FireRedHackGame.CrystalAdvanceRedux => SaveFormat.CrystalAdvanceRedux,
            _ => null,
        };
    }

    /// <summary>The ROM-hack route these bytes take, in the documented order (Origin HeartGold, Unbound, GS
    /// Chronicles, CFRU, Recharged Yellow, Quetzal, Soul Gold, expansion, Elite Redux, Fire Red Extended / Crystal Advance Redux), or null for stock PKHeX.</summary>
    internal static SaveFormat? HackRoute(ReadOnlySpan<byte> data)
    {
        if (IsPokemonOriginHeartGold(data)) return SaveFormat.OriginHeartGold;
        if (IsPokemonUnbound(data)) return SaveFormat.Unbound;
        if (IsPokemonGsChronicles(data)) return SaveFormat.GsChronicles;
        if (IsPokemonRadicalRed(data)) return SaveFormat.RadicalRed;
        if (IsPokemonRechargedYellow(data)) return SaveFormat.RechargedYellow;
        if (IsPokemonQuetzal(data)) return SaveFormat.Quetzal;
        if (IsPokemonSoulGold(data)) return SaveFormat.SoulGold;
        if (IsPokemonExpansion(data)) return SaveFormat.Expansion;
        if (IsPokemonEliteRedux(data)) return SaveFormat.EliteRedux;
        if (FireRedHackRoute(data) is { } fireRed) return fireRed;
        return null;
    }
}
