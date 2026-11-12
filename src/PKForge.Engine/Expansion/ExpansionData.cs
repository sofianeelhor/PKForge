using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using PKForge.Domain;
using PKHeX.Core;
using PKForge.Engine.Unbound;

namespace PKForge.Engine.Expansion;

/// <summary>
/// The expansion's ROM-truth tables: raw species id → national id, base stats, types,
/// abilities, gender ratio and growth rate; plus its move, item and ability name tables.
/// Generated from rh-hideout/pokeemerald-expansion (see Data/README.md).
///
/// Species names deliberately come from PKHeX's English national table for national
/// 1..1025, so a form id such as <c>SPECIES_FLOETTE_ETERNAL</c> reads as "Floette".
/// Custom ids with no national counterpart fall back to <c>Species N</c> exactly as the
/// ticket asks; nothing is guessed from another game's numbering.
/// </summary>
internal sealed class ExpansionData
{
    private static readonly Lazy<ExpansionData> Lazy = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    public static ExpansionData Instance => Lazy.Value;

    private static readonly Lazy<ExpansionData> SharedSince18Lazy = new(
        () => Instance.Restrict(maxSpecies: 1523, maxMove: 847, maxItem: 827, maxAbility: 310),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<ExpansionData> BlackPearlLegacyLazy = new(
        () => Instance.Restrict(maxSpecies: 1523, maxMove: 847, maxItem: 974, maxAbility: 310,
            itemNames: LoadItemNames("expansion.blackpearl-items.json")),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The ids every pokeemerald-expansion release from 1.8.0 to 1.17.2 numbers the same way,
    /// for a hack whose exact release is unknown: species below NUM_SPECIES 1524, moves below
    /// MOVES_COUNT 848 (no Z or Max moves), items below ITEMS_COUNT 828 and abilities below
    /// ABILITIES_COUNT 311. Every constant in that range keeps its id across those tags, so the
    /// pack's names hold and a picker never offers an id the hack's build may not have.
    /// </summary>
    public static ExpansionData SharedSince18 => SharedSince18Lazy.Value;

    /// <summary>
    /// Black Pearl Emerald before 2.1 (expansion 1.8.6 in 1.0.x, 1.16.3 in 2.0.x): the shared
    /// expansion ids for species, moves and abilities, and the hack's own item enum (TM01-TM219
    /// at 582-800, which moves every later item), ids 1-974 being the same in both builds.
    /// </summary>
    public static ExpansionData BlackPearlLegacy => BlackPearlLegacyLazy.Value;

    private readonly Dictionary<int, SpeciesEntry> _species;
    private readonly Dictionary<int, int> _rawByNational;
    private readonly Dictionary<int, string> _moveNames;
    private readonly Dictionary<int, int> _movePp;
    private readonly Dictionary<int, string> _itemNames;
    private readonly Dictionary<int, string> _abilityNames;
    private readonly string[] _nationalNames;

    public int MaxSpecies { get; }
    public int MaxMove { get; }
    public int MaxItem { get; }
    public int MaxAbility { get; }

    /// <summary>A view of these tables limited to the ids a hack's build has, optionally with
    /// the hack's own item names (its item enum differs from the expansion's).</summary>
    private ExpansionData(ExpansionData source, int maxSpecies, int maxMove, int maxItem, int maxAbility, Dictionary<int, string>? itemNames)
    {
        _species = source._species;
        _rawByNational = source._rawByNational;
        _moveNames = source._moveNames;
        _movePp = source._movePp;
        _itemNames = itemNames ?? source._itemNames;
        _abilityNames = source._abilityNames;
        _nationalNames = source._nationalNames;
        MaxSpecies = Math.Min(maxSpecies, source.MaxSpecies);
        MaxMove = Math.Min(maxMove, source.MaxMove);
        MaxItem = itemNames is null ? Math.Min(maxItem, source.MaxItem) : maxItem;
        MaxAbility = Math.Min(maxAbility, source.MaxAbility);
    }

    private ExpansionData Restrict(int maxSpecies, int maxMove, int maxItem, int maxAbility, Dictionary<int, string>? itemNames = null) =>
        new(this, maxSpecies, maxMove, maxItem, maxAbility, itemNames);

    private static Dictionary<int, string> LoadItemNames(string resource)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException($"The item table {resource} is missing from the build.");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var items = JsonSerializer.Deserialize<List<ItemEntry>>(stream, options)
            ?? throw new InvalidDataException($"The item table {resource} could not be read.");
        return items.ToDictionary(item => item.Id, item => item.Name);
    }

    private ExpansionData(Pack pack)
    {
        _species = new Dictionary<int, SpeciesEntry>(pack.Species.Count);
        foreach (var species in pack.Species)
            _species[species.Id] = species;
        MaxSpecies = pack.MaxSpecies;
        MaxMove = pack.MaxMove;
        MaxItem = pack.MaxItem;
        MaxAbility = pack.MaxAbility;

        _rawByNational = new Dictionary<int, int>();
        foreach (var species in pack.Species)
        {
            if (species.Nat <= 0)
                continue;
            if (!_rawByNational.TryGetValue(species.Nat, out var current) || species.Id < current)
                _rawByNational[species.Nat] = species.Id;
        }

        _moveNames = pack.Moves.ToDictionary(move => move.Id, move => move.Name);
        _movePp = pack.Moves.ToDictionary(move => move.Id, move => move.Pp);
        _itemNames = pack.Items.ToDictionary(item => item.Id, item => item.Name);
        _abilityNames = pack.Abilities.ToDictionary(ability => ability.Id, ability => ability.Name);
        _nationalNames = GameInfo.GetStrings("en").specieslist;
    }

    private static ExpansionData Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("expansion.expansion.json")
            ?? throw new InvalidDataException("The expansion data pack is missing from the build.");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var pack = JsonSerializer.Deserialize<Pack>(stream, options)
            ?? throw new InvalidDataException("The expansion data pack could not be read.");
        return new ExpansionData(pack);
    }

