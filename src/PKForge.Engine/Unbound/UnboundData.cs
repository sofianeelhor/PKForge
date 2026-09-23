using PKHeX.Core;
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

    // ── National (PKHeX) id bridges ──
    // Unbound's ROM ids are NOT national/PKHeX ids: species follow the Gen-3 internal
    // order plus CFRU/DPE forms (Treecko is 277, Sneasler 1256), moves past 354 follow
    // CFRU's include/constants/moves.h (355 is Leech Fang, not Roost), abilities follow
    // CFRU's include/constants/abilities.h (72 is Transistor, not Vital Spirit), and
    // items follow CFRU's include/constants/items.h with the UNBOUND define (176 is
    // Choice Band). Every UI table (IGameDataService, sprites, pickers) speaks PKHeX
    // ids, so the session must bridge at its boundary or every name is wrong.
    // Sources: github.com/Skeli789/Complete-Fire-Red-Upgrade include/constants/*.h and
    // github.com/Skeli789/Unbound-Cloud server/src/data/unbound_2_1/*.json (ids) +
    // src/data/*Names.json (display names); both agree with PUSE's ROM dump id-for-id.

    private static Dictionary<int, int>? _nationalBySpecies;
    private static Dictionary<int, int>? _speciesByNational;
    private static CfruIdBridge? _moveBridge;
    private static CfruIdBridge? _abilityBridge;
    private static CfruIdBridge? _itemBridge;

    /// <summary>The national species id behind an Unbound ROM id, or 0 when unknown.
    /// Primary truth is species_national.txt (Unbound-Cloud's SpeciesToDexNum for the
    /// 2.1 table); ids past that table (the 2.1 ROM's trailing forms) bridge by name.</summary>
    public static int NationalIdOf(int species)
    {
        LoadNationalSpecies();
        if (_nationalBySpecies!.TryGetValue(species, out var national)) return national;
        var name = SpeciesName(species);
        if (name.StartsWith('#')) return 0;
        var strings = GameInfo.GetStrings("en").specieslist;
        var key = NormalizeName(name);
        for (var id = 1; id < strings.Length; id++)
            if (NormalizeName(strings[id]) == key)
                return id;
        return 0;
    }

    /// <summary>The Unbound ROM id for a national species (its base form: the lowest
    /// ROM id mapping to it), or 0 when Unbound has no such species.</summary>
    public static int SpeciesFromNational(int national)
    {
        LoadNationalSpecies();
        return _speciesByNational!.TryGetValue(national, out var species) ? species : 0;
    }

    public static int MoveToNational(int move) => MoveBridge().ToNational(move);
    public static int MoveFromNational(int move) => MoveBridge().FromNational(move);
    public static int AbilityToNational(int ability) => AbilityBridge().ToNational(ability);
    public static int AbilityFromNational(int ability) => AbilityBridge().FromNational(ability);
    public static int ItemToNational(int item) => ItemBridge().ToNational(item);
    public static int ItemFromNational(int item) => ItemBridge().FromNational(item);

    private static CfruIdBridge MoveBridge()
    {
        if (_moves is null) LoadNames();
        // Unbound shows two official moves under shortened names (CFRU MOVE_PETALBLIZZARD
        // 0x1EA "Petal Storm", MOVE_CRAFTYSHIELD 0x27B "Crafty Guard"). MOVE_LEECHFANG
        // 0x163 and MOVE_STEELYHIT 0x1F3 ("Metal Bash") are CFRU/Unbound originals with no
        // PKHeX id: they read as 0 and an echoed edit leaves them in place.
        return _moveBridge ??= new CfruIdBridge(_moves!, GameInfo.GetStrings("en").movelist,
            new Dictionary<int, int> { [490] = (int)Move.PetalBlizzard, [635] = (int)Move.CraftyShield });
    }

    private static CfruIdBridge AbilityBridge()
    {
        if (_abilities is null) LoadNames();
        // PKHeX names both As One abilities identically; CFRU splits them by owner
        // (ABILITY_ASONE_GRIM 153 = Spectrier, ABILITY_ASONE_CHILLING 154 = Glastrier).
        return _abilityBridge ??= new CfruIdBridge(_abilities!, GameInfo.GetStrings("en").abilitylist,
            new Dictionary<int, int> { [153] = (int)Ability.AsOneG, [154] = (int)Ability.AsOneI });
    }

    private static CfruIdBridge ItemBridge()
    {
        if (_items is null) LoadNames();
        return _itemBridge ??= new CfruIdBridge(_items!, GameInfo.GetStrings("en").itemlist, []);
    }

    private static void LoadNationalSpecies()
    {
        if (_nationalBySpecies is not null) return;
        var map = new Dictionary<int, int>(1400);
        foreach (var (id, text) in LoadNameMap("unbound.species_national.txt"))
            if (int.TryParse(text, out var national) && national > 0)
                map[id] = national;
        var reverse = new Dictionary<int, int>(1100);
        foreach (var (id, national) in map.OrderBy(pair => pair.Key))
            reverse.TryAdd(national, id);
        _speciesByNational = reverse;
        _nationalBySpecies = map;
    }

    /// <summary>Case/punctuation-insensitive name key: "Mr. Mime" == "Mr Mime",
    /// "Farfetch’d" == "Farfetch'd", "Flabébé" == "Flabebe", "Nidoran♀" == "NidoranF".</summary>
    internal static string NormalizeName(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (ch == '\u2640') builder.Append('f');
            else if (ch == '\u2642') builder.Append('m');
            else if (char.IsAsciiLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString();
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

/// <summary>A two-way id map between a CFRU hack's name table and a PKHeX name list,
/// joined on normalized names (first PKHeX id wins for duplicate names).</summary>
internal sealed class CfruIdBridge
{
    private readonly Dictionary<int, int> _toNational = [];
    private readonly Dictionary<int, int> _fromNational = [];

    public CfruIdBridge(Dictionary<int, string> hackNames, IReadOnlyList<string> pkhex, Dictionary<int, int> overrides)
    {
        var byName = new Dictionary<string, int>(pkhex.Count);
        for (var id = 1; id < pkhex.Count; id++)
        {
            var key = UnboundData.NormalizeName(pkhex[id]);
            if (key.Length > 0) byName.TryAdd(key, id);
        }
        foreach (var (id, name) in hackNames.OrderBy(pair => pair.Key))
        {
            if (id <= 0) continue;
            if (!overrides.TryGetValue(id, out var national) && !byName.TryGetValue(UnboundData.NormalizeName(name), out national))
                continue;
            _toNational[id] = national;
            _fromNational.TryAdd(national, id);
        }
    }

    public int ToNational(int id) => _toNational.TryGetValue(id, out var national) ? national : 0;
    public int FromNational(int national) => _fromNational.TryGetValue(national, out var id) ? id : 0;
}
