using PKHeX.Core;

namespace PKForge.Engine.FireRedHacks;

/// <summary>
/// What each hack's ids mean, limited to what the real saves prove (FireRedHacks/Data/README.md
/// lists the evidence). Neither hack publishes its tables and its ROM is not read, so:
/// <list type="bullet">
/// <item>Species 1-251 and 277-411 keep FireRed's internal numbering in both hacks (Fire Red
/// Extended: 30 boxed legendaries and starters, Mew in the party; Crystal Advance Redux: the
/// party Chikorita). Fire Red Extended adds the national dex past Deoxys at internal id
/// national + 53 (Turtwig 440 … Enamorus 958), proven by 87 boxed Pokémon of generations 4-8
/// whose default nickname is their species name and whose ids all fit that rule. Its ids past
/// 958 (forms and new Megas) are shown as "Species #N".</item>
/// <item>Moves 1-354 are FireRed's (the level-10 starters' moves in Fire Red Extended, the
/// Chikorita's in Crystal Advance Redux). Fire Red Extended's moves past 354 follow neither the
/// national nor the CFRU order and stay unnamed: the move pickers offer 1-354 only.</item>
/// <item>Items keep FireRed's ids, except ids the hacks reuse: the slots FireRed leaves unnamed
/// ("???", Fire Red Extended stores item 91 ×999 in its bag) and, in Crystal Advance Redux, the
/// mail block (Orange Mail, id 121, sits in its Key Items pocket). Those are never offered.</item>
/// <item>Growth rates: FireRed's for species 1-411 (every boxed Fire Red Extended Pokémon of
/// that range holds the exact level-10 EXP of its retail rate). Fire Red Extended changed some
/// past 411 (its Arceus and Eternatus hold Fluctuating level-10 EXP, Type: Null Medium Fast),
/// so their level is never written.</item>
/// <item>Base stats, types and abilities are never assumed for Fire Red Extended. A party
/// Pokémon's stats are only recomputed when its stored stats equal FireRed's formula for its
/// species, which proves the data for that Pokémon (both party Pokémon in the samples pass).</item>
/// </list>
/// </summary>
internal sealed class FireRedHackTables
{
    public static readonly FireRedHackTables FireRedExtended = new(FireRedHackGame.FireRedExtended);
    public static readonly FireRedHackTables CrystalAdvanceRedux = new(FireRedHackGame.CrystalAdvanceRedux);

    public static FireRedHackTables For(FireRedHackGame game) =>
        game == FireRedHackGame.FireRedExtended ? FireRedExtended : CrystalAdvanceRedux;

    /// <summary>Highest move id whose name both hacks prove (FireRed's table).</summary>
    public const int MaxKnownMove = 354;
    /// <summary>Highest retail Gen 3 internal species id.</summary>
    private const int RetailInternalMax = 411;
    private const int ExtendedFirst = 440;
    private const int ExtendedLast = 958;
    private const int ExtendedShift = 53;

    private readonly FireRedHackGame _game;
    private readonly Lazy<string[]> _items;

    private FireRedHackTables(FireRedHackGame game)
    {
        _game = game;
        _items = new(BuildItemNames);
    }

    private static GameStrings Strings => GameInfo.GetStrings("en");

    public string GameName => _game == FireRedHackGame.FireRedExtended ? "Fire Red Extended" : "Crystal Advance Redux";

    /// <summary>The national dex number of a stored species id, or 0 when it is not proven.</summary>
    public int NationalOf(int raw)
    {
        if (raw is >= 1 and <= 251 or >= 277 and <= RetailInternalMax)
            return SpeciesConverter.GetNational3((ushort)raw);
        if (_game == FireRedHackGame.FireRedExtended && raw is >= ExtendedFirst and <= ExtendedLast)
            return raw - ExtendedShift;
        return 0;
    }

    /// <summary>True when the species' data (growth, base stats) is FireRed's own.</summary>
    public static bool IsRetailSpecies(int raw) => raw is >= 1 and <= 251 or >= 277 and <= RetailInternalMax;

    public string SpeciesName(int raw) =>
        NationalOf(raw) is > 0 and var national ? Strings.specieslist[national] : $"Species #{raw}";

