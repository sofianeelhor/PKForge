using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// What the Hall of Fame museum reads from one save. Only what PKHeX.Core exposes:
///  - Hall of Fame teams through <see cref="HallOfFameService"/> (Gen 1, 3, 6, 7);
///  - clears: <c>SAV1.HallOfFameCount</c>; Gen 3 <c>ENTERED_HOF</c> (<see cref="RecID3Emerald"/> and kin);
///    Gen 4 HeartGold/SoulSilver <c>LeagueWins</c> (<see cref="Record4.Record4HGSSIndex"/>);
///    Gen 5 record 71 "Champion Beaten" (<see cref="RecordLists.RecordList_5"/>); the Gen 6 Hall of Fame's clear number;
///  - first clear: Gen 3 <c>FIRST_HOF_PLAY_TIME</c>, Gen 4-7 <c>SecondsToFame</c>;
///  - records: Emerald's Battle Frontier (<see cref="BattleFrontier3"/>), Ruby/Sapphire's Battle Tower and
///    Hoenn contests (Gen 3 game stats), HeartGold/SoulSilver's Battle Tower wins and Pokéathlon best scores,
///    Black/White and Black 2/White 2's Battle Subway (<see cref="BattleSubway5"/>), X/Y and Omega Ruby/Alpha
///    Sapphire's Battle Maison streaks (<see cref="RecordLists.RecordList_6"/>), Sun/Moon and Ultra Sun/Ultra
///    Moon's Battle Tree (<see cref="BattleTree7"/>), Sword/Shield's Battle Tower streaks.
/// Gen 4 Platinum/HeartGold/SoulSilver Battle Frontier streaks have no model in PKHeX.Core, so they are not shown.
/// The session is only read; nothing is written.
/// </summary>
public static class MuseumService
{
    public static MuseumReading Read(ISaveEngineSession session)
    {
        if (session is not SaveEngineSession engine)
            return new MuseumReading(0, null, null, null, null, [], 0, 0, []);
        var save = engine.SaveFile;
        HallOfFameInfo? fame = null;
        if (HallOfFameService.IsSupported(session))
        {
            try { fame = HallOfFameService.Get(session); }
            catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or InvalidOperationException) { }
        }

        IReadOnlyList<TrainerBadge> badges = [];
        try
        {
            var card = TrainerCardService.Get(session);
            if (card.SupportsBadges) badges = card.Badges;
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or InvalidOperationException or NotSupportedException) { }

        var records = new List<MuseumRecord>();
        int? clears = null;
        string? firstPlayTime = null;
        try
        {
            switch (save)
            {
                case SAV1 s:
                    clears = s.HallOfFameCount;
                    break;
                case SAV3 s:
                    clears = (int)Math.Min(int.MaxValue, s.GetRecord((int)RecID3Emerald.ENTERED_HOF));
                    firstPlayTime = Museum.Gen3PlayTime(s.GetRecord((int)RecID3Emerald.FIRST_HOF_PLAY_TIME));
                    Gen3(s, records);
                    break;
                case SAV4HGSS s:
                {
                    // One accessor for both reads: each new Record4 decrypts the block in place, and
                    // EndAccess puts it back encrypted as the game keeps it.
                    var stats = s.Records;
                    try
                    {
                        clears = stats.GetRecord16((int)Record4.Record4HGSSIndex.LeagueWins - (int)Record4.Record4HGSSIndex.FirstU16);
                        Add(records, "Battle Tower", "Wins", (int)Math.Min(int.MaxValue, stats.GetRecord32((int)Record4.Record4HGSSIndex.BattleTowerWins)));
                    }
                    finally { stats.EndAccess(); }
                    for (var e = PokeathlonEvent4.HurdleDash; e < PokeathlonEvent4.Count; e++)
                        Add(records, "Pokéathlon", Words(e.ToString()), s.Pokeathlon.GetBestScore(e));
                    break;
                }
                case SAV5 s:
                    // "Champion Beaten" is record 71, the fourth of the 16-bit records.
                    var stats5 = s.Records;
                    try { clears = stats5.GetRecord16(ChampionBeaten5 - Record5.Record32); }
                    finally { stats5.EndAccess(); }
                    Subway(s.BattleSubway, records);
                    break;
                case SAV6 s:
                    if (fame is { Teams.Count: > 0 })
                        clears = fame.Teams.Max(t => t.ClearNumber ?? 0) is > 0 and var n ? n : fame.Teams.Count;
                    string[] maison = ["Singles", "Doubles", "Triples", "Rotation", "Multi"];
                    for (var i = 0; i < maison.Length; i++)
                        Add(records, "Battle Maison", $"Best {maison[i].ToLowerInvariant()} streak", s.GetRecord(MaisonStreak6 + i));
                    Add(records, "League", "Title defenses", s.GetRecord(TitleDefense));
                    break;
                case SAV7 s:
                    string[] tree = ["Singles", "Doubles", "Multi"];
                    for (var i = 0; i < tree.Length; i++)
                    {
                        Add(records, "Battle Tree", $"Best {tree[i].ToLowerInvariant()} streak", s.BattleTree.GetTreeStreak(i, super: false, max: true));
                        Add(records, "Battle Tree", $"Best super {tree[i].ToLowerInvariant()}", s.BattleTree.GetTreeStreak(i, super: true, max: true));
                    }
                    Add(records, "League", "Title defenses", s.GetRecord(TitleDefense));
                    break;
                case SAV8SWSH s:
                    Add(records, "Battle Tower", "Singles streak", U16(s, SaveBlockAccessor8SWSH.KBattleTowerSinglesStreak));
                    Add(records, "Battle Tower", "Doubles streak", U16(s, SaveBlockAccessor8SWSH.KBattleTowerDoublesStreak));
                    break;
            }
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or InvalidOperationException or NotSupportedException or InvalidCastException)
        {
            // A layout the record offsets do not fit: the hall shows what was read before.
        }

