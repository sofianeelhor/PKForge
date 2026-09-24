using PKForge.Engine.RadicalRed;

namespace PKForge.Engine.GsChronicles;

/// <summary>
/// A Pokémon GS Chronicles save (Ruki Studios, CFRU on FireRed). Ground truth: the
/// game's engine source, github.com/G0LD/GS-Chronicles-Engine @7517ab2, and a real
/// device save. The layout is the CFRU one Radical Red uses, verified field by field:
/// <list type="bullet">
/// <item>src/save.c gSaveSectionOffsets: the same checksum windows (0xF24, 0xFF0 x3,
/// 0xD98, 0xFF0 x8, 0x450), the same parasite tails, sectors 30/31 raw; only the
/// footer signature differs (CUSTOM_FILE_SIGNATURE 0x66290096, src/config.h).</item>
/// <item>include/global.h SaveBlock1 (0x202552C): party count 0x34, party 0x38, money
/// 0x290, dex seen/caught 0x310/0x38D; SaveBlock2 (0x2024588): name, gender, trainer
/// id, encryption key 0xF20.</item>
/// <item>src/item.c: the five bag pockets at RAM 0x203BB20, capacities 450/75/50/128/75
/// (NUM_TMSHMS = 120 TMs + 8 HMs, src/config.h).</item>
/// <item>src/pokemon_storage_system.c sPokemonBoxPtrs: 25 boxes (TOTAL_BOXES_COUNT,
/// include/pokemon_storage_system.h). Boxes 1-19 in PokemonStorage, 20-22 at RAM
/// 0x203CB44 (the sector-30/31 region, as in Radical Red), and, unlike Radical Red,
/// boxes 23-24 at 0x2027434 = SaveBlock1 + 0x1F08 (the tail of frontierRecords,
/// which include/new/frontier.h marks free from there) and box 25 at 0x2024638 =
/// SaveBlock2 + 0xB0 (global.h: u8 box25[0x6CC]) — all three inside checksummed
/// sections, so they are real, saved boxes.</item>
/// </list>
/// The real save agrees: 14/14 live sections validate against the CFRU windows, the
/// party is one plaintext Mesprit (species 534, Cut + Dual Wingbeat, holding an Oran
/// Berry), dex bit 481 is seen and caught, the bag holds 2 Potions at section 13 +
/// 0xAD8, and the three save-block box areas are zero exactly up to SaveBlock1 0x2CA0.
/// </summary>
internal sealed class GsChroniclesEngineSession : CfruEngineSession
{
    public static readonly CfruGameProfile Profile = new(
        "GS Chronicles", "GSCHRONICLES", GsChroniclesData.Instance,
        [
            new CfruSectorBox(FirstSection: 1, BlockOffset: 0x1F08), // box 23: SaveBlock1, sections 2-3
            new CfruSectorBox(FirstSection: 1, BlockOffset: 0x1F08 + 30 * RadicalRedFormat.PcMonSize), // box 24: section 3
            new CfruSectorBox(FirstSection: 0, BlockOffset: 0xB0), // box 25: SaveBlock2, section 0
        ],
        bytes => SaveParser.IsPokemonGsChronicles(bytes.Span));

    public GsChroniclesEngineSession(ReadOnlyMemory<byte> bytes, string? displayName = null)
        : base(bytes, displayName, Profile)
    {
    }
}
