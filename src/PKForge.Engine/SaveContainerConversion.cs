using System.Buffers.Binary;
using System.Text;

namespace PKForge.Engine;

/// <summary>The file shapes one save can take from one emulator to another.</summary>
public enum SaveContainer
{
    /// <summary>The cartridge's memory as it is: a .sav (mGBA, melonDS, most emulators).</summary>
    Raw,
    /// <summary>RetroArch's .srm: the raw memory, or that memory in RetroArch's rzip envelope.</summary>
    RetroArch,
    /// <summary>DraStic's and DeSmuME's .dsv: the raw memory then DeSmuME's footer.</summary>
    DeSmuME,
}

/// <summary>A conversion's outcome: the new file's name and bytes, or why it was refused.</summary>
public sealed record SaveConversion(string? FileName, byte[]? Bytes, string? Refusal)
{
    public bool Succeeded => Bytes is not null;
}

/// <summary>
/// Moves a save from one emulator's file shape to another's without touching what the game
/// wrote. Every conversion is checked by converting back: unless the way back gives the very
/// bytes the player started from, nothing is offered. The original file is never written.
/// </summary>
public static class SaveContainerConversion
{
    // DeSmuME's footer (desmume/src/mc.cpp, BackupDevice::ensure): the text, six little-endian
    // words (bytes written, padded size, memory type, address size, memory size, version 0)
    // and the cookie.
    private static readonly byte[] FooterText = Encoding.ASCII.GetBytes("|<--Snip above here to create a raw sav by excluding this DeSmuME savedata footer:");
    private static readonly byte[] Cookie = Encoding.ASCII.GetBytes("|-DESMUME SAVE-|");
    private static int FooterLength => FooterText.Length + 24 + Cookie.Length;

    // The flash sizes DS Pokémon games use, with DeSmuME's save_types index and address size.
    private static (int Type, int AddressSize)? DeSmuMEType(int size) => size switch
    {
        0x40000 => (5, 3),  // FLASH 2Mbit
        0x80000 => (6, 3),  // FLASH 4Mbit: every DS Pokémon game
        0x100000 => (7, 3), // FLASH 8Mbit
        _ => null,
    };

    public static string Extension(SaveContainer container) => container switch
    {
        SaveContainer.RetroArch => ".srm",
        SaveContainer.DeSmuME => ".dsv",
        _ => ".sav",
    };

    /// <summary>The shape of <paramref name="bytes"/>, told by the bytes first, then the file name.</summary>
    public static SaveContainer Detect(ReadOnlySpan<byte> bytes, string fileName)
    {
        if (RetroArchSaveContainer.IsCompressed(bytes)) return SaveContainer.RetroArch;
        if (ReadFooter(bytes) is not null) return SaveContainer.DeSmuME;
        return string.Equals(Path.GetExtension(fileName), ".srm", StringComparison.OrdinalIgnoreCase)
            ? SaveContainer.RetroArch : SaveContainer.Raw;
    }

    /// <summary>The shapes this save can be written as, each with what converting says.</summary>
    public static IReadOnlyList<(SaveContainer Target, SaveConversion Result)> Options(ReadOnlySpan<byte> bytes, string fileName, int generation)
    {
        var from = Detect(bytes, fileName);
        var options = new List<(SaveContainer, SaveConversion)>();
        foreach (var target in Enum.GetValues<SaveContainer>())
            if (target != from) options.Add((target, Convert(bytes, fileName, generation, target)));
        return options;
    }

