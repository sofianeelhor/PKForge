using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine.GsChronicles;
using PKForge.Engine.Unbound;
using PKHeX.Core;

namespace PKForge.Engine.RadicalRed;

/// <summary>
/// The entity format of a CFRU Pokémon (Bank entries, exports): the 58-byte compact PC
/// record exactly as the game boxes it, tagged with the hack it came from. Every id in
/// it is the hack's own, so no PKHeX format can carry it: the old .pk3 export reset the
/// ball, friendship, met data, markings, language and hidden ability, and could not hold
/// any species past Deoxys at all. The game it came from takes these bytes back as they
/// are; every other game gets the PK3 conversion its session has always made
/// (<see cref="ToPk3"/>).
/// </summary>
internal static class CfruEntity
{
    public const string RadicalRed = "PK3RR";
    public const string GsChronicles = "PK3GSC";
    public const string Unbound = "PK3UB";

    public static readonly IReadOnlyList<string> Formats = [RadicalRed, GsChronicles, Unbound];

    /// <summary>The CFRU format a recorded format (type name, extension or file name) names, else null.</summary>
    public static string? Recognize(string? format) =>
        EntityBytes.Normalize(format) is { } name && Formats.Contains(name) ? name : null;

    /// <summary>The game whose tables read <paramref name="format"/>, for refusals.</summary>
    public static string GameName(string format) => format switch
    {
        RadicalRed => "Radical Red",
        GsChronicles => "GS Chronicles",
        _ => "Unbound",
    };

    /// <summary>
    /// The compact record of a 100-byte party mon, byte for byte what the game stores
    /// when it deposits it (CFRU's CompressedPokemon): the header up to the markings,
    /// growth up to the ball, moves packed 10 bits each, EVs, then pokérus, met data and
    /// the IV word with its egg and hidden-ability bits. The party-only tail (status,
    /// level, stats, current PP, contest stats, ribbons) is not part of a boxed mon.
    /// </summary>
    public static byte[] Compact(ReadOnlySpan<byte> party)
    {
        var compact = new byte[RadicalRedFormat.PcMonSize];
        party[..0x1C].CopyTo(compact);
        party[0x20..0x2B].CopyTo(compact.AsSpan(0x1C));
        ulong packed = 0;
        for (var i = 0; i < 4; i++)
            packed |= (ulong)(BinaryPrimitives.ReadUInt16LittleEndian(party[(0x2C + i * 2)..]) & 0x3FF) << (10 * i);
        for (var i = 0; i < 5; i++)
            compact[0x27 + i] = (byte)(packed >> (8 * i));
        party[0x38..0x3E].CopyTo(compact.AsSpan(0x2C));
        party[0x44..0x4C].CopyTo(compact.AsSpan(0x32));
        return compact;
    }

    /// <summary>
    /// The reverse of <see cref="Compact"/>, as the game withdraws a boxed mon: every
    /// stored field lands where the party form keeps it and the moves get full PP
    /// (<paramref name="basePp"/> is the hack's table). The caller computes the tail
    /// (level, stats, HP) through its own tables.
    /// </summary>
    public static void Expand(ReadOnlySpan<byte> compact, Span<byte> party, Func<int, int> basePp)
    {
        party[..RadicalRedFormat.PartyMonSize].Clear();
        compact[..0x1C].CopyTo(party);
        compact[0x1C..0x27].CopyTo(party[0x20..]);
        ulong packed = 0;
        for (var i = 0; i < 5; i++)
            packed |= (ulong)compact[0x27 + i] << (8 * i);
        var ppBonuses = compact[0x24];
        for (var i = 0; i < 4; i++)
        {
            var move = (int)((packed >> (10 * i)) & 0x3FF);
            BinaryPrimitives.WriteUInt16LittleEndian(party[(0x2C + i * 2)..], (ushort)move);
            var pp = move == 0 ? 0 : basePp(move);
            party[0x34 + i] = (byte)(pp + pp * 20 * ((ppBonuses >> (2 * i)) & 3) / 100); // CalculatePPWithBonus
        }
        compact[0x2C..0x32].CopyTo(party[0x38..]);
        compact[0x32..0x3A].CopyTo(party[0x44..]);
    }

    /// <summary>What the Bank shows for these bytes, or null when they are not a mon of that game.</summary>
    public static BankEntryInfo? Describe(byte[] bytes, string format, string sourceName)
    {
        if (bytes.Length != RadicalRedFormat.PcMonSize) return null;
        if (format == Unbound)
        {
            var mon = new UnboundMon(bytes.ToArray(), 0, party: false);
            return mon.LooksValid ? UnboundEngineSession.Describe(mon, sourceName) : null;
        }
        var cfru = new RadicalRedMon(bytes.ToArray(), 0, false, DataOf(format));
        return cfru.LooksValid ? CfruEngineSession.Describe(cfru, format, sourceName) : null;
    }

    /// <summary>The PK3 every other game imports, or null when the bytes are not a mon of that
    /// game or one of its ids has no Generation 3 counterpart (<see cref="Pk3Refusal"/> says which).</summary>
    public static PK3? ToPk3(byte[] bytes, string format) => ToPk3(bytes, format, out _);

    /// <summary>What changed for these bytes to fit Generation 3 on their way to another game
    /// (a ball or ability it lacks); empty when nothing did or they do not convert.</summary>
    public static IReadOnlyList<string> Pk3Adjustments(byte[] bytes, string format)
    {
        var adjustments = new List<string>();
        ToPk3(bytes, format, out _, adjustments);
        return adjustments;
    }

