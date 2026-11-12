using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One of the seven Festival Plaza facility slots.</summary>
/// <param name="Type">Raw facility type: kind, flavour and level in one index (PKHeX's CB_FacilityType order).</param>
/// <param name="TypeLabel">Kind, flavour and level, e.g. "Lottery GoldRush 3".</param>
/// <param name="Color">Raw colour index within the kind's palette.</param>
/// <param name="ColorName">The colour that index draws for this kind.</param>
public sealed record FestivalFacility(int Index, int Type, string TypeLabel, int Kind, string KindName, int Level,
    int Color, string ColorName, int ColorCount, bool IsIntroduced);

/// <summary>A rank reward: 0 not yet, 1 ready to collect, 2 received (PKHeX's JoinFesta7 prize byte).</summary>
public sealed record FestivalReward(int Index, string Name, int State);

public sealed record FestivalPlazaInfo(string PlazaName, int Coins, int CoinsUsed, int CoinsCollected, int Rank,
    string RankCoinRange, int PhrasesUnlocked, int PhraseCount, IReadOnlyList<FestivalReward> Rewards,
    IReadOnlyList<FestivalFacility> Facilities);

/// <summary>
/// Festival Plaza for Sun/Moon and Ultra Sun/Ultra Moon, after PKHeX's WinForms
/// <c>SAV_FestivalPlaza</c> and <c>JoinFesta7</c>. Only the plain values are edited: coins
/// (current, with "used" read from record 38 and the collected total recomputed exactly as the
/// <c>FestaCoins</c> setter does), plaza rank 1-999, the rank rewards, the tent phrases, and each
/// facility's type and colour. Visitor identities, messages and the Battle Agency are left alone.
/// </summary>
public static class FestivalPlazaService
{
    public const int MaxCoins = 9_999_999;
    public const int MinRank = 1;
    public const int MaxRank = 999;
    public const int RecordCoinsUsed = 38;
    public const int FacilityCount = (int)JoinFesta7.FestaFacilityCount;

    /// <summary>Phrases 0-105 plus 106, the "Rank 100" phrase.</summary>
    public const int PhraseCount = 107;

    public const int RewardNone = 0;
    public const int RewardReady = 1;
    public const int RewardReceived = 2;

    /// <summary>SAV_FestivalPlaza res2: prize indices 0-9, then 10 for the current rank's prize.</summary>
    public static readonly IReadOnlyList<string> RewardNames =
    [
        "Rank 4: missions", "Rank 8: facility", "Rank 10: fashion", "Rank 20: rename", "Rank 30: special menu",
        "Rank 40: BGM", "Rank 50: theme Glitz", "Rank 60: theme Fairy", "Rank 70: theme Tone", "Rank 100: phrase",
        "Current rank",
    ];

    public static readonly IReadOnlyList<string> KindNames = ["Lottery", "Haunted", "Goody", "Food", "Bouncy", "Fortune", "Dye", "Exchange"];

    private static readonly string[][] Flavours =
    [
        ["Big Dream", "Gold Rush", "Treasure Hunt"],
        ["Ghosts Den", "Trick Room", "Confuse Ray"],
        ["Ball", "General", "Battle", "Soft Drink", "Pharmacy"],
        ["Rare", "Battle", "Friendship Café", "Friendship Parlor"],
        ["Thump", "Clink", "Stomp"],
        ["Kanto", "Johto", "Hoenn", "Sinnoh", "Unova", "Kalos", "Pokémon"],
        ["Red", "Yellow", "Green", "Blue", "Orange", "Navy Blue", "Purple", "Pink"],
        ["Switcheroo"],
    ];

    // SAV_FestivalPlaza.RES_FacilityLevelType: 3 = levels 1-3, 4 = levels 1/3/5, 5 = levels 1-5.
    private static readonly byte[][] LevelTypes =
    [
        [5, 5, 5], [5, 5, 5], [3, 5, 3, 3, 3], [5, 4, 5, 5], [5, 5, 5], [4, 4, 4, 4, 4, 4, 4], [4, 4, 4, 4, 4, 4, 4, 4], [3],
    ];

