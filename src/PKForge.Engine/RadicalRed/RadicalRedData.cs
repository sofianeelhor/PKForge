using PKForge.Engine.Unbound;
using System.Reflection;
using PKHeX.Core;

namespace PKForge.Engine.RadicalRed;

/// <summary>
/// Radical Red's ROM-truth tables (species and item ids → names, from the v4.1 ROM via
/// the Rad-Red-4.1-Team-Exporter extraction, cross-checked against the owner's save)
/// plus the name bridge into PKHeX's modern national tables.
///
/// Radical Red ids are the CFRU engine's own: species ≤ 411 follow Gen 3's
/// Hoenn-internal order (Bulbasaur is not 1) and the expansion continues to 1375, so
/// ids and national numbers diverge everywhere past the overlap. NAMES are the only
/// safe key between the two worlds; anything derived (base stats, growth curve,
/// gender ratio, abilities, Pokédex bits) resolves through the national table and
/// degrades to a neutral fallback when the name bridge misses (form slots like
/// "Terapagos-Terastal" are not national species entries).
/// </summary>
internal static class RadicalRedData
{
    // The app opens sessions from worker threads, so every lazy table publishes a
    // fully-built object behind a volatile guard; a half-filled table would make
    // even save detection flunk (IsKnownSpecies feeds IsRadicalRed).
    private static readonly object LoadGate = new();
    private static volatile Dictionary<int, string>? _species;
    private static volatile Dictionary<int, string>? _items;
    private static Dictionary<string, int>? _speciesByName;
    private static volatile Dictionary<string, int>? _nationalByName;
    private static string[] _speciesList = [];
    private static string[] _moveList = [];
    private static string[] _abilityList = [];
    private static int _maxSpecies;
    private static int _maxItem;

    public static string SpeciesName(int species)
    {
        LoadSpecies();
        return _species!.TryGetValue(species, out var name) && name.Length > 0 && name != "."
            ? name
            : $"#{species}";
    }

    public static string ItemName(int item)
    {
        LoadItems();
        return _items!.TryGetValue(item, out var name) && name.Length > 0 && name != "."
            ? name
            : $"#{item}";
    }

    public static int MaxSpeciesId
    {
        get
        {
            LoadSpecies();
            return _maxSpecies;
        }
    }

    public static int MaxItemId
    {
        get
        {
            LoadItems();
            return _maxItem;
        }
    }

    // ── Bag pockets ──
    // The engine routes every item to one of the five bag pockets through its ROM
    // item table, which is not extractable without the ROM. This map mixes retail
    // zones with name rules and is validated entry-for-entry against the champion
    // save — all 294 stored items agree (including Radical Red's own quirks: Exp.
    // Share is a key item here, the Primal Orbs are hold items, and Light/Smoke/Iron
    // "Balls" are not balls). Balls are retail ids 1..12 plus the CFRU block
    // 239..253; berries and TM/HM machines match by name; key items are the FireRed
    // key zones 259..265 and 347..374 plus Exp. Share (182). Everything else —
    // including the unreachable Hoenn leftovers at 266..288 — reads as the Items
    // pocket, exactly as observed.
    private static volatile BagPocketSets? _pockets;

    /// <summary>Item ids that belong to a bag pocket family.</summary>
    public static IReadOnlyCollection<int> PocketIds(string family)
    {
        var pockets = Pockets();
        return family switch
        {
            "ball" => pockets.Ball,
            "berry" => pockets.Berry,
            "tm" => pockets.Machine,
            "key" => pockets.Key,
            _ => [],
        };
    }

    /// <summary>True unless the item belongs to the balls/berries/TM/key pockets.</summary>
    public static bool IsSpecialPocketItem(int itemId)
    {
        var pockets = Pockets();
        return pockets.Ball.Contains(itemId) || pockets.Berry.Contains(itemId)
            || pockets.Machine.Contains(itemId) || pockets.Key.Contains(itemId);
    }

    /// <summary>ROM filler slots ("-DONT USE- -", "Free Space*") never enter a picker.</summary>
    public static bool IsFillerItem(int item)
    {
        LoadItems();
        return !_items!.TryGetValue(item, out var name) || name.Length == 0 || name == "."
            || name.StartsWith('-') || name.Contains("Free Space", StringComparison.Ordinal);
    }

    private static BagPocketSets Pockets()
    {
        if (_pockets is not null) return _pockets;
        lock (LoadGate)
        {
            if (_pockets is not null) return _pockets;
            LoadItems();
            var ball = new HashSet<int>();
            var berry = new HashSet<int>();
            var machine = new HashSet<int>();
            var key = new HashSet<int>();
            foreach (var (id, name) in _items!)
            {
                if (id is >= 1 and <= 12 or >= 239 and <= 253)
                    ball.Add(id);
                else if (name.EndsWith(" Berry", StringComparison.Ordinal))
                    berry.Add(id);
                else if (IsMachineName(name))
                    machine.Add(id);
                else if (id is >= 259 and <= 265 or >= 347 and <= 374 or 182)
                    key.Add(id);
            }
            _pockets = new BagPocketSets(ball, berry, machine, key);
            return _pockets;
        }
    }