    // ── Species ──

    public int NationalIdOf(int species) => Find(species)?.Nat is > 0 and var national ? national : species;

    public int SpeciesFromNational(int national) =>
        _rawByNational.TryGetValue(national, out var raw) ? raw
        : national > 0 && national <= MaxSpecies ? national
        : 0;

    public string SpeciesName(int species)
    {
        var national = NationalIdOf(species);
        if (national is >= 1 and < 1026 && national < _nationalNames.Length)
        {
            var name = _nationalNames[national];
            if (!string.IsNullOrWhiteSpace(name))
                return name;
        }
        return $"Species {species}";
    }

    public bool IsKnownSpecies(int species) => species > 0 && species <= MaxSpecies;

    public BaseStats BaseStats(int species)
    {
        var entry = Find(species) ?? Canonical(NationalIdOf(species));
        return entry is null
            ? new BaseStats(0, 0, 0, 0, 0, 0)
            : new BaseStats(entry.Hp, entry.Atk, entry.Def, entry.Spa, entry.Spd, entry.Spe);
    }

    public IReadOnlyList<int> Types(int species)
    {
        var entry = Find(species) ?? Canonical(NationalIdOf(species));
        return entry?.Types ?? [];
    }

    public (int A1, int A2, int Hidden) Abilities(int species)
    {
        var entry = Find(species) ?? Canonical(NationalIdOf(species));
        if (entry is null || entry.Abilities.Length < 3)
            return (0, 0, 0);
        return (entry.Abilities[0], entry.Abilities[1], entry.Abilities[2]);
    }

    public int GenderRatio(int species)
    {
        var entry = Find(species) ?? Canonical(NationalIdOf(species));
        return entry?.Gender ?? 255;
    }

