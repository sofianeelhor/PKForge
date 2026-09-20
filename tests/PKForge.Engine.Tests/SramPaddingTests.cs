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
    public void LargerChipsTrimToTheirOwnSize()
    {
        // A 32 KB SRAM save padded out to 64 KB, and an 8 KB one padded to 32 KB.
        Assert.Equal(0x8000, SramPadding.Trim(Dump(0x8000, 0x8000)).Length);
        Assert.Equal(0x2000, SramPadding.Trim(Dump(0x2000, 0x6000)).Length);
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
    public void DumpsWithoutPaddingAreWrittenAsTheyAre()
    {
        var exact = Dump(0x20000, 0);
        var payload = SramPadding.Trim(exact);
        Assert.Equal(0x20000, SramPadding.Pad(payload, exact).Length);
    }
}