    private sealed record BagPocketSets(HashSet<int> Ball, HashSet<int> Berry, HashSet<int> Machine, HashSet<int> Key);

    private static bool IsMachineName(string name)
    {
        if (name.Length < 3 || name[1] != 'M' || name[0] is not ('T' or 'H'))
            return false;
        foreach (var c in name[2..])
            if (!char.IsAsciiDigit(c))
                return false;
        return true;
    }

    public static bool IsKnownSpecies(int species)
    {
        LoadSpecies();
        return species is > 0 && _species!.ContainsKey(species);
    }

    /// <summary>Radical Red species id for a species NAME. Form slots duplicate their
    /// base name; the lowest id of a duplicate wins (the base form).</summary>
    public static int SpeciesIdByName(string name)
    {
        if (name.Length == 0) return 0;
        if (_speciesByName is null)
        {
            LoadSpecies();
            _speciesByName = new Dictionary<string, int>(_species!.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _species.Keys.Order())
                if (!_speciesByName.ContainsKey(_species[entry]))
                    _speciesByName[_species[entry]] = entry;
        }
        return _speciesByName.TryGetValue(name, out var species) ? species : 0;
    }

    /// <summary>The national species id behind a Radical Red species id, or 0 when the
    /// name bridge misses. Form names ("Terapagos-Terastal") fall back to their base
    /// name by dropping trailing dash segments until a national name matches.</summary>
    public static int NationalIdOf(int species)
    {
        LoadStrings();
        if (!IsKnownSpecies(species)) return 0;
        var name = SpeciesName(species);
        if (_nationalByName!.TryGetValue(name, out var national)) return national;
        while (name.Contains('-'))
        {
            name = name[..name.LastIndexOf('-')];
            if (_nationalByName.TryGetValue(name, out national)) return national;
        }
        return 0;
    }

    // ── Move ids ──
    // Radical Red's move table is the CFRU engine's, which shares ids with every Gen 3
    // game for 1..354 (the frozen pre-Gen 4 national move numbering) and diverges
    // beyond (the champion's Terapagos stores Earth Power as 372 and Flash Cannon as
    // 449, neither of which is the national id). Past the shared zone the ids follow
    // CFRU's include/constants/moves.h (github.com/Skeli789/Complete-Fire-Red-Upgrade),
    // which is id-for-id the Unbound table through 766 (Take Heart) and agrees with
    // both champion ground-truth points, so those ids bridge through it; ids past 766
    // are unverified for this hack and stay unnamed (and are never overwritten by an
    // echoed edit).
    public const int SharedMoveLimit = 354;
    public const int CfruMoveLimit = 766;

    public static string MoveName(int move)
    {
        LoadStrings();
        if (move is > 0 and <= SharedMoveLimit && move < _moveList.Length && _moveList[move].Length > 0)
            return _moveList[move];
        return move is > SharedMoveLimit and <= CfruMoveLimit ? UnboundData.MoveName(move) : $"#{move}";
    }

    /// <summary>The PKHeX (national) move id behind a stored move, or 0 when unknown.</summary>
    public static int MoveToNational(int move) => move switch
    {
        > 0 and <= SharedMoveLimit => move,
        > SharedMoveLimit and <= CfruMoveLimit => UnboundData.MoveToNational(move),
        _ => 0,
    };

    /// <summary>The stored id for a PKHeX (national) move, or 0 when the table lacks it.</summary>
    public static int MoveFromNational(int national)
    {
        if (national is > 0 and <= SharedMoveLimit) return national;
        var id = UnboundData.MoveFromNational(national);
        return id is > SharedMoveLimit and <= CfruMoveLimit ? id : 0;
    }

    /// <summary>Stores a move requested by its PKHeX name.</summary>
    public static int MoveIdByName(string name)
    {
        LoadStrings();
        if (name.Length == 0) return 0;
        for (var id = 1; id < _moveList.Length; id++)
            if (_moveList[id].Equals(name, StringComparison.OrdinalIgnoreCase))
                return MoveFromNational(id);
        return 0;
    }

    /// <summary>The Radical Red species id for a national species (lowest id of that
    /// name), or 0 when the table lacks it.</summary>
    public static int SpeciesFromNational(int national)
    {
        LoadStrings();
        return national > 0 && national < _speciesList.Length ? SpeciesIdByName(_speciesList[national]) : 0;
    }

