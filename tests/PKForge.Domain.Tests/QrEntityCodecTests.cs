using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class QrEntityCodecTests
{
    /// <summary>Every entity file size a generation can produce must survive the envelope untouched.</summary>
    [Theory]
    [InlineData(33, 1)]   // Gen 1 stored
    [InlineData(80, 3)]   // Gen 3 party
    [InlineData(100, 3)]  // Gen 3 GameCube (Colosseum / XD)
    [InlineData(136, 4)]  // Gen 4
    [InlineData(136, 5)]  // Gen 5
    [InlineData(232, 6)]  // Gen 6
    [InlineData(232, 7)]  // Gen 7
    [InlineData(328, 8)]  // Gen 8 / Gen 9
    [InlineData(344, 8)]  // Legends: Arceus - the largest entity file
    public void RoundTripsEveryEntitySize(int size, int generation)
    {
        var entity = new byte[size];
        Random.Shared.NextBytes(entity); // arbitrary binary, 0x00 and 0xFF included, must pass through
        var payload = QrEntityCodec.MakePayload(entity, generation, "Pikachu");

        var parsed = QrEntityCodec.TryParse(payload);

        Assert.NotNull(parsed);
        Assert.Equal(generation, parsed.Generation);
        Assert.Equal("Pikachu", parsed.SpeciesName);
        Assert.Equal(entity, parsed.EntityBytes);
    }

    /// <summary>Pins the wire format: an unknown scanner must see exactly these bytes.</summary>
    [Fact]
    public void EnvelopeLayoutIsFixed()
    {
        var payload = QrEntityCodec.MakePayload([1, 2, 3], 7, "Mew");

        byte[] expected = [.. "PKF1"u8, 1, 7, 3, 0, 1, 2, 3, 3, .. "Mew"u8];
        Assert.Equal(expected, payload);
    }

    [Fact]
    public void RoundTripsNonAsciiSpeciesName()
    {
        var entity = new byte[136];
        Random.Shared.NextBytes(entity);

        var parsed = QrEntityCodec.TryParse(QrEntityCodec.MakePayload(entity, 5, "Flabébé"));

        Assert.NotNull(parsed);
        Assert.Equal("Flabébé", parsed.SpeciesName);
        Assert.Equal(entity, parsed.EntityBytes);
    }

    [Fact]
    public void RejectsEveryTruncation()
    {
        var payload = QrEntityCodec.MakePayload(new byte[328], 8, "Charizard");
        for (var cut = 0; cut < payload.Length; cut++)
            Assert.Null(QrEntityCodec.TryParse(payload.AsSpan(0, cut).ToArray()));
    }

    [Fact]
    public void RejectsCorruptEnvelopes()
    {
        var payload = QrEntityCodec.MakePayload([9, 8, 7], 4, "Eevee");

        Assert.Null(QrEntityCodec.TryParse([])); // empty
        Bad(payload, 0, (byte)'X'); // magic
        Bad(payload, 4, 2); // unknown envelope version
        Bad(payload, 5, 0); // generation below range
        Bad(payload, 5, 10); // generation above range
        Bad(payload, 6, 0); // zero entity length
        Bad(payload, 6, 200); // declared length beyond the payload

        byte[] trailing = [.. payload, 0];
        Assert.Null(QrEntityCodec.TryParse(trailing)); // junk after the name
    }

    [Fact]
    public void RejectsInvalidUtf8SpeciesName()
    {
        var payload = QrEntityCodec.MakePayload([1], 7, "Mew");
        payload[^1] = 0xFF; // never a valid UTF-8 byte on its own

        Assert.Null(QrEntityCodec.TryParse(payload));
    }

    [Fact]
    public void EncoderRejectsImpossibleInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QrEntityCodec.MakePayload([], 7, "Mew")); // empty entity
        Assert.Throws<ArgumentOutOfRangeException>(() => QrEntityCodec.MakePayload([1], 0, "Mew")); // no generation
        Assert.Throws<ArgumentOutOfRangeException>(() => QrEntityCodec.MakePayload([1], 10, "Mew")); // beyond Gen 9
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            QrEntityCodec.MakePayload([1], 7, new string('x', 256))); // name overflows the 1-byte length field
    }

    private static void Bad(byte[] payload, int offset, byte value)
    {
        payload[offset] = value;
        Assert.Null(QrEntityCodec.TryParse(payload));
    }
}