    /// <summary>
    /// <paramref name="bytes"/> as a <paramref name="target"/> file named after <paramref name="fileName"/>,
    /// or the reason it cannot be done without loss.
    /// </summary>
    public static SaveConversion Convert(ReadOnlySpan<byte> bytes, string fileName, int generation, SaveContainer target)
    {
        var from = Detect(bytes, fileName);
        if (from == target) return Refuse("The save is already in that format.");
        if (generation is not (1 or 2 or 3 or 4 or 5))
            return Refuse("Only cartridge saves (Game Boy to DS) are plain memory that another emulator can take as it is.");
        if (target == SaveContainer.DeSmuME && generation is not (4 or 5))
            return Refuse("The .dsv format is for DS games only.");

        byte[] raw;
        try { raw = Unwrap(bytes, from); }
        catch (InvalidDataException error) { return Refuse($"The file could not be read as {Extension(from)}: {error.Message}"); }
        if (raw.Length == 0) return Refuse("The file is empty.");

        var output = Wrap(raw, target, out var refusal);
        if (output is null) return Refuse(refusal!);

        // The proof: the way back gives the game's memory exactly, and for an uncompressed
        // source the very file the player has.
        byte[] back;
        try { back = Unwrap(output, target); }
        catch (InvalidDataException) { return Refuse("The converted file did not read back."); }
        if (!back.AsSpan().SequenceEqual(raw)) return Refuse("Converting back did not give the same save, so nothing was written.");
        if (from != SaveContainer.RetroArch || !RetroArchSaveContainer.IsCompressed(bytes))
        {
            var again = Wrap(back, from, out _);
            if (again is null || !again.AsSpan().SequenceEqual(bytes))
                return Refuse(from == SaveContainer.DeSmuME
                    ? "This .dsv footer is not the one DeSmuME writes for its size, so it could not be rebuilt on the way back."
                    : "Converting back did not give the same file, so nothing was written.");
        }
        return new SaveConversion(Path.GetFileNameWithoutExtension(fileName) + Extension(target), output, null);
    }

    private static SaveConversion Refuse(string reason) => new(null, null, reason);

    /// <summary>The game's memory inside a file of shape <paramref name="from"/>.</summary>
    internal static byte[] Unwrap(ReadOnlySpan<byte> bytes, SaveContainer from)
    {
        switch (from)
        {
            case SaveContainer.RetroArch: return RetroArchSaveContainer.Decode(bytes);
            case SaveContainer.DeSmuME:
                var footer = ReadFooter(bytes) ?? throw new InvalidDataException("No DeSmuME footer.");
                return bytes[..footer.PadSize].ToArray();
            default: return bytes.ToArray();
        }
    }

    /// <summary>The memory as a file of shape <paramref name="target"/>; a .srm is written uncompressed, which RetroArch reads.</summary>
    internal static byte[]? Wrap(ReadOnlySpan<byte> raw, SaveContainer target, out string? refusal)
    {
        refusal = null;
        if (target != SaveContainer.DeSmuME) return raw.ToArray();
        if (DeSmuMEType(raw.Length) is not { } type)
        {
            refusal = $"A {raw.Length:N0}-byte save is not a DS flash size PKForge knows the .dsv footer for.";
            return null;
        }
        var output = new byte[raw.Length + FooterLength];
        raw.CopyTo(output);
        var at = raw.Length;
        FooterText.CopyTo(output, at);
        at += FooterText.Length;
        foreach (var word in new[] { raw.Length, raw.Length, type.Type, type.AddressSize, raw.Length, 0 })
        {
            BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(at), word);
            at += 4;
        }
        Cookie.CopyTo(output, at);
        return output;
    }

    private sealed record Footer(int Size, int PadSize);

    /// <summary>DeSmuME's footer at the end of <paramref name="bytes"/>, or null when there is none whole.</summary>
    private static Footer? ReadFooter(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= FooterLength || !bytes.EndsWith(Cookie)) return null;
        var words = bytes[^(Cookie.Length + 24)..^Cookie.Length];
        var size = BinaryPrimitives.ReadInt32LittleEndian(words);
        var padSize = BinaryPrimitives.ReadInt32LittleEndian(words[4..]);
        var version = BinaryPrimitives.ReadInt32LittleEndian(words[20..]);
        if (version != 0 || padSize <= 0 || size < 0 || size > padSize) return null;
        if (bytes.Length != padSize + FooterLength || !bytes.Slice(padSize, FooterText.Length).SequenceEqual(FooterText)) return null;
        return new Footer(size, padSize);
    }
}
