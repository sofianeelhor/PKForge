using System.Buffers.Binary;
using System.IO.Compression;

namespace PKForge.Engine;

/// <summary>
/// RetroArch rzip v1 battery-save envelope: 20-byte header, then independently
/// zlib-compressed chunks prefixed by their little-endian compressed lengths.
/// Format: libretro-common/streams/rzip_stream.c. Never infer this from .srm alone.
/// </summary>
internal static class RetroArchSaveContainer
{
    private const int MaxSaveSize = 32 * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "#RZIPv\x01#"u8;
    internal static bool IsCompressed(ReadOnlySpan<byte> bytes) => bytes.StartsWith("#RZIP"u8);

    internal static byte[] Decode(ReadOnlySpan<byte> bytes)
    {
        if (!IsCompressed(bytes)) return bytes.ToArray();
        if (bytes.Length < 20 || !bytes.StartsWith(Magic))
            throw new InvalidDataException("Unsupported or incomplete RetroArch compressed save header.");
        var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var length = BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..]);
        if (chunkSize is 0 or > 64 * 1024 * 1024 || length is 0 or > MaxSaveSize)
            throw new InvalidDataException("Invalid RetroArch compressed save size (maximum 32 MB).");
        var output = new byte[(int)length];
        var position = 20;
        for (var offset = 0; offset < output.Length;)
        {
            if (bytes.Length - position < 4) throw Truncated();
            var compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes[position..]);
            position += 4;
            if (compressedLength < 6 || compressedLength > bytes.Length - position || compressedLength > Math.Max(chunkSize * 2UL, chunkSize + 64UL))
                throw Truncated();
            var count = (int)Math.Min(chunkSize, (uint)(output.Length - offset));
            using var input = new MemoryStream(bytes.Slice(position, (int)compressedLength).ToArray(), writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            try { zlib.ReadExactly(output.AsSpan(offset, count)); }
            catch (EndOfStreamException error) { throw new InvalidDataException("Truncated RetroArch compressed save chunk.", error); }
            if (zlib.ReadByte() != -1)
                throw new InvalidDataException("RetroArch compressed save chunk exceeds its declared size.");
            // ZLibStream can report EOF for a truncated trailer after returning all
            // payload bytes. Require the Adler-32 trailer explicitly as well.
            var expectedChecksum = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(position + (int)compressedLength - 4, 4));
            if (Adler32(output.AsSpan(offset, count)) != expectedChecksum)
                throw new InvalidDataException("Missing or invalid RetroArch compressed save checksum.");
            position += (int)compressedLength;
            offset += count;
        }
        if (position != bytes.Length)
            throw new InvalidDataException("Unexpected trailing data in RetroArch compressed save.");
        return output;
    }

    internal static byte[] Repack(ReadOnlySpan<byte> data, ReadOnlySpan<byte> original)
    {
        if (!IsCompressed(original)) return data.ToArray();
        // Keep the exact original compression when the payload was unchanged.
        if (data.SequenceEqual(Decode(original))) return original.ToArray();
        return Encode(data, (int)BinaryPrimitives.ReadUInt32LittleEndian(original[8..]));
    }

    internal static byte[] Encode(ReadOnlySpan<byte> data, int chunkSize = 131072)
    {
        if (data.Length is 0 or > MaxSaveSize || chunkSize is <= 0 or > 64 * 1024 * 1024)
            throw new InvalidDataException("Invalid RetroArch save size.");
        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[20];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)chunkSize);
        BinaryPrimitives.WriteUInt64LittleEndian(header[12..], (ulong)data.Length);
        output.Write(header);
        Span<byte> size = stackalloc byte[4];
        for (var offset = 0; offset < data.Length;)
        {
            var count = Math.Min(chunkSize, data.Length - offset);
            using var chunk = new MemoryStream();
            using (var zlib = new ZLibStream(chunk, CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(data.Slice(offset, count));
            BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)chunk.Length);
            output.Write(size);
            chunk.Position = 0;
            chunk.CopyTo(output);
            offset += count;
        }
        return output.ToArray();
    }

    private static uint Adler32(ReadOnlySpan<byte> data)
    {
        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    private static InvalidDataException Truncated() => new("Invalid or truncated RetroArch compressed save chunk.");
}
