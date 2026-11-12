using System.Reflection;
using System.Text.Json;
using PKHeX.Core;
using PKForge.Engine.Unbound;

namespace PKForge.Engine.SoulGold;

/// <summary>
/// Soul Gold's ROM-truth tables (species → national id, base stats, types,
/// abilities, gender ratio, growth rate; move, item and ability names),
/// generated from Eemeliri/soulgold 1.2.1 (`e738033`) — see Data/README.md.
/// The expansion version matters: ids above the national range shift between
/// releases, and this hack's 1.15.2 tables are not the current expansion's.
/// Species names come from the ROM's own tables; unknown ids read as
/// "Species N" / "Move N" / "Item N" rather than a guess. Moves are stored in
/// the expansion's numbering, which leaves out the Z-moves: the session
/// translates them to PKHeX's ids (<see cref="MoveToNational"/>) at its edge.
/// </summary>
internal sealed class SoulGoldData
{
    public static SoulGoldData Instance { get; } = new();

    private sealed record SpeciesEntry(string Name, int NatDex, int[] Base, int Growth, int[] Types, int Gender, int[] Abilities);
    private sealed record MoveEntry(string Name, int Pp);
    private sealed record ItemEntry(string Name, string Pocket);
    private sealed record AbilityEntry(string Name);

    private readonly Dictionary<int, SpeciesEntry> _species;
    private readonly Dictionary<int, MoveEntry> _moves;
    private readonly Dictionary<int, ItemEntry> _items;
    private readonly Dictionary<int, string> _abilities;

    public int MaxSpecies { get; }
    public int MaxMove { get; }
    public int MaxItem { get; }
    private readonly Dictionary<int, int> _romOfNational;
    private SoulGoldData()
    {
        _species = Load<SpeciesEntry>("species.json");
        _moves = Load<MoveEntry>("moves.json");
        _items = Load<ItemEntry>("items.json");
        _abilities = Load<AbilityEntry>("abilities.json")
            .ToDictionary(pair => pair.Key, pair => pair.Value.Name);
        _romOfNational = [];
        foreach (var (id, entry) in _species)
            if (entry.NatDex > 0 && (!_romOfNational.TryGetValue(entry.NatDex, out var rom) || id < rom))
                _romOfNational[entry.NatDex] = id;
        MaxSpecies = _species.Keys.Max();
        MaxMove = _moves.Keys.Max();
        MaxItem = _items.Keys.Max();
    }