    // ── Held items ──
    // Radical Red's item ids are its own table (items.txt); the UI's held-item picker
    // speaks PKHeX's modern item ids, so held items bridge by name.
    private static CfruIdBridge? _itemBridge;

    private static CfruIdBridge ItemBridge()
    {
        LoadItems();
        return _itemBridge ??= new CfruIdBridge(_items!, GameInfo.GetStrings("en").itemlist, []);
    }

    public static int ItemToNational(int item) => item == 0 ? 0 : ItemBridge().ToNational(item);
    public static int ItemFromNational(int national) => national == 0 ? 0 : ItemBridge().FromNational(national);

    /// <summary>Ability ids are CFRU's modern-numbered table; the display name comes
    /// from PKHeX's national ability list.</summary>
    public static string AbilityName(int ability)
    {
        LoadStrings();
        return ability > 0 && ability < _abilityList.Length && _abilityList[ability].Length > 0
            ? _abilityList[ability]
            : $"#{ability}";
    }

    private static void LoadStrings()
    {
        if (_nationalByName is not null) return;
        lock (LoadGate)
        {
            if (_nationalByName is not null) return;
            var strings = GameInfo.GetStrings("en");
            var national = new Dictionary<string, int>(strings.specieslist.Length, StringComparer.OrdinalIgnoreCase);
            for (var id = 1; id < strings.specieslist.Length; id++)
                if (strings.specieslist[id].Length > 0 && !national.ContainsKey(strings.specieslist[id]))
                    national[strings.specieslist[id]] = id;
            _speciesList = strings.specieslist;
            _moveList = strings.movelist;
            _abilityList = strings.abilitylist;
            _nationalByName = national;
        }
    }

    // ── National-table derived data (base stats, growth, gender, abilities) ──
    // Shared with every CFRU hack: see CfruNationalTraits.

    /// <summary>Base stats in app order: HP, Atk, Def, SpA, SpD, Spe.</summary>
    public static int[] BaseStats(int species) => CfruNationalTraits.BaseStats(NationalIdOf(species));

    /// <summary>Primary + secondary type ids (modern national numbering).</summary>
    public static int[] TypesOf(int species) => CfruNationalTraits.TypesOf(NationalIdOf(species));

    /// <summary>0 male, 1 female, 2 genderless.</summary>
    public static int GenderOf(uint pid, int species) => CfruNationalTraits.GenderOf(pid, NationalIdOf(species));

    public static int GenderThreshold(int species) => CfruNationalTraits.GenderThreshold(NationalIdOf(species));

    public static (int A1, int A2, int Hidden) AbilityIds(int species) => CfruNationalTraits.AbilityIds(NationalIdOf(species));

    public static int ActiveAbility(RadicalRedMon mon) => CfruNationalTraits.ActiveAbility(mon, NationalIdOf(mon.Species));

    public static uint ExperienceAtLevel(int species, int level) =>
        CfruNationalTraits.ExperienceAtLevel(NationalIdOf(species), level);

    public static int LevelForExperience(int species, uint experience) =>
        CfruNationalTraits.LevelForExperience(NationalIdOf(species), experience);

    /// <summary>Battle stats for a PC mon (party mons carry theirs in the save tail).</summary>
    public static int[] ComputeStats(RadicalRedMon mon) => CfruNationalTraits.ComputeStats(mon, NationalIdOf(mon.Species));

    private static void LoadSpecies()
    {
        if (_species is not null) return;
        lock (LoadGate)
        {
            if (_species is not null) return;
            var species = new Dictionary<int, string>();
            var max = 0;
            foreach (var (id, name) in LoadTable("radicalred.species.txt"))
            {
                species[id] = name;
                if (id > max)
                    max = id;
            }
            _maxSpecies = max;
            _species = species;
        }
    }

    private static void LoadItems()
    {
        if (_items is not null) return;
        lock (LoadGate)
        {
            if (_items is not null) return;
            var items = new Dictionary<int, string>();
            var max = 0;
            foreach (var (id, name) in LoadTable("radicalred.items.txt"))
            {
                items[id] = name;
                if (id > max)
                    max = id;
            }
            _maxItem = max;
            _items = items;
        }
    }

    /// <summary>The exporter tables are `id&lt;TAB&gt;name` with `#` provenance comments.</summary>
    private static Dictionary<int, string> LoadTable(string resource)
    {
        var map = new Dictionary<int, string>(2048);
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing embedded resource {resource}.");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line[0] == '#')
                continue;
            var separator = line.IndexOf('\t');
            if (separator <= 0) continue;
            if (int.TryParse(line.AsSpan(0, separator), out var id))
                map[id] = line[(separator + 1)..].Trim();
        }
        return map;
    }
}