        DateOnly? firstDate = null;
        if (save is SAV4 or SAV5 or SAV6 or SAV7 && save.SecondsToFame > 0)
            firstDate = DateOnly.FromDateTime(new DateTime(2000, 1, 1).AddSeconds(save.SecondsToFame));
        else if (fame?.Teams.FirstOrDefault(t => t.Index == 0)?.Date is { } dated)
            firstDate = dated;

        return new MuseumReading(save.Generation, fame, clears, firstDate, firstPlayTime, badges,
            save.PlayedHours, save.PlayedMinutes, records);
    }

    // Gen 5 "Champion Beaten"; Gen 6 Battle Maison streaks 147-151; Gen 6/7 "Champion Title Defense".
    private const int ChampionBeaten5 = 71;
    private const int MaisonStreak6 = 147;
    private const int TitleDefense = 100;

    private static void Add(List<MuseumRecord> records, string facility, string label, int value)
    {
        if (value > 0) records.Add(new MuseumRecord(facility, label, value));
    }

    private static string Words(string name) =>
        string.Concat(name.Select((ch, i) => i > 0 && char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : ch.ToString()));

    private static void Gen3(SAV3 s, List<MuseumRecord> records)
    {
        if (s is SAV3E e)
        {
            var frontier = e.SmallBlock.BattleFrontier;
            for (var facility = BattleFrontierFacility3.Tower; facility <= BattleFrontierFacility3.Pyramid; facility++)
            {
                var best = 0;
                for (var mode = 0; mode < BattleFrontier3.GetModeCount(facility); mode++)
                for (var level = BattleFrontierRecordType3.Level50; level <= BattleFrontierRecordType3.OpenLevel; level++)
                    best = Math.Max(best, frontier.GetStat(facility, (BattleFrontierBattleMode3)mode, level, BattleFrontierStatType3.RecordStreak));
                Add(records, $"Battle {facility}", "Best streak", best);
            }
        }
        else if (s is SAV3RS)
            Add(records, "Battle Tower", "Best streak", Stat(s, (int)RecID3RuSa.BATTLE_TOWER_BEST_STREAK));
        // Ruby, Sapphire and Emerald hold contests; FireRed/LeafGreen do not.
        if (s is SAV3RS or SAV3E)
            Add(records, "Contests", "Won", Stat(s, (int)RecID3Emerald.WON_CONTEST));
    }

    private static int Stat(SAV3 s, int record) => (int)Math.Min(int.MaxValue, s.GetRecord(record));

    private static void Subway(BattleSubway5 subway, List<MuseumRecord> records)
    {
        Add(records, "Battle Subway", "Best singles", subway.SingleRecord);
        Add(records, "Battle Subway", "Best doubles", subway.DoubleRecord);
        Add(records, "Battle Subway", "Best multi", Math.Max(subway.MultiNPCRecord, subway.MultiFriendsRecord));
        Add(records, "Battle Subway", "Best super singles", subway.SuperSingleRecord);
        Add(records, "Battle Subway", "Best super doubles", subway.SuperDoubleRecord);
        Add(records, "Battle Subway", "Best super multi", Math.Max(subway.SuperMultiNPCRecord, subway.SuperMultiFriendsRecord));
    }

    private static int U16(SAV8SWSH s, uint key) =>
        s.Blocks.TryGetBlock(key, out var block) && block.Type == SCTypeCode.UInt16 ? (ushort)block.GetValue() : 0;
}
