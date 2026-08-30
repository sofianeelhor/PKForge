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
        return SaveUtil.TryGetSaveFile(data, out save);
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
}
