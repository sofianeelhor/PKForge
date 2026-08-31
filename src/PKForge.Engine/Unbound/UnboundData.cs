using System.Reflection;
using System.Text.Json;

namespace PKForge.Engine.Unbound;

/// <summary>
/// Unbound's ROM-truth tables (vendored from PUSE, MIT): names, types, base stats,
/// gender thresholds, growth rates, abilities, and move PP for every species and id
/// the CFRU engine defines, far beyond retail Gen 3.
/// </summary>
internal static class UnboundData
{
    private static Dictionary<int, string>? _species;
    private static Dictionary<int, string>? _moves;
    private static Dictionary<int, string>? _items;
    private static Dictionary<int, string>? _abilities;
    private static Dictionary<int, int>? _movePp;
    private static Dictionary<int, int[]>? _types;
    private static Dictionary<int, int[]>? _baseStats;
    private static Dictionary<int, int>? _genderThresholds;
    private static Dictionary<int, int>? _growthRates;
    private static Dictionary<int, (int A1, int A2, int Hidden)>? _abilitiesMeta;
    private static HashSet<int>? _ballItems;
    private static HashSet<int>? _berryItems;
    private static HashSet<int>? _tmItems;
    private static HashSet<int>? _keyItems;

    public static string SpeciesName(int species) => Name(ref _species, "unbound.pokemon.txt", species);
    public static string MoveName(int move) => Name(ref _moves, "unbound.moves.txt", move);

    private static Dictionary<string, int>? _speciesByName;
    private static Dictionary<string, int>? _movesByName;

