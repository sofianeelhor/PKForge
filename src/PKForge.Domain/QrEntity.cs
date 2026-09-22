using System.Buffers.Binary;
using System.Text;

namespace PKForge.Domain;

/// <summary>One entity carried by a PKF1 QR envelope: the raw .pk bytes plus their provenance.</summary>
public sealed record QrEntityPayload(int Generation, byte[] EntityBytes, string SpeciesName);

/// <summary>
/// The PKF1 binary .pk QR envelope: PKSM-style mon transfer where the QR's byte
/// segment carries the whole entity file, not a text rendering of it.
///
/// Layout (multi-byte fields little-endian):
///   offset 0      4 bytes   magic "PKF1"
///   offset 4      1 byte    envelope version (currently 1)
///   offset 5      1 byte    entity generation (1-9)
///   offset 6      2 bytes   entity length N
///   offset 8      N bytes   raw .pk file bytes, exactly as ExportSlot writes them
///   offset 8+N    1 byte    species-name length M (UTF-8 bytes)
///   offset 9+N    M bytes   species name, UTF-8 - human confirmation before import
///
/// The largest entity file is 344 bytes (Legends: Arceus), so the envelope tops out
/// near 365 bytes with a full species name - well inside a single QR code even at
/// error-correction level M (a version-40 code holds 2331 byte-mode bytes). No
/// chunked multi-QR split exists or is needed.
/// </summary>
public static class QrEntityCodec
{
    public const string Magic = "PKF1";
    public const byte CurrentVersion = 1;
    private const int HeaderSize = 8; // magic + version + generation + length
    private const int MaxGeneration = 9;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Wraps raw .pk bytes (as ExportSlot produces them) in a PKF1 envelope.</summary>
    public static byte[] MakePayload(byte[] entityBytes, int generation, string speciesName)
    {
        ArgumentNullException.ThrowIfNull(entityBytes);
        ArgumentNullException.ThrowIfNull(speciesName);
        ArgumentOutOfRangeException.ThrowIfZero(entityBytes.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(generation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(generation, MaxGeneration);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(entityBytes.Length, ushort.MaxValue);
        var name = StrictUtf8.GetBytes(speciesName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(name.Length, byte.MaxValue);

        var payload = new byte[HeaderSize + entityBytes.Length + name.Length + 1];
        Encoding.Latin1.GetBytes(Magic, payload.AsSpan(0, Magic.Length));
        payload[Magic.Length] = CurrentVersion;
        payload[Magic.Length + 1] = (byte)generation;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(Magic.Length + 2, 2), (ushort)entityBytes.Length);
        entityBytes.AsSpan().CopyTo(payload.AsSpan(HeaderSize));
        payload[HeaderSize + entityBytes.Length] = (byte)name.Length;
        name.AsSpan().CopyTo(payload.AsSpan(HeaderSize + entityBytes.Length + 1));
        return payload;
    }

    /// <summary>Reads a PKF1 envelope back; null unless every byte is accounted for exactly.</summary>
    public static QrEntityPayload? TryParse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize + 1) return null;
        if (!payload.StartsWith("PKF1"u8)) return null;
        if (payload[Magic.Length] != CurrentVersion) return null;
        var generation = payload[Magic.Length + 1];
        if (generation is < 1 or > MaxGeneration) return null;
        var length = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(Magic.Length + 2, 2));
        if (length == 0 || HeaderSize + length + 1 > payload.Length) return null;
        var nameLength = payload[HeaderSize + length];
        if (HeaderSize + length + 1 + nameLength != payload.Length) return null; // trailing bytes mean corruption
        string name;
        try
        {
            name = StrictUtf8.GetString(payload.Slice(HeaderSize + length + 1, nameLength));
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
        return new QrEntityPayload(generation, payload.Slice(HeaderSize, length).ToArray(), name);
    }
}
