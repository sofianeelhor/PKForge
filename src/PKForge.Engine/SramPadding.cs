namespace PKForge.Engine;

/// <summary>
/// Raw emulator SRAM dumps are routinely longer than the chip they came from: VBA-M
/// and friends append 8 KB of 0xFF, and the flash's own erased tail adds more on top.
/// PKHeX checks save sizes exactly, so a perfectly good dump gets rejected outright.
///
/// Trimming never alters one payload byte, and the pad is remembered from the original
/// file and re-applied on write, so the emulator still finds the file the size it
/// expects and an unedited save round trips byte-identically.
/// </summary>
internal static class SramPadding
{
    /// <summary>Game Boy Advance save-chip sizes: two flash sizes, then two SRAM sizes.</summary>
    private static readonly int[] ChipSizes = [0x20000, 0x10000, 0x8000, 0x2000];

    /// <summary>The dump without its trailing padding; the same bytes when there is none.</summary>
    internal static byte[] Trim(byte[] data)
    {
        var padding = PaddingOf(data);
        return padding.IsEmpty ? data : data[..^padding.Length];
    }

    /// <summary>The trailing padding an oversized dump carries, or empty when it is exact.</summary>
    internal static ReadOnlySpan<byte> PaddingOf(ReadOnlySpan<byte> original)
    {
        foreach (var size in ChipSizes)
        {
            if (size >= original.Length) continue;
            var pad = original[size..];
            if (IsUniformPad(pad)) return pad;
        }
        return default;
    }

    /// <summary>
    /// Re-applies the original file's padding. An unchanged payload is handed back
    /// byte-for-byte, so the safe writer's "nothing to write" check still sees equality.
    /// </summary>
    internal static byte[] Pad(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> original)
    {
        var padding = PaddingOf(original);
        if (padding.IsEmpty) return payload.ToArray();
        if (payload.SequenceEqual(original[..^padding.Length])) return original.ToArray();

        var result = new byte[payload.Length + padding.Length];
        payload.CopyTo(result);
        padding.CopyTo(result.AsSpan(payload.Length));
        return result;
    }

    /// <summary>Padding is a non-empty run of one erased value: flash 0xFF or SRAM 0x00.</summary>
    private static bool IsUniformPad(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty) return false;
        var value = span[0];
        if (value is not (0xFF or 0x00)) return false;
        // Vectorised scan: a multi-megabyte tail is the only case that costs anything.
        return span.IndexOfAnyExcept(value) < 0;
    }
}