    /// <summary>Unbound species id for a species NAME: the ROM table diverges from
    /// national ids (Sneasler is 1256 there, not 903), so names are the only safe key.</summary>
    public static int SpeciesIdByName(string name)
    {
        if (_speciesByName is null)
        {
            if (_species is null) LoadNames();
            _speciesByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _species!)
                if (!_speciesByName.ContainsKey(entry.Value))
                    _speciesByName[entry.Value] = entry.Key;
        }
        return _speciesByName.TryGetValue(name, out var id) ? id : 0;
    }

    public static int MoveIdByName(string name)
    {
        if (_movesByName is null)
        {
            if (_moves is null) LoadNames();
            _movesByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _moves!)
                if (!_movesByName.ContainsKey(entry.Value))
                    _movesByName[entry.Value] = entry.Key;
        }
        return _movesByName.TryGetValue(name, out var id) ? id : 0;
    }
    public static string ItemName(int item) => Name(ref _items, "unbound.items.txt", item);
    public static string AbilityName(int ability) => Name(ref _abilities, "unbound.abilities.txt", ability);

    public static int MoveBasePp(int move)
    {
        LoadPp();
        return _movePp!.TryGetValue(move, out var pp) ? pp : 0;
    }

    /// <summary>Item ids that belong to a pocket family (PUSE's ROM-derived map).</summary>
    public static IReadOnlyCollection<int> PocketIds(string family) => family switch
    {
        "ball" => LoadPockets().Ball,
        "berry" => LoadPockets().Berry,
        "tm" => LoadPockets().Tm,
        "key" => LoadPockets().Key,
        _ => [],
    };

    public static bool IsSpecialPocketItem(int itemId)
    {
        var pockets = LoadPockets();
        return pockets.Ball.Contains(itemId) || pockets.Berry.Contains(itemId)
            || pockets.Tm.Contains(itemId) || pockets.Key.Contains(itemId);
    }

    private static (HashSet<int> Ball, HashSet<int> Berry, HashSet<int> Tm, HashSet<int> Key) LoadPockets()
    {
        if (_ballItems is not null)
            return (_ballItems, _berryItems!, _tmItems!, _keyItems!);

        _ballItems = [];
        _berryItems = [];
        _tmItems = [];
        _keyItems = [];
        try
        {
            var root = JsonDocument.Parse(ReadAll("unbound.item_pocket_map.json")).RootElement.GetProperty("pockets");
            Fill(_ballItems, root, "ball");
            Fill(_berryItems, root, "berry");
            var tm = new HashSet<int>();
            Fill(tm, root, "tm");
            Fill(tm, root, "hm");
            _tmItems = tm;
            Fill(_keyItems, root, "key");
        }
        catch
        {
            // PUSE's conservative fallback sets, from the same project.
            _ballItems = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 52, 53, 54, 59, 60, 622, 623, 624, 625, 626, 627, 628, 629, 630, 631];
            _berryItems = [.. Enumerable.Range(133, 43).Concat(Enumerable.Range(539, 24))];
            _tmItems = [.. Enumerable.Range(289, 58).Concat(Enumerable.Range(375, 62)).Concat(Enumerable.Range(437, 8))];
            _keyItems = [.. Enumerable.Range(259, 30).Concat(Enumerable.Range(348, 27))];
        }
        return (_ballItems, _berryItems, _tmItems, _keyItems);

        static void Fill(HashSet<int> into, JsonElement root, string family)
        {
            if (!root.TryGetProperty(family, out var node))
                return;
            foreach (var id in node.GetProperty("ids").EnumerateArray())
                into.Add(id.GetInt32());
        }
    }

    /// <summary>Primary + secondary type ids (the CFRU engine's own type order).</summary>
    public static int[] TypesOf(int species)
    {
        if (_types is null) LoadTypes();
        return _types!.TryGetValue(species, out var types) ? types : [0];
    }

    /// <summary>Base stats in app order: HP, Atk, Def, SpA, SpD, Spe.</summary>
    public static int[] BaseStats(int species)
    {
        if (_baseStats is null) LoadBaseStats();
        return _baseStats!.TryGetValue(species, out var stats) ? stats : [50, 50, 50, 50, 50, 50];
    }

    public static int GenderThreshold(int species)
    {
        if (_genderThresholds is null) LoadIdentity();
        return _genderThresholds!.TryGetValue(species, out var threshold) ? threshold : 127;
    }

    public static (int A1, int A2, int Hidden) AbilityIds(int species)
    {
        if (_abilitiesMeta is null) LoadAbilitiesMeta();
        return _abilitiesMeta!.TryGetValue(species, out var ids) ? ids : (0, 0, 0);
    }

    /// <summary>Gender from the PID low byte and the species threshold: 0 male, 1 female, 2 genderless.</summary>
    public static int GenderOf(uint pid, int species)
    {
        var threshold = GenderThreshold(species);
        if (threshold is 255) return 2;
        if (threshold is 0) return 0;
        return (pid & 0xFF) < (uint)threshold ? 1 : 0;
    }

    /// <summary>The ability actually active on a mon, honoring the hidden-ability flag.</summary>
    public static int ActiveAbility(UnboundMon mon)
    {
        var (a1, a2, hidden) = AbilityIds(mon.Species);
        if (mon.HiddenAbility) return hidden;
        var slot = mon.Pid & 1;
        return slot == 1 && a2 != 0 ? a2 : a1;
    }

    public static int LevelForExperience(int species, uint experience)
    {
        if (_growthRates is null) LoadGrowth();
        var rate = _growthRates!.TryGetValue(species, out var growth) ? growth : 0;
        for (var level = 100; level >= 1; level--)
            if (experience >= (uint)ExperienceAt(rate, level))
                return level;
        return 1;
    }

    public static int GrowthRateFor(int species)
    {
        if (_growthRates is null) LoadGrowth();
        return _growthRates!.TryGetValue(species, out var growth) ? growth : 0;
    }

    public static uint ExperienceAtLevel(int rate, int level) => (uint)Math.Max(0, ExperienceAt(rate, level));

    /// <summary>Battle stats for a PC mon (party mons carry theirs in the save tail).</summary>
    public static int[] ComputeStats(UnboundMon mon)
    {
        var baseStats = BaseStats(mon.Species);
        var ivs = mon.IVs;
        var evs = mon.EVs;
        var level = mon.Level;

        int NatureBoost(int nature, int storageIndex)
        {
            // G3 nature table over the non-HP storage stats (Atk=1, Def=2, Spe=3,
            // SpA=4, SpD=5): nature/5 is the boosted row, nature%5 the dropped
            // column; equal indices are the five neutral natures.
            var up = nature / 5 + 1;
            var down = nature % 5 + 1;
            if (up == down) return 100;
            if (storageIndex == up) return 110;
            if (storageIndex == down) return 90;
            return 100;
        }

        var hp = (2 * baseStats[0] + ivs[0] + evs[0] / 4) * level / 100 + level + 10;
        var result = new int[6];
        result[0] = hp;
        for (var i = 1; i < 6; i++)
        {
            // App order (HP, Atk, Def, SpA, SpD, Spe) -> storage order (Spe=3, SpA=4, SpD=5).
            var storageIndex = i is 3 ? 4 : i is 5 ? 3 : i;
            var raw = (2 * baseStats[i] + ivs[i] + evs[i] / 4) * level / 100 + 5;
            result[i] = raw * NatureBoost(mon.Nature, storageIndex) / 100;
        }
        return result;
    }

    private static string Name(ref Dictionary<int, string>? cache, string resource, int id)
    {
        if (cache is null) LoadNames();
        return cache!.TryGetValue(id, out var name) && name.Length > 0 ? name : $"#{id}";
    }

    private static void LoadNames()
    {
        _species = LoadNameMap("unbound.pokemon.txt");
        _moves = LoadNameMap("unbound.moves.txt");
        _items = LoadNameMap("unbound.items.txt");
        _abilities = LoadNameMap("unbound.abilities.txt");
    }

    private static Dictionary<int, string> LoadNameMap(string resource)
    {
        var map = new Dictionary<int, string>(4096);
        foreach (var line in Lines(resource))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0) continue;
            map[int.Parse(line[..separator])] = line[(separator + 1)..].Trim();
        }
        return map;
    }

    private static void LoadPp()
    {
        _movePp = [];
        foreach (var (id, text) in LoadNameMap("unbound.movepp.txt"))
            _movePp[id] = int.TryParse(text, out var pp) ? pp : 0;
    }

    private static void LoadTypes()
    {
        _types = [];
        foreach (var property in JsonDocument.Parse(ReadAll("unbound.species_types.json")).RootElement.EnumerateObject())
        {
            var entry = property.Value;
            _types[int.Parse(property.Name)] =
            [
                entry.GetProperty("type1_id").GetInt32(),
                entry.GetProperty("type2_id").GetInt32(),
            ];
        }
    }

    private static void LoadBaseStats()
    {
        _baseStats = [];
        foreach (var property in JsonDocument.Parse(ReadAll("unbound.species_base_stats.json")).RootElement.EnumerateObject())
        {
            var entry = property.Value;
            _baseStats[int.Parse(property.Name)] =
            [
                entry.GetProperty("hp").GetInt32(),
                entry.GetProperty("atk").GetInt32(),
                entry.GetProperty("def").GetInt32(),
                entry.GetProperty("spa").GetInt32(),
                entry.GetProperty("spd").GetInt32(),
                entry.GetProperty("spe").GetInt32(),
            ];
        }
    }

    private static void LoadIdentity()
    {
        _genderThresholds = [];
        foreach (var property in JsonDocument.Parse(ReadAll("unbound.species_identity_meta.json")).RootElement.EnumerateObject())
            _genderThresholds[int.Parse(property.Name)] = property.Value.GetProperty("gender_threshold").GetInt32();
    }

    private static void LoadGrowth()
    {
        _growthRates = [];
        foreach (var property in JsonDocument.Parse(ReadAll("unbound.species_growth_rates.json")).RootElement.EnumerateObject())
            _growthRates[int.Parse(property.Name)] = property.Value.GetProperty("growth_rate").GetInt32();
    }

    private static void LoadAbilitiesMeta()
    {
        _abilitiesMeta = [];
        foreach (var property in JsonDocument.Parse(ReadAll("unbound.species_abilities_meta.json")).RootElement.EnumerateObject())
        {
            var entry = property.Value;
            _abilitiesMeta[int.Parse(property.Name)] = (
                entry.GetProperty("ability_1_id").GetInt32(),
                entry.GetProperty("ability_2_id").GetInt32(),
                entry.GetProperty("hidden_ability_id").GetInt32());
        }
    }

    private static string ReadAll(string resource)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource {resource}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IEnumerable<string> Lines(string resource) =>
        ReadAll(resource).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>G3 growth curves (PUSE formulas): 0 cubic, 1 erratic, 2 fluctuating, 3 medium-slow, 4 fast, 5 slow.</summary>
    private static long ExperienceAt(int rate, int level)
    {
        if (level <= 1) return 0;
        level = Math.Min(level, 100);
        return rate switch
        {
            0 => (long)Math.Pow(level, 3),
            1 => level <= 50 ? (long)(Math.Pow(level, 3) * (100 - level) / 50)
                : level <= 68 ? (long)(Math.Pow(level, 3) * (150 - level) / 100)
                : level <= 98 ? (long)(Math.Pow(level, 3) * ((1911 - 10 * level) / 3.0) / 500)
                : (long)(Math.Pow(level, 3) * (160 - level) / 100),
            2 => level <= 15 ? (long)(Math.Pow(level, 3) * (Math.Floor((level + 1) / 3.0) + 24) / 50)
                : level <= 36 ? (long)(Math.Pow(level, 3) * (level + 14) / 50)
                : (long)(Math.Pow(level, 3) * (Math.Floor(level / 2.0) + 32) / 50),
            3 => (long)(1.2 * Math.Pow(level, 3) - 15 * level * level + 100 * level - 140),
            4 => 4 * (long)Math.Pow(level, 3) / 5,
            5 => 5 * (long)Math.Pow(level, 3) / 4,
            _ => (long)Math.Pow(level, 3),
        };
    }
}