    private static Dictionary<int, T> Load<T>(string file)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream($"soulgold.{file}") ?? throw new InvalidOperationException($"Missing soulgold.{file}.");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<Dictionary<int, T>>(stream, options)
               ?? throw new InvalidOperationException($"soulgold.{file} is empty.");
    }

    // ── Species ──

    public int NationalIdOf(int species) =>
        _species.TryGetValue(species, out var entry) && entry.NatDex > 0 ? entry.NatDex : 0;

    /// <summary>The rom id of a national dex number's base form: the lowest rom
    /// id mapped to that national number (form ids such as SPECIES_AMPHAROS_MEGA
    /// 921 → national 181 share the number with the base form).</summary>
    public int SpeciesFromNational(int national) =>
        _romOfNational.TryGetValue(national, out var rom) ? rom : 0;

    public string SpeciesName(int species)
    {
        if (_species.TryGetValue(species, out var entry))
            return entry.Name;
        var national = NationalIdOf(species);
        var names = GameInfo.GetStrings("en").specieslist;
        return national > 0 && national < names.Length && names[national].Length > 0 ? names[national] : $"Species {species}";
    }

    public bool IsKnownSpecies(int species) => _species.ContainsKey(species);

    public IReadOnlyList<int> Types(int species) =>
        _species.TryGetValue(species, out var entry) && entry.Types.Length > 0
            ? entry.Types
            : [0];

    public int GenderRatio(int species) =>
        _species.TryGetValue(species, out var entry) ? entry.Gender : 255;

    public int GrowthRate(int species) =>
        _species.TryGetValue(species, out var entry) ? entry.Growth : 0;

    public int[] Abilities(int species) =>
        _species.TryGetValue(species, out var entry) ? entry.Abilities : [0, 0, 0];

    public int[] BaseStats(int species) =>
        _species.TryGetValue(species, out var entry) ? entry.Base : [1, 1, 1, 1, 1, 1];

    /// <summary>Expansion growth enum order (include/constants/pokemon.h):
    /// 0 medium fast, 1 erratic, 2 fluctuating, 3 medium slow, 4 fast, 5 slow.</summary>
    public static uint ExperienceAtLevel(int growth, int level)
    {
        level = Math.Clamp(level, 1, 100);
        double n = level;
        return growth switch
        {
            1 => level switch
            {
                < 50 => (uint)(n * n * n * (100 - n) / 50),
                < 68 => (uint)(n * n * n * (150 - n) / 100),
                < 98 => (uint)(n * n * n * ((1911 - 10 * n) / 3) / 500),
                _ => (uint)(n * n * n * (160 - n) / 100),
            },
            2 => level switch
            {
                < 15 => (uint)(n * n * n * ((level + 1) / 3 + 24) / 50),
                < 36 => (uint)(n * n * n * (n + 14) / 50),
                _ => (uint)(n * n * n * (level / 2 + 32) / 50),
            },
            3 => (uint)(6 / 5.0 * n * n * n - 15 * n * n + 100 * n - 140),
            4 => (uint)(4 / 5.0 * n * n * n),
            5 => (uint)(5 / 4.0 * n * n * n),
            _ => (uint)(n * n * n),
        };
    }

    public int LevelForExperience(int growth, uint experience)
    {
        for (var level = 100; level >= 1; level--)
            if (ExperienceAtLevel(growth, level) <= experience)
                return level;
        return 1;
    }

    // ── Moves / items / abilities ──

    public bool IsKnownMove(int move) => move == 0 || _moves.ContainsKey(move);

    public string MoveName(int move) =>
        _moves.TryGetValue(move, out var entry) ? entry.Name : $"Move {move}";

    /// <summary>Raw move ids up to this one are the national ones (Hyperspace Fury); past it
    /// the expansion leaves out the Z-moves, so Shore Up is 622 here and 659 in PKHeX.</summary>
    private const int LastNationalOrderMove = 621;

    /// <summary>Raw ability ids up to this one are the national ones; past it are the hack's own.</summary>
    public const int LastNationalAbility = 310;

    private CfruIdBridge? _moveBridge;

    private CfruIdBridge MoveBridge => _moveBridge ??= new CfruIdBridge(
        _moves.Where(pair => pair.Key > LastNationalOrderMove).ToDictionary(pair => pair.Key, pair => pair.Value.Name),
        GameInfo.GetStrings("en").movelist, []);

    /// <summary>The PKHeX id of a raw move (by name past the national order), or 0 when PKHeX has none.</summary>
    public int MoveToNational(int move) =>
        move <= 0 ? 0 : move <= LastNationalOrderMove ? move : MoveBridge.ToNational(move);

    /// <summary>The raw id of a PKHeX move, or 0 when the hack has none.</summary>
    public int MoveFromNational(int national) =>
        national <= 0 ? 0 : national <= LastNationalOrderMove ? national : MoveBridge.FromNational(national);

    public int MoveBasePp(int move) => _moves.TryGetValue(move, out var entry) ? entry.Pp : 0;

    public bool IsKnownItem(int item) => item == 0 || _items.ContainsKey(item);

    public string ItemName(int item) =>
        item <= 0 ? string.Empty : _items.TryGetValue(item, out var entry) ? entry.Name : $"Item {item}";

    public string ItemPocket(int item) =>
        _items.TryGetValue(item, out var entry) ? entry.Pocket : string.Empty;

    public string AbilityName(int ability) =>
        _abilities.TryGetValue(ability, out var name) ? name : $"Ability {ability}";


    public IReadOnlyList<string> ItemNames()
    {
        var names = new string[MaxItem + 1];
        for (var id = 0; id <= MaxItem; id++)
            names[id] = id == 0 ? "None" : ItemName(id);
        return names;
    }

    // ── Gen 3 stat math (nature in PKHeX's 0..24 order) ──

    public int[] ComputeStats(int species, int level, int nature, IReadOnlyList<int> ivs, IReadOnlyList<int> evs)
    {
        var bases = BaseStats(species);
        var stats = new int[6];
        for (var stat = 0; stat < 6; stat++)
        {
            var value = (2 * bases[stat] + ivs[stat] + evs[stat] / 4) * level / 100 + 5;
            if (stat > 0)
                value = value * NatureStatMultiplier(nature, stat) / 100;
            stats[stat] = value;
        }
        stats[0] += level + 10 - 5; // HP: +level+10 where others get +5
        return stats;
    }

    /// <summary>Nature multiplier for stat 1..5 (Atk/Def/SpA/SpD/Spe): 110 on the
    /// nature's raised stat (nature/5), 90 on its lowered one (nature%5).</summary>
    private static int NatureStatMultiplier(int nature, int stat)
    {
        var raised = nature / 5;
        var lowered = nature % 5;
        if (raised == lowered || (raised != stat - 1 && lowered != stat - 1))
            return 100;
        return raised == stat - 1 ? 110 : 90;
    }

    public static int GenderOf(uint pid, int ratio) => ratio switch
    {
        255 => 2,
        254 => 1,
        0 => 0,
        _ => (pid & 0xFF) < ratio ? 1 : 0,
    };
}