    // SAV_FestivalPlaza.RES_FacilityColor: colour index -> FestivalPlazaFacilityColor, per kind.
    private static readonly byte[][] Palettes =
    [
        [0, 1, 2, 3], [4, 0, 5, 3], [1, 0, 5, 3], [6, 7, 0, 3], [4, 5, 8, 3], [0, 1, 2, 3], [0, 7, 8, 4, 5, 1, 9, 10], [11, 1, 5, 3],
    ];

    private static readonly IReadOnlyList<(int Kind, string Label, int Level)> TypeTable = BuildTypes();

    public static bool IsSupported(ISaveEngineSession session) => TryGetSave(session) is not null;

    /// <summary>Facility types this game offers, in raw-index order (Exchange is US/UM only).</summary>
    public static IReadOnlyList<string> GetFacilityTypes(ISaveEngineSession session)
    {
        var count = TypeCount(RequireSave(session));
        return [.. TypeTable.Take(count).Select(t => t.Label)];
    }

    public static FestivalPlazaInfo Get(ISaveEngineSession session)
    {
        var save = RequireSave(session);
        var festa = save.Festa;
        var used = save.GetRecord(RecordCoinsUsed);
        var phrases = Enumerable.Range(0, PhraseCount).Count(festa.GetFestaPhraseUnlocked);
        var rewards = RewardNames.Select((name, i) => new FestivalReward(i, name, festa.GetFestPrizeReceived(i))).ToList();
        var facilities = Enumerable.Range(0, FacilityCount).Select(i => Describe(save, i, festa.GetFestaFacility(i))).ToList();
        return new FestivalPlazaInfo(festa.FestivalPlazaName, festa.FestaCoins, used, festa.TotalFestaCoins,
            festa.FestaRank, RankCoinRange(festa.FestaRank), phrases, PhraseCount, rewards, facilities);
    }

    /// <summary>Current coins (clamped 0-9,999,999); JoinFesta7 rewrites the collected total as used + current.</summary>
    public static void SetCoins(ISaveEngineSession session, int coins) =>
        RequireSave(session).Festa.FestaCoins = Math.Clamp(coins, 0, MaxCoins);

    /// <summary>Plaza rank, SAV_FestivalPlaza's NUD_Rank range (1-999). The rank's coin range is
    /// informational; the coins are not changed with it, as in PKHeX.</summary>
    public static void SetRank(ISaveEngineSession session, int rank) =>
        RequireSave(session).Festa.FestaRank = (ushort)Math.Clamp(rank, MinRank, MaxRank);

    /// <summary>SAV_FestivalPlaza.B_AllPhrases_Click: every tent phrase, the Rank 100 one included.</summary>
    public static void UnlockAllPhrases(ISaveEngineSession session)
    {
        var festa = RequireSave(session).Festa;
        for (var i = 0; i < PhraseCount; i++)
            festa.SetFestaPhraseUnlocked(i, true);
    }

