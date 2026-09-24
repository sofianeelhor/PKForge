namespace PKForge.Engine.RadicalRed;

/// <summary>
/// The per-hack tables a CFRU session reads through. The save layout (sector envelope,
/// CFRU checksum windows, plaintext party, 58-byte compact PC, bag expansion) is the
/// engine's and identical across CFRU hacks built on it; the ids behind species, moves,
/// items and abilities are each hack's own, so every id crosses into PKHeX's national
/// space through the hack's tables, never through another game's.
/// </summary>
internal interface ICfruGameData
{
    int MaxSpeciesId { get; }
    int MaxItemId { get; }
    bool IsKnownSpecies(int species);
    string SpeciesName(int species);
    int SpeciesIdByName(string name);
    int NationalIdOf(int species);
    int SpeciesFromNational(int national);
    /// <summary>The save's dex-bitmap number for a national species (bit n-1), or 0
    /// when the hack's Pokédex has no slot for it.</summary>
    int DexNumberOf(int national);

    string MoveName(int move);
    int MoveToNational(int move);
    int MoveFromNational(int national);
    int MoveIdByName(string name);
    /// <summary>Base PP of a stored move id, 0 when the table does not know it.</summary>
    int MoveBasePp(int move);

    string ItemName(int item);
    int ItemToNational(int item);
    int ItemFromNational(int national);
    bool IsFillerItem(int item);
    IReadOnlyCollection<int> PocketIds(string family);
    bool IsSpecialPocketItem(int item);

    string AbilityName(int ability);
    (int A1, int A2, int Hidden) AbilityIds(int species);
    int ActiveAbility(RadicalRedMon mon);

    int[] BaseStats(int species);
    int[] TypesOf(int species);
    int GenderOf(uint pid, int species);
    int GenderThreshold(int species);
    uint ExperienceAtLevel(int species, int level);
    int LevelForExperience(int species, uint experience);
    int[] ComputeStats(RadicalRedMon mon);
}

/// <summary>A PC box the save keeps outside the PokemonStorage stream, inside a
/// checksummed save block: <paramref name="FirstSection"/> is the section holding the
/// block's first 0xFF0 bytes (SaveBlock2 = 0, SaveBlock1 = 1) and <paramref name="BlockOffset"/>
/// the box's offset inside the block, so a box may straddle two sections.</summary>
internal readonly record struct CfruSectorBox(int FirstSection, int BlockOffset);

/// <summary>
/// One CFRU hack as a session sees it: its name, its tables, and how many PC boxes its
/// build actually saves. Boxes 0-18 always live in the PokemonStorage stream and 19-21
/// in the raw sector-30/31 region (the engine's LoadSector30And31); anything past that
/// is per-hack.
/// </summary>
internal sealed record CfruGameProfile(
    string GameName,
    string SnapshotTag,
    ICfruGameData Data,
    IReadOnlyList<CfruSectorBox> SectorBoxes,
    Func<ReadOnlyMemory<byte>, bool> Detect)
{
    public int BoxCount => RadicalRedFormat.StreamBoxes + RadicalRedFormat.RawBoxes + SectorBoxes.Count;
}

/// <summary>The Radical Red tables behind <see cref="ICfruGameData"/>.</summary>
internal sealed class RadicalRedGameData : ICfruGameData
{
    public static readonly RadicalRedGameData Instance = new();

    private RadicalRedGameData() { }

    public int MaxSpeciesId => RadicalRedData.MaxSpeciesId;
    public int MaxItemId => RadicalRedData.MaxItemId;
    public bool IsKnownSpecies(int species) => RadicalRedData.IsKnownSpecies(species);
    public string SpeciesName(int species) => RadicalRedData.SpeciesName(species);
    public int SpeciesIdByName(string name) => RadicalRedData.SpeciesIdByName(name);
    public int NationalIdOf(int species) => RadicalRedData.NationalIdOf(species);
    public int SpeciesFromNational(int national) => RadicalRedData.SpeciesFromNational(national);
    public int DexNumberOf(int national) => national; // the CFRU dex indexes national numbers
    public string MoveName(int move) => RadicalRedData.MoveName(move);
    public int MoveToNational(int move) => RadicalRedData.MoveToNational(move);
    public int MoveFromNational(int national) => RadicalRedData.MoveFromNational(national);
    public int MoveIdByName(string name) => RadicalRedData.MoveIdByName(name);

    /// <summary>Gen 3 PP in the shared zone 1..354, the CFRU table (shared with Unbound) beyond.</summary>
    public int MoveBasePp(int move) => move switch
    {
        <= 0 or > RadicalRedData.CfruMoveLimit => 0,
        <= RadicalRedData.SharedMoveLimit => PKHeX.Core.MoveInfo.GetPP(PKHeX.Core.EntityContext.Gen3, (ushort)move),
        _ => Unbound.UnboundData.MoveBasePp(move),
    };

    public string ItemName(int item) => RadicalRedData.ItemName(item);
    public int ItemToNational(int item) => RadicalRedData.ItemToNational(item);
    public int ItemFromNational(int national) => RadicalRedData.ItemFromNational(national);
    public bool IsFillerItem(int item) => RadicalRedData.IsFillerItem(item);
    public IReadOnlyCollection<int> PocketIds(string family) => RadicalRedData.PocketIds(family);
    public bool IsSpecialPocketItem(int item) => RadicalRedData.IsSpecialPocketItem(item);
    public string AbilityName(int ability) => RadicalRedData.AbilityName(ability);
    public (int A1, int A2, int Hidden) AbilityIds(int species) => RadicalRedData.AbilityIds(species);
    public int ActiveAbility(RadicalRedMon mon) => RadicalRedData.ActiveAbility(mon);
    public int[] BaseStats(int species) => RadicalRedData.BaseStats(species);
    public int[] TypesOf(int species) => RadicalRedData.TypesOf(species);
    public int GenderOf(uint pid, int species) => RadicalRedData.GenderOf(pid, species);
    public int GenderThreshold(int species) => RadicalRedData.GenderThreshold(species);
    public uint ExperienceAtLevel(int species, int level) => RadicalRedData.ExperienceAtLevel(species, level);
    public int LevelForExperience(int species, uint experience) => RadicalRedData.LevelForExperience(species, experience);
    public int[] ComputeStats(RadicalRedMon mon) => RadicalRedData.ComputeStats(mon);
}