    /// <summary>FireRed's personal data for a retail species, or null when the hack's own data is unknown.</summary>
    public static PersonalInfo3? RetailPersonal(int raw) =>
        IsRetailSpecies(raw) ? PersonalTable.FR[SpeciesConverter.GetNational3((ushort)raw)] : null;

    /// <summary>Growth rate of a species, or null when the hack's rate is not proven.</summary>
    public static byte? Growth(int raw) => RetailPersonal(raw)?.EXPGrowth;

    /// <summary>Level from EXP when the growth rate is proven, else null.</summary>
    public static int? LevelOf(int raw, uint experience) =>
        Growth(raw) is { } growth ? Experience.GetLevel(experience, growth) : null;

    /// <summary>A display level for a species whose rate is unknown: the level whose threshold
    /// the EXP equals in exactly one of the six Gen 3 rates (a Pokémon generated at a level holds
    /// that threshold), else 0, meaning "unknown". Never written.</summary>
    public static int InferredLevel(uint experience)
    {
        var found = 0;
        for (byte growth = 0; growth < 6; growth++)
        for (byte level = 2; level <= 100; level++)
        {
            if (Experience.GetEXP(level, growth) != experience) continue;
            if (found != 0 && found != level) return 0;
            found = level;
        }
        return found;
    }

    public static string MoveName(int move) =>
        move is > 0 and <= MaxKnownMove ? Strings.movelist[move] : move == 0 ? string.Empty : $"Move #{move}";

    /// <summary>The move as the app's national id: FireRed's ids up to 354, 0 past (unnamed).</summary>
    public static int MoveToNational(int move) => move is > 0 and <= MaxKnownMove ? move : 0;

    public static int BasePp(int move) => move is > 0 and <= MaxKnownMove ? MoveInfo.GetPP(EntityContext.Gen3, (ushort)move) : 0;

    /// <summary>Gen 3 item names indexed by id, with every id this hack may have reused blanked
    /// (an empty name is "no such item" to every picker).</summary>
    public IReadOnlyList<string> ItemNames => _items.Value;

    public bool IsKnownItem(int item) => item == 0 || ((uint)item < (uint)_items.Value.Length && _items.Value[item].Length > 0);

    public string ItemName(int item) => IsKnownItem(item) ? _items.Value[item] : $"Item #{item}";

    private string[] BuildItemNames()
    {
        var names = Strings.GetItemStrings(EntityContext.Gen3, GameVersion.FR).ToArray();
        for (var id = 0; id < names.Length; id++)
        {
            var name = names[id] ?? string.Empty;
            if (name.Contains('?') || (_game == FireRedHackGame.CrystalAdvanceRedux && id is >= 121 and <= 132))
                name = string.Empty;
            names[id] = name;
        }
        names[0] = string.Empty;
        return names;
    }

    /// <summary>FireRed's Gen 3 stat formula for a retail species, in app order, or null.</summary>
    public static int[]? RetailStats(int raw, int level, IReadOnlyList<int> ivs, IReadOnlyList<int> evs, int nature)
    {
        if (RetailPersonal(raw) is not { } personal)
            return null;
        int[] bases = [personal.HP, personal.ATK, personal.DEF, personal.SPA, personal.SPD, personal.SPE];
        var stats = new int[6];
        stats[0] = bases[0] == 1 ? 1 : (2 * bases[0] + ivs[0] + evs[0] / 4) * level / 100 + level + 10;
        // Nature order in PID % 25: up = nature / 5, down = nature % 5 over Atk, Def, Spe, SpA, SpD.
        int[] natureIndex = [-1, 0, 1, 3, 4, 2]; // app index → nature stat index (Atk 0, Def 1, Spe 2, SpA 3, SpD 4)
        for (var i = 1; i < 6; i++)
        {
            var value = (2 * bases[i] + ivs[i] + evs[i] / 4) * level / 100 + 5;
            var up = nature / 5;
            var down = nature % 5;
            if (up != down)
            {
                if (natureIndex[i] == up) value = value * 110 / 100;
                else if (natureIndex[i] == down) value = value * 90 / 100;
            }
            stats[i] = value;
        }
        return stats;
    }
}