    /// <summary>Every rank reward to one state: B_AllReceiveReward (received) or B_AllReadyReward (ready to collect).</summary>
    public static void SetAllRewards(ISaveEngineSession session, int state)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(state);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(state, RewardReceived);
        var festa = RequireSave(session).Festa;
        for (var i = 0; i < RewardNames.Count; i++)
            festa.SetFestaPrizeReceived(i, (byte)state);
    }

    /// <summary>
    /// SAV_FestivalPlaza.CB_FacilityType_SelectedIndexChanged: a new type keeps the colour when
    /// the new kind's palette has it, else takes the palette's last colour; the Fortune and
    /// Exchange extras are cleared as SaveFacility does for other kinds.
    /// </summary>
    public static FestivalFacility SetFacilityType(ISaveEngineSession session, int index, int type)
    {
        var save = RequireSave(session);
        ArgumentOutOfRangeException.ThrowIfNegative(type);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(type, TypeCount(save));
        var facility = Facility(save, index);
        facility.Type = type;
        var kind = TypeTable[type].Kind;
        var maxColor = Palettes[kind].Length - 1;
        if (facility.Color > maxColor) facility.Color = maxColor;
        if (kind != 7) facility.ExchangeLeftCount = 0;
        if (kind != 5) { facility.UsedLuckyRank = 0; facility.UsedLuckyPlace = 0; }
        return Describe(save, index, facility);
    }

    public static FestivalFacility SetFacilityColor(ISaveEngineSession session, int index, int color)
    {
        var save = RequireSave(session);
        var facility = Facility(save, index);
        var kind = KindOf(facility.Type);
        ArgumentOutOfRangeException.ThrowIfNegative(color);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(color, PaletteOf(save, kind).Length - 1);
        facility.Color = color;
        return Describe(save, index, facility);
    }

    /// <summary>The colour names a facility kind can take, for a picker.</summary>
    public static IReadOnlyList<string> GetColorNames(ISaveEngineSession session, int type)
    {
        var save = RequireSave(session);
        return [.. PaletteOf(save, KindOf(type)).Select(c => ((FestivalPlazaFacilityColor)c).ToString())];
    }

    /// <summary>SAV_FestivalPlaza.GetRankText: the total-coin range that reaches this rank.</summary>
    public static string RankCoinRange(int rank)
    {
        if (rank < 1) return string.Empty;
        if (rank == 1) return "0 - 5";
        if (rank == 2) return "6 - 15";
        if (rank == 3) return "16 - 30";
        if (rank <= 10)
        {
            var i = ((rank - 1) * (rank - 2) * 5) + 1;
            return $"{i} - {i + ((rank - 1) * 10) - 1}";
        }
        if (rank <= 20)
        {
            var i = (rank * 100) - 649;
            return $"{i} - {i + 99}";
        }
        if (rank <= 70)
        {
            var j = (rank - 1) / 10;
            var i = (rank * ((j * 30) + 60)) - ((j * j * 150) + (j * 180) + 109);
            return $"{i} - {i + (j * 30) + 59}";
        }
        if (rank <= 100)
        {
            var i = (rank * 270) - 8719;
            return $"{i} - {i + 269}";
        }
        if (rank <= 998)
        {
            var i = (rank * 300) - 11749;
            return $"{i} - {i + 299}";
        }
        return rank == 999 ? "287951 - " : string.Empty;
    }

    private static FestivalFacility Describe(SAV7 save, int index, FestaFacility facility)
    {
        var type = facility.Type;
        var known = type < TypeCount(save);
        var kind = known ? TypeTable[type].Kind : -1;
        var palette = PaletteOf(save, kind);
        var colorName = facility.Color < palette.Length ? ((FestivalPlazaFacilityColor)palette[facility.Color]).ToString() : $"Colour {facility.Color}";
        return new FestivalFacility(index, type, known ? TypeTable[type].Label : $"Unknown ({type})",
            kind, kind >= 0 ? KindNames[kind] : "Unknown", known ? TypeTable[type].Level : 0,
            facility.Color, colorName, palette.Length, facility.IsIntroduced);
    }

    private static FestaFacility Facility(SAV7 save, int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, FacilityCount);
        return save.Festa.GetFestaFacility(index);
    }

    private static int KindOf(int type) => (uint)type < (uint)TypeTable.Count ? TypeTable[type].Kind : -1;

    /// <summary>SAV_FestivalPlaza.GetColorCount: an unknown kind (or Exchange outside US/UM) gets four colours.</summary>
    private static byte[] PaletteOf(SAV7 save, int kind)
    {
        var kinds = save is SAV7USUM ? Palettes.Length : Palettes.Length - 1;
        return kind >= 0 && kind < kinds ? Palettes[kind] : [0, 1, 2, 3];
    }

    private static int TypeCount(SAV7 save) => save is SAV7USUM ? TypeTable.Count : TypeTable.Count(t => t.Kind != 7);

    private static List<(int Kind, string Label, int Level)> BuildTypes()
    {
        var list = new List<(int, string, int)>();
        for (var kind = 0; kind < LevelTypes.Length; kind++)
        {
            for (var flavour = 0; flavour < LevelTypes[kind].Length; flavour++)
            {
                var name = $"{KindNames[kind]} · {Flavours[kind][flavour]}";
                int[] levels = LevelTypes[kind][flavour] == 4 ? [1, 3, 5] : [.. Enumerable.Range(1, LevelTypes[kind][flavour])];
                foreach (var level in levels)
                    list.Add((kind, $"{name} · Lv. {level}", level));
            }
        }
        return list;
    }

    private static SAV7? TryGetSave(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile as SAV7 : null;

    private static SAV7 RequireSave(ISaveEngineSession session) =>
        TryGetSave(session) ?? throw new NotSupportedException("Festival Plaza exists only in Sun, Moon, Ultra Sun and Ultra Moon saves.");
}
