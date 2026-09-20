using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Emulator SRAM dumps longer than their chip (VBA-M appends 8 KB of 0xFF). The
/// payload must survive untouched and the pad must come back on write, or a real
/// Emerald save is rejected and then written back the wrong size.
/// </summary>
public sealed class SramPaddingTests
{
    private static byte[] Dump(int payloadSize, int padSize, byte pad = 0xFF)
    {
        var data = new byte[payloadSize + padSize];
        for (var i = 0; i < payloadSize; i++)
            data[i] = (byte)(i % 251);
        for (var i = payloadSize; i < data.Length; i++)
            data[i] = pad;
        return data;
    }

    [Fact]
    public void TrimRemovesTheEmulatorPadAndKeepsThePayload()
    {
        var padded = Dump(0x20000, 0x2000);

        var trimmed = SramPadding.Trim(padded);

        Assert.Equal(0x20000, trimmed.Length);
        Assert.True(trimmed.AsSpan().SequenceEqual(padded.AsSpan(0, 0x20000)));
    }

    [Fact]
    public void ExactSizedDumpsAndRealDataAreLeftAlone()
    {
        var exact = Dump(0x20000, 0);
        Assert.Same(exact, SramPadding.Trim(exact));

        // Trailing bytes that are not a uniform erased run are real data, not padding.
        var data = Dump(0x20000, 0x2000);
        data[^1] = 0x42;
        Assert.Same(data, SramPadding.Trim(data));
    }

    [Fact]
    public void SmallerChipsTrimOnlyWhenTheSizeIsNotItselfAChip()
    {
        // 0x8000 of SRAM padded out to 0xC000: neither size is a chip, so the pad is
        // unambiguous and comes off.
        Assert.Equal(0x8000, SramPadding.Trim(Dump(0x8000, 0x4000)).Length);
        Assert.Equal(0x2000, SramPadding.Trim(Dump(0x2000, 0x1000)).Length);

        // But a file whose total size IS a chip size is never cut down, even when the
        // upper half is erased: 32 KB SRAM padded to 64 KB is indistinguishable from a
        // real 64 KB flash dump with an empty second slot, and truncating the wrong one
        // is destructive. PKHeX gets the exact-size file as-is.
        Assert.Equal(0x10000, SramPadding.Trim(Dump(0x8000, 0x8000)).Length);
        Assert.Equal(0x8000, SramPadding.Trim(Dump(0x2000, 0x6000)).Length);
    }

    [Fact]
    public void UnchangedPayloadComesBackByteIdenticalWithItsPad()
    {
        var padded = Dump(0x20000, 0x2000);
        var payload = SramPadding.Trim(padded);

        var written = SramPadding.Pad(payload, padded);

        Assert.Equal(padded.Length, written.Length);
        Assert.True(written.AsSpan().SequenceEqual(padded), "an unedited save must round trip byte for byte");
    }

    [Fact]
    public void EditedPayloadKeepsThePadShape()
    {
        var padded = Dump(0x20000, 0x2000);
        var payload = SramPadding.Trim(padded);
        payload[100] ^= 0xFF;

        var written = SramPadding.Pad(payload, padded);

        Assert.Equal(padded.Length, written.Length);
        Assert.True(written.AsSpan(0x20000).SequenceEqual(padded.AsSpan(0x20000)), "the pad must be re-applied");
        Assert.Equal((byte)(100 % 251 ^ 0xFF), written[100]);
    }

    [Fact]
    public void ExactChipSizeWithAnErasedUpperHalfIsNeverTrimmed()
    {
        // A full-flash Gen 3 dump whose second save slot was never written: the upper
        // 64 KB is all 0xFF. PKHeX models this shape natively (half-size flash), so the
        // patcher must leave it alone rather than hand the engine a smaller chip.
        var flash = new byte[0x20000];
        for (var i = 0; i < 0x10000; i++) flash[i] = (byte)(i % 251);
        for (var i = 0x10000; i < flash.Length; i++) flash[i] = 0xFF;

        Assert.Same(flash, SramPadding.Trim(flash));
        Assert.Equal(flash.Length, SramPadding.Pad(SramPadding.Trim(flash), flash).Length);

        // ...and the same for every exact chip size.
        foreach (var size in new[] { 0x20000, 0x10000, 0x8000, 0x2000 })
        {
            var exact = Dump(size, 0);
            for (var i = size / 2; i < size; i++) exact[i] = 0xFF;
            Assert.Same(exact, SramPadding.Trim(exact));
        }
    }

    [Fact]
    public void FormatsThisTypeWasNotMeantForAreNeverGrown()
    {
        // A DS-sized save that parsed on its own terms: the write path must not invent a
        // pad for it, whatever its tail looks like.
        var ds = new byte[0x80000];
        for (var i = 0; i < 0x20000; i++) ds[i] = (byte)(i % 251);
        for (var i = 0x20000; i < ds.Length; i++) ds[i] = 0xFF;

        var written = SramPadding.Pad(ds, ds);

        Assert.Equal(ds.Length, written.Length);
        Assert.True(written.AsSpan().SequenceEqual(ds));
    }

    [Fact]
    public void EditWriteReopenCyclesAreStable()
    {
        // Open the padded dump, edit, write, reopen, edit again: the file must keep its
        // size and its pad, and land byte-identical once the edits match the original.
        var padded = Dump(0x20000, 0x2000);
        var payload = SramPadding.Trim(padded);

        var edited = payload.ToArray();
        edited[1000] ^= 0xFF;
        var firstWrite = SramPadding.Pad(edited, padded);
        Assert.Equal(padded.Length, firstWrite.Length);

        // Second cycle starts from the file we wrote.
        var reopenedPayload = SramPadding.Trim(firstWrite);
        Assert.Equal(edited.Length, reopenedPayload.Length);
        Assert.True(reopenedPayload.AsSpan().SequenceEqual(edited), "the payload must survive a save cycle");

        var secondWrite = SramPadding.Pad(reopenedPayload, firstWrite);
        Assert.Equal(firstWrite.Length, secondWrite.Length);
        Assert.True(secondWrite.AsSpan().SequenceEqual(firstWrite), "a no-op edit must not drift the file");

        // And an edit that restores the original bytes gives the original file back.
        Assert.True(SramPadding.Pad(payload, firstWrite).AsSpan().SequenceEqual(padded));
    }

    [Fact]
    public void PadValueIsPreservedRatherThanReinvented()
    {
        // Some dumps pad with 0x00; whatever the file used is what comes back.
        var padded = Dump(0x20000, 0x2000, pad: 0x00);
        var payload = SramPadding.Trim(padded);
        payload[5] ^= 0xFF;

        var written = SramPadding.Pad(payload, padded);

        Assert.True(written.AsSpan(0x20000).IndexOfAnyExcept((byte)0x00) < 0, "the original pad value must be kept");
    }

    [Fact]
    public void DumpsWithoutPaddingAreWrittenAsTheyAre()
    {
        var exact = Dump(0x20000, 0);
        var payload = SramPadding.Trim(exact);
        Assert.Equal(0x20000, SramPadding.Pad(payload, exact).Length);
    }
}
