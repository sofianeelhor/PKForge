using System.Buffers.Binary;
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
        if ((chosen ?? EditionFromOwnPokemon(save)) is { } edition)
            save.Version = edition;
    }

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

    private static IEnumerable<PKM> OwnedEntities(SaveFile save)
    {
        if (save.HasParty)
            for (var i = 0; i < save.PartyCount; i++)
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
}