    /// <summary>Why these bytes have no PK3 for other games, or null when they convert.</summary>
    public static string? Pk3Refusal(byte[] bytes, string format)
    {
        ToPk3(bytes, format, out var refusal);
        return refusal;
    }

    private static PK3? ToPk3(byte[] bytes, string format, out string? refusal, List<string>? adjustments = null)
    {
        refusal = null;
        if (bytes.Length != RadicalRedFormat.PcMonSize) return null;
        if (format == Unbound)
        {
            var mon = new UnboundMon(bytes.ToArray(), 0, party: false);
            return mon.LooksValid ? UnboundEngineSession.ToPk3(mon, out refusal, adjustments) : null;
        }
        var cfru = new RadicalRedMon(bytes.ToArray(), 0, false, DataOf(format));
        return cfru.LooksValid ? CfruEngineSession.ToPk3(cfru, out refusal, adjustments) : null;
    }

    /// <summary>Why <paramref name="entity"/> cannot enter the CFRU game whose snapshot reports
    /// <paramref name="snapshotTag"/> (read through <see cref="CfruPk3.Source"/>, like the import
    /// itself), or null when it can or the tag names no CFRU game.</summary>
    public static string? ImportRefusal(PKM entity, string snapshotTag)
    {
        Land(entity, snapshotTag, out var refusal, null);
        return refusal;
    }

    /// <summary>What changes for <paramref name="entity"/> to fit the CFRU game whose snapshot
    /// reports <paramref name="snapshotTag"/> (an ability the hack does not give it); empty when
    /// nothing does, it is refused, or the tag names no CFRU game.</summary>
    public static IReadOnlyList<string> ImportAdjustments(PKM entity, string snapshotTag)
    {
        var adjustments = new List<string>();
        return Land(entity, snapshotTag, out _, adjustments) ? adjustments : [];
    }

    private static bool Land(PKM entity, string snapshotTag, out string? refusal, List<string>? adjustments)
    {
        refusal = null;
        var unbound = snapshotTag == UnboundEngineSession.SnapshotTag;
        var profile = Array.Find(Profiles, p => p.SnapshotTag == snapshotTag);
        if (!unbound && profile is null) return false;
        if (CfruPk3.Source(entity) is not { } source) return false;
        return unbound
            ? UnboundEngineSession.Landing(source, out refusal, adjustments) is not null
            : CfruEngineSession.Landing(source, profile!.Data, profile.GameName, out refusal, adjustments) is not null;
    }

    private static readonly CfruGameProfile[] Profiles = [RadicalRedEngineSession.Profile, GsChroniclesEngineSession.Profile];

    /// <summary>Egg flag, gender (0/1/2), PKHeX ball and held item for the Bank's sorts and
    /// filters, read through the hack's tables; null when the bytes are not a mon.</summary>
    public static (bool IsEgg, int Gender, int Ball, int HeldItem)? Facts(byte[] bytes, string format)
    {
        if (Describe(bytes, format, string.Empty) is not { } info) return null;
        if (format == Unbound)
        {
            var mon = new UnboundMon(bytes.ToArray(), 0, party: false);
            return (mon.IsEgg, UnboundData.GenderOf(mon.Pid, mon.Species), mon.DisplayBall, info.HeldItem ?? 0);
        }
        var cfru = new RadicalRedMon(bytes.ToArray(), 0, false, DataOf(format));
        return (cfru.IsEgg, cfru.Data.GenderOf(cfru.Pid, cfru.Species), cfru.DisplayBall, info.HeldItem ?? 0);
    }

    /// <summary>
    /// A throwaway session of the record's own game, the record in box 1 slot 1 of an
    /// otherwise blank save, so the Bank's summary and viewers read it through the hack's
    /// tables (species past 386 included). Exports from it are the same exact record;
    /// nothing in it is ever written to a file. Null when the bytes are not a mon.
    /// </summary>
    public static ISaveEngineSession? Host(byte[] bytes, string format, string? displayName)
    {
        if (Describe(bytes, format, string.Empty) is null) return null;
        ISaveEngineSession session = format switch
        {
            Unbound => new UnboundEngineSession(BlankImage(SaveParser.UnboundSectorSignature), displayName),
            GsChronicles => CfruEngineSession.Blank(GsChroniclesEngineSession.Profile,
                BlankImage(SaveParser.GsChroniclesSectorSignature), displayName),
            _ => CfruEngineSession.Blank(RadicalRedEngineSession.Profile, BlankImage(RetailSignature), displayName),
        };
        session.ImportSlot(0, 0, bytes, format);
        return session;
    }

    internal const uint RetailSignature = 0x0801_2025;

    /// <summary>One slot's 14 sections, empty but for footers (id, signature, save index).</summary>
    internal static byte[] BlankImage(uint signature)
    {
        var image = new byte[RadicalRedFormat.FileSize];
        for (var id = 0; id < RadicalRedFormat.SectionCount; id++)
        {
            var footer = id * RadicalRedFormat.SectorSize;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(footer + 0xFF4), (ushort)id);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(footer + 0xFF8), signature);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(footer + 0xFFC), 1);
        }
        return image;
    }

    private static ICfruGameData DataOf(string format) =>
        format == GsChronicles ? GsChroniclesData.Instance : RadicalRedGameData.Instance;
}