    public int Growth(int species)
    {
        var entry = Find(species) ?? Canonical(NationalIdOf(species));
        return entry?.Growth ?? 0;
    }

    public uint ExperienceAtLevel(int species, int level) =>
        Experience.GetEXP((byte)Math.Clamp(level, 1, 100), (byte)Growth(species));

    public int LevelForExperience(int species, uint experience) =>
        Experience.GetLevel(experience, (byte)Growth(species));

    // ── Moves / items / abilities ──

    public string MoveName(int move) => _moveNames.TryGetValue(move, out var name) ? name : $"Move {move}";

    public int MovePp(int move) => _movePp.TryGetValue(move, out var pp) ? pp : 0;

    public bool IsKnownMove(int move) => move == 0 || (move > 0 && move <= MaxMove);

    /// <summary>Raw move ids up to this one are the national ones (Hyperspace Fury); past it the
    /// expansion leaves out the Z-moves, so Shore Up is 622 here and 659 in PKHeX.</summary>
    private const int LastNationalOrderMove = 621;

    /// <summary>Raw ability ids up to this one are the national ones; past it are the hacks' own.</summary>
    public const int LastNationalAbility = 310;

    private CfruIdBridge? _moveBridge;

    private CfruIdBridge MoveBridge => _moveBridge ??= new CfruIdBridge(
        _moveNames.Where(pair => pair.Key > LastNationalOrderMove).ToDictionary(), GameInfo.GetStrings("en").movelist, []);

    /// <summary>The PKHeX id of a raw move (by name past the national order), or 0 when PKHeX has none.</summary>
    public int MoveToNational(int move) =>
        move <= 0 ? 0 : move <= LastNationalOrderMove ? move : MoveBridge.ToNational(move);

    /// <summary>The raw id of a PKHeX move, or 0 when the expansion has none.</summary>
    public int MoveFromNational(int national) =>
        national <= 0 ? 0 : national <= LastNationalOrderMove ? national : MoveBridge.FromNational(national);

    public string ItemName(int item) =>
        item <= 0 ? string.Empty : item <= MaxItem && _itemNames.TryGetValue(item, out var name) ? name : $"Item {item}";

    public bool IsKnownItem(int item) => item == 0 || (item > 0 && item <= MaxItem);

    public string AbilityName(int ability) =>
        _abilityNames.TryGetValue(ability, out var name) ? name : $"Ability {ability}";

    public IReadOnlyList<string> ItemNames()
    {
        var names = new string[MaxItem + 1];
        for (var id = 0; id <= MaxItem; id++)
            names[id] = id == 0 ? "None" : ItemName(id);
        return names;
    }

    private SpeciesEntry? Find(int species) => _species.TryGetValue(species, out var entry) ? entry : null;

    private SpeciesEntry? Canonical(int national) =>
        _rawByNational.TryGetValue(national, out var raw) ? Find(raw) : null;

    // ── Pack model ──

    private sealed class Pack
    {
        public int MaxSpecies { get; set; }
        public int MaxMove { get; set; }
        public int MaxItem { get; set; }
        public int MaxAbility { get; set; }
        public List<SpeciesEntry> Species { get; set; } = [];
        public List<MoveEntry> Moves { get; set; } = [];
        public List<ItemEntry> Items { get; set; } = [];
        public List<AbilityEntry> Abilities { get; set; } = [];
    }

    private sealed class SpeciesEntry
    {
        public int Id { get; set; }
        public int Nat { get; set; }
        public int Hp { get; set; }
        public int Atk { get; set; }
        [JsonPropertyName("def")] public int Def { get; set; }
        public int Spa { get; set; }
        public int Spd { get; set; }
        public int Spe { get; set; }
        public int[] Types { get; set; } = [];
        public int[] Abilities { get; set; } = [];
        public int Gender { get; set; }
        public int Growth { get; set; }
    }

    private sealed class MoveEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Pp { get; set; }
    }

    private sealed class ItemEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class AbilityEntry
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }
}
