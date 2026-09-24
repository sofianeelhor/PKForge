namespace PKForge.Engine;

/// <summary>
/// Raw emulator SRAM dumps are routinely longer than the chip they came from: VBA-M
/// and friends append 8 KB of 0xFF, and the flash's own erased tail adds more on top.
/// PKHeX checks save sizes exactly, so a perfectly good dump gets rejected outright.
///
/// Two rules keep the patch/unpatch cycle safe, because a save is opened and written
/// over and over:
/// <list type="bullet">
/// <item>An exact chip size is never trimmed. A full-flash Gen 3 save whose second save
/// slot was never written has an all-0xFF upper half, and PKHeX already models that
/// shape (half-size flash); trimming it would hand the engine a smaller chip than the
/// file really is.</item>
/// <item>Padding is only ever what was dropped, measured from the payload length that
/// came back out. Nothing is inferred from chip sizes on the write path, so a format
/// this type was never meant for (a DS save, say) can never be grown.</item>
/// </list>
/// An unedited save therefore round trips byte-identically, and edit/write cycles are
/// stable: the same file in gives the same file out, byte for byte.
/// </summary>
internal static class SramPadding
{
    /// <summary>Game Boy Advance save-chip sizes: two flash sizes, then two SRAM sizes.</summary>
    private static readonly int[] ChipSizes = [0x20000, 0x10000, 0x8000, 0x2000];

    /// <summary>The dump without its trailing padding; the same bytes when there is none.</summary>
    internal static byte[] Trim(byte[] data)
    {
        var paddingLength = PaddingLength(data);
        return paddingLength == 0 ? data : data[..^paddingLength];
    }

    /// <summary>
    /// mGBA stores a real-time-clock cartridge's RTC state as 16 bytes appended to the flash
    /// dump (Unbound, Emerald and Ruby/Sapphire carry an RTC). Unlike padding it is live
    /// data, so it is dropped for parsing and handed back verbatim on write.
    /// </summary>
    private const int RtcFooterLength = 16;

    private static bool IsRtcFooter(int fileLength) =>
        fileLength - RtcFooterLength is 0x20000 or 0x10000;

    /// <summary>
    /// How many trailing bytes an oversized dump carries beyond its chip, or 0 when the
    /// file is an exact chip size or its tail is not an erased run. The largest chip that
    /// fits wins, so a flash dump whose upper half is erased is never cut down to SRAM.
    /// </summary>
    private static int PaddingLength(ReadOnlySpan<byte> original)
    {
        // An exact chip size means the dump is the chip: nothing was appended, and a
        // smaller chip must never be inferred from an erased tail.
        if (Array.IndexOf(ChipSizes, original.Length) >= 0) return 0;
        if (IsRtcFooter(original.Length)) return RtcFooterLength;

        foreach (var size in ChipSizes)
        {
            if (size >= original.Length) continue;
            var pad = original[size..];
            if (IsUniformPad(pad)) return pad.Length;
        }
        return 0;
    }

    /// <summary>
    /// The file to write back for a parsed payload: the dropped tail re-applied inside the
    /// RetroArch container (the pad and RTC footer are part of what the core wrote, so they
    /// sit inside the rzip stream), then re-wrapped exactly like the original.
    /// </summary>
    internal static byte[] Restore(ReadOnlySpan<byte> payload, byte[] original) =>
        RetroArchSaveContainer.Repack(Pad(payload, RetroArchSaveContainer.Decode(original)), original);

    /// <summary>
    /// Re-applies exactly the bytes that were dropped at open, and only when the payload
    /// is what the rest of the original file already was. An unchanged payload is handed
    /// back byte-for-byte so the safe writer still sees "nothing changed".
    /// </summary>
    internal static byte[] Pad(ReadOnlySpan<byte> payload, ReadOnlySpan<byte> original)
    {
        if (payload.Length >= original.Length) return payload.ToArray(); // nothing was dropped
        var dropped = original[payload.Length..];
        var rtcFooter = dropped.Length == RtcFooterLength && IsRtcFooter(original.Length);
        if (!rtcFooter && !IsUniformPad(dropped)) return payload.ToArray(); // not ours to re-append
        if (payload.SequenceEqual(original[..payload.Length])) return original.ToArray();

        var result = new byte[original.Length];
        payload.CopyTo(result);
        dropped.CopyTo(result.AsSpan(payload.Length));
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
