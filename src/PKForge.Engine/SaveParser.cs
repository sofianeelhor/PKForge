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
    internal static void ApplyVersionHint(SaveFile save, string? displayName)
    {
        if (save is not SAV3RS || save.Version != GameVersion.RS || displayName is null) return;
        var tokens = System.Text.RegularExpressions.Regex.Split(displayName, @"[^\p{L}\p{N}]+");
        var ruby = tokens.Contains("Ruby", StringComparer.OrdinalIgnoreCase);
        var sapphire = tokens.Contains("Sapphire", StringComparer.OrdinalIgnoreCase);
        if (ruby != sapphire) save.Version = ruby ? GameVersion.R : GameVersion.S;
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
}
