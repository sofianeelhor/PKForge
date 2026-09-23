using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Mon-level conversion between PKSM bank payloads and PKForge bank bytes. PKSM stores each
/// mon as PKSM-Core's decrypted <c>rawData</c>: Gen I/II as the single-mon list record with
/// names (PKHeX's SIZE_*LIST), Gen III-VIII as the decrypted box structure. PKForge's bank
/// stores decrypted party data (what <c>ExportSlot</c> writes), and Gen I/II as the same
/// list record so names survive.
/// <para>
/// Generation numbers here are chronological (1-9) plus a Let's Go flag; the PKSM tag
/// numbering is mapped by the infrastructure layer.
/// </para>
/// </summary>
public static class PksmEntityConversion
{
    private const int PksmLetsGoBoxLength = 232;
    // Single-mon list records (PKSM-Core PK1/PK2 *_LENGTH_WITH_NAMES = PKHeX SIZE_*LIST): 59/69, 63/73.

    /// <summary>Decoded bank bytes plus facts, or the reason the mon was left behind.</summary>
    public readonly record struct Decoded(byte[]? Bytes, BankEntryInfo? Info, string? Reason);

    /// <summary>A PKSM payload for one mon (<paramref name="Generation"/> 0 = none), or the reason.</summary>
    public readonly record struct Encoded(int Generation, bool LetsGo, byte[]? Data, string? Reason);

    /// <summary>
    /// Builds the entity PKSM tagged as <paramref name="generation"/> (null: detect from the
    /// bytes, as for a loose PKHeX file) and checks it before it may enter the bank.
    /// </summary>
    public static Decoded Decode(int? generation, bool letsGo, byte[] data, string sourceName)
    {
        PKM? pk;
        try
        {
            pk = generation switch
            {
                null => EntityFormat.GetFromBytes(data),
                _ when letsGo => null,
                1 => data.Length is 59 or 69 ? PokeList1.ReadFromSingle(data) : new PK1(data.ToArray()),
                2 => data.Length is 63 or 73 ? PokeList2.ReadFromSingle(data) : new PK2(data.ToArray()),
                3 => new PK3(data.ToArray()),
                4 => new PK4(data.ToArray()),
                5 => new PK5(data.ToArray()),
                6 => new PK6(data.ToArray()),
                7 => new PK7(data.ToArray()),
                8 => new PK8(data.ToArray()),
                _ => null,
            };
        }
        catch (Exception error)
        {
            return new(null, null, $"unreadable ({error.Message})");
        }

        if (letsGo)
            return new(null, null, "Let's Go (PB7) mons are not supported by the PKForge bank yet");
        if (pk is null)
            return new(null, null, generation is null ? "not a recognizable Pokémon file" : $"Gen {generation} is not a PKSM bank format");
        if (pk.Species == 0)
            return new(null, null, "empty slot (no species)");
        if (pk.Species > pk.MaxSpeciesID)
            return new(null, null, $"species #{pk.Species} does not exist in Gen {pk.Format}");
        if (!pk.ChecksumValid)
            return new(null, null, "checksum mismatch (corrupt slot)");

        byte[] bytes;
        switch (pk)
        {
            case PK1 pk1: bytes = PokeList1.WrapSingle(pk1); break;
            case PK2 pk2: bytes = PokeList2.WrapSingle(pk2); break;
            default:
                // PKSM keeps box structures only; give the bank sane party stats.
                if (generation is not null) pk.ResetPartyStats();
                bytes = new byte[pk.SIZE_PARTY];
                pk.WriteDecryptedDataParty(bytes);
                break;
        }

        // The bank re-reads its bytes by size and content alone. Where PKHeX's heuristics would
        // read them back as another format (the PK6/PK7 and PK8/PB8 size collisions), refuse
        // rather than store a mon the rest of the app would misread.
        var reread = EntityFormat.GetFromBytes(bytes);
        if (reread is null || reread.GetType() != pk.GetType())
            return new(null, null, $"PKForge would read this {pk.GetType().Name} back as {reread?.GetType().Name ?? "nothing"}");

        var info = new BankEntryInfo(pk.Species, pk.Form, pk.IsShiny,
            pk.IsNicknamed ? pk.Nickname : GameInfo.GetStrings("en").specieslist[pk.Species],
            pk.CurrentLevel, pk.Format, sourceName);
        return new(bytes, info, null);
    }

    /// <summary>
    /// The PKSM payload for a bank mon. PKSM's bank holds exactly the formats PKSM-Core has
    /// classes for (PK1-PK8, PB7); same-generation side formats (Stadium 2, Colosseum/XD,
    /// Battle Revolution) are converted to their mainline sibling, everything else is refused.
    /// </summary>
    public static Encoded Encode(byte[] bankBytes)
    {
        var pk = EntityFormat.GetFromBytes(bankBytes);
        if (pk is null || pk.Species == 0)
            return new(0, false, null, "not a readable Pokémon");

        var target = pk switch
        {
            SK2 => typeof(PK2),
            CK3 or XK3 => typeof(PK3),
            BK4 or RK4 => typeof(PK4),
            _ => null,
        };
        if (target is not null)
        {
            var converted = EntityConverter.ConvertToType(pk, target, out var result);
            if (converted is null)
                return new(0, false, null, $"could not convert {pk.GetType().Name} to {target.Name} ({result})");
            pk = converted;
        }

        pk.RefreshChecksum();
        switch (pk)
        {
            case PK1 pk1: return new(1, false, PokeList1.WrapSingle(pk1), null);
            case PK2 pk2: return new(2, false, PokeList2.WrapSingle(pk2), null);
            case PK3 or PK4 or PK5 or PK6 or PK7 or PB7 or PK8:
            {
                var data = new byte[pk.SIZE_STORED];
                pk.WriteDecryptedDataStored(data);
                // PB7 is party-sized even at rest; PKSM-Core's PB7::BOX_LENGTH is the first 232 bytes.
                if (pk is PB7) data = data[..PksmLetsGoBoxLength];
                return new(pk.Format, pk is PB7, data, null);
            }
            default:
                return new(0, false, null, $"PKSM banks have no slot for {pk.GetType().Name} ({pk.Context})");
        }
    }
}
