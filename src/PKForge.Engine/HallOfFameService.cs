using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Hall of Fame records, after PKHeX WinForms:
///  - Gen 1: <c>SAV_HallOfFame1</c> over <c>HallOfFameReader1</c> (50 teams; species, level, nickname) and <c>SAV1.HallOfFameCount</c>;
///  - Gen 3: <c>SAV_HallOfFame3</c> over <c>HallFame3Entry</c> (50 teams in the two Hall of Fame sectors; adds PID and TID/SID);
///  - Gen 6: <c>SAV_HallOfFame</c> over <c>HallOfFame6</c> (first clear + 15 latest; full cards, date and clear number);
///  - Gen 7: <c>SAV_HallOfFame7</c> over <c>EventWork7.Fame</c> (species of the first and the latest team).
/// Gen 2, 4, 5 and 8+ Hall of Fame data has no structured model in PKHeX.Core, so it is not offered.
/// Every write changes the save in place and is meant to run inside the app's mutation path.
/// </summary>
public static class HallOfFameService
{
    private const int Members = 6;
    private static readonly Lazy<GameStrings> Strings = new(() => GameInfo.GetStrings("en"));

    /// <summary>False for ROM hack sessions: their Hall of Fame sits where PKHeX does not look.</summary>
    public static bool IsSupported(ISaveEngineSession session) =>
        session is SaveEngineSession { SaveFile: { } save } && Caps(save) is not null;

    public static HallOfFameInfo Get(ISaveEngineSession session)
    {
        var save = Save(session);
        var caps = Caps(save) ?? throw Unsupported();
        return save switch
        {
            SAV1 s => new(1, caps, ReadGen1(s), s.HallOfFameCount),
            SAV3 s => new(3, caps, ReadGen3(s), null),
            SAV6 s => new(6, caps, ReadGen6(s), null),
            SAV7 s => new(7, caps, ReadGen7(s), null),
            _ => throw Unsupported(),
        };
    }

    /// <summary>Replaces one member of the team in storage slot <paramref name="team"/>.</summary>
    public static HallOfFameInfo SetMember(ISaveEngineSession session, int team, int slot, HallOfFameMemberEdit edit)
    {
        var save = Save(session);
        var caps = Check(save, team, slot);
        if (edit.Species == 0 || edit.Species > save.MaxSpeciesID)
            throw new ArgumentOutOfRangeException(nameof(edit), $"Species must be between 1 and {save.MaxSpeciesID}.");
        if (edit.Level is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(edit), "Level must be between 1 and 100.");
        if (edit.Nickname is { } nick && nick.Length > caps.NicknameMaxLength)
            throw new ArgumentException($"Nicknames are limited to {caps.NicknameMaxLength} characters.", nameof(edit));
        if (edit.OriginalTrainer is { Length: > 12 })
            throw new ArgumentException("Trainer names are limited to 12 characters.", nameof(edit));
        // The team is written in the save's own charset: map what can be, refuse the rest.
        if (edit.Nickname is { Length: > 0 } typedNick)
            edit = edit with { Nickname = NameCharset.FitForSave(save, typedNick, NameField.Nickname, caps.NicknameMaxLength) };
        if (edit.OriginalTrainer is { Length: > 0 } typedOt)
            edit = edit with { OriginalTrainer = NameCharset.FitForSave(save, typedOt, NameField.TrainerName, 12) };

        switch (save)
        {
            case SAV1 s: WriteGen1(s, team, slot, edit); break;
            case SAV3 s: WriteGen3(s, team, slot, edit); break;
            case SAV6 s: WriteGen6(s, team, slot, edit); break;
            case SAV7 s: s.EventWork.Fame.SetEntry(team * Members + slot, edit.Species); break;
        }
        return Get(session);
    }

    /// <summary>Empties one member slot; later members of the team move up so the team stays contiguous.</summary>
    public static HallOfFameInfo ClearMember(ISaveEngineSession session, int team, int slot)
    {
        var save = Save(session);
        Check(save, team, slot);
        switch (save)
        {
            case SAV1 s:
            {
                var fame = s.HallOfFame;
                for (var i = slot; i < Members - 1; i++)
                    fame.GetEntity(team, i + 1).Data.CopyTo(fame.GetEntity(team, i).Data);
                fame.GetEntity(team, Members - 1).Clear();
                break;
            }
            case SAV3 s:
            {
                var entries = HallFame3Entry.GetEntries(s);
                var e = entries[team];
                for (var i = slot; i < Members - 1; i++)
                    e.GetMember(i + 1).Data.CopyTo(e.GetMember(i).Data);
                e.GetMember(Members - 1).Data.Clear();
                HallFame3Entry.SetEntries(s, entries);
                break;
            }
            case SAV6 s:
            {
                var fame = Fame6(s);
                for (var i = slot; i < Members - 1; i++)
                    fame.GetEntity(team, i + 1).CopyTo(fame.GetEntity(team, i));
                fame.GetEntity(team, Members - 1).Clear();
                break;
            }
            case SAV7 s:
            {
                var fame = s.EventWork.Fame;
                for (var i = slot; i < Members - 1; i++)
                    fame.SetEntry(team * Members + i, fame.GetEntry(team * Members + i + 1));
                fame.SetEntry(team * Members + Members - 1, 0);
                break;
            }
        }
        return Get(session);
    }

    /// <summary>Deletes a team; later teams move up (Gen 1/3/6). Gen 7 empties its fixed slot.</summary>
    public static HallOfFameInfo DeleteTeam(ISaveEngineSession session, int team)
    {
        var save = Save(session);
        Check(save, team, 0);
        switch (save)
        {
            case SAV1 s: s.HallOfFame.Delete(team); break;
            case SAV3 s:
            {
                var entries = HallFame3Entry.GetEntries(s);
                for (var i = team; i < entries.Length - 1; i++)
                    entries[i].CopyFrom(entries[i + 1]);
                entries[^1].Data.Clear();
                HallFame3Entry.SetEntries(s, entries);
                break;
            }
            case SAV6 s: Fame6(s).ClearEntry(team); break;
            case SAV7 s:
                for (var i = 0; i < Members; i++)
                    s.EventWork.Fame.SetEntry(team * Members + i, 0);
                break;
        }
        return Get(session);
    }

    /// <summary>Empties every team (the Gen 1 clear counter is left alone; set it with <see cref="SetClearCount"/>).</summary>
    public static HallOfFameInfo ClearAll(ISaveEngineSession session)
    {
        var save = Save(session);
        _ = Caps(save) ?? throw Unsupported();
        switch (save)
        {
            case SAV1 s: s.HallOfFame.Clear(); break;
            case SAV3 s: s.SetHallOfFameData(new byte[s.GetHallOfFameData().Length]); break;
            case SAV6 s: Fame6(s).Data.Clear(); break;
            case SAV7 s:
                for (var i = 0; i < Members * 2; i++)
                    s.EventWork.Fame.SetEntry(i, 0);
                break;
        }
        return Get(session);
    }

    /// <summary>Gen 1's counter of champion clears (0-255).</summary>
    public static HallOfFameInfo SetClearCount(ISaveEngineSession session, int count)
    {
        if (Save(session) is not SAV1 s)
            throw new NotSupportedException("Only Red/Blue/Yellow keep a separate clear counter.");
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, byte.MaxValue);
        s.HallOfFameCount = (byte)count;
        return Get(session);
    }

    /// <summary>Gen 6: the date and clear number stored with a team (marks the slot as used).</summary>
    public static HallOfFameInfo SetTeamRecord(ISaveEngineSession session, int team, DateOnly date, int clearNumber)
    {
        if (Save(session) is not SAV6 s)
            throw new NotSupportedException("Only X/Y and Omega Ruby/Alpha Sapphire date their Hall of Fame entries.");
        Check(s, team, 0);
        if (date.Year is < 2000 or > 2255)
            throw new ArgumentOutOfRangeException(nameof(date), "Years run from 2000 to 2255.");
        ArgumentOutOfRangeException.ThrowIfNegative(clearNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(clearNumber, HallOfFame6.MaxComplete);
        WriteIndex6(Fame6(s), team, date, (uint)clearNumber);
        return Get(session);
    }

    /// <summary>Writes the current party in as the newest entry, as the game does at the Hall of Fame
    /// (Gen 1 shifts out the oldest team when all 50 are used and raises the clear counter; Gen 3 appends or
    /// replaces the last entry when full; Gen 6 uses PKHeX's insert slot and stamps <paramref name="date"/>).</summary>
    public static HallOfFameInfo RecordParty(ISaveEngineSession session, DateOnly? date = null)
    {
        var save = Save(session);
        if (save.PartyCount == 0)
            throw new InvalidOperationException("The party is empty.");
        switch (save)
        {
            case SAV1 s:
                s.HallOfFameCount = s.HallOfFame.RegisterParty(s, s.HallOfFameCount);
                break;
            case SAV3 s:
            {
                var entries = HallFame3Entry.GetEntries(s);
                var index = Array.FindIndex(entries, e => e.GetMember(0).Species == 0);
                if (index < 0)
                {
                    for (var i = 0; i < entries.Length - 1; i++)
                        entries[i].CopyFrom(entries[i + 1]);
                    index = entries.Length - 1;
                }
                entries[index].Data.Clear();
                entries[index].CopyFrom(save.PartyData.ToArray());
                HallFame3Entry.SetEntries(s, entries);
                break;
            }
            case SAV6 s:
            {
                var fame = Fame6(s);
                var index = (int)fame.GetInsertIndex(out var clear);
                fame.GetEntry(index).Clear();
                var party = save.PartyData;
                for (var i = 0; i < party.Count; i++)
                    WriteEntity6(s, fame.GetEntity(index, i), Card(party[i]));
                WriteIndex6(fame, index, date ?? DateOnly.FromDateTime(DateTime.Today), clear + 1);
                break;
            }
            default:
                throw new NotSupportedException("This game's Hall of Fame cannot record the party.");
        }
        return Get(session);
    }

    // ── Shared ──

    // ROM hack sessions are not PKHeX-backed: refuse them the way an unsupported game is refused.
    private static SaveFile Save(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile : throw Unsupported();

    private static NotSupportedException Unsupported() => new("This game's Hall of Fame is not supported.");

    private static HallOfFameCapabilities? Caps(SaveFile save) => save switch
    {
        SAV1 s => new(HallOfFameReader1.TeamCount, Members, s.Japanese ? 5 : 10,
            StoresLevel: true, StoresNickname: true, StoresTrainer: false, StoresPid: false,
            StoresMovesAndItem: false, StoresFormGenderShiny: false,
            StoresDate: false, StoresClearNumber: false, StoresClearCount: true,
            CanDeleteTeams: true, CanRecordParty: true),
        SAV3 s when s.IsFullSaveFile => new(50, Members, s.Japanese ? 5 : 10,
            StoresLevel: true, StoresNickname: true, StoresTrainer: true, StoresPid: true,
            StoresMovesAndItem: false, StoresFormGenderShiny: false,
            StoresDate: false, StoresClearNumber: false, StoresClearCount: false,
            CanDeleteTeams: true, CanRecordParty: true),
        SAV6XY or SAV6AO => new(HallOfFame6.Entries, Members, 12,
            StoresLevel: true, StoresNickname: true, StoresTrainer: true, StoresPid: true,
            StoresMovesAndItem: true, StoresFormGenderShiny: true,
            StoresDate: true, StoresClearNumber: true, StoresClearCount: false,
            CanDeleteTeams: true, CanRecordParty: true),
        SAV7 => new(2, Members, 0,
            StoresLevel: false, StoresNickname: false, StoresTrainer: false, StoresPid: false,
            StoresMovesAndItem: false, StoresFormGenderShiny: false,
            StoresDate: false, StoresClearNumber: false, StoresClearCount: false,
            CanDeleteTeams: true, CanRecordParty: false),
        _ => null,
    };

    private static HallOfFameCapabilities Check(SaveFile save, int team, int slot)
    {
        var caps = Caps(save) ?? throw Unsupported();
        ArgumentOutOfRangeException.ThrowIfNegative(team);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(team, caps.TeamSlots);
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, caps.MembersPerTeam);
        return caps;
    }

    private static string NameOf(ushort species) =>
        species < Strings.Value.specieslist.Length ? Strings.Value.specieslist[species] : $"#{species}";

    /// <summary>The species name the game would show; a Gen 3 save that does not record its language reads as English.</summary>
    private static string DefaultName(SaveFile save, ushort species, byte generation) =>
        SpeciesName.GetSpeciesNameGeneration(species, save.Language == 0 ? (int)LanguageID.English : save.Language, generation);

    private static HallOfFameMember Member(int slot, ushort species, int? level = null, string? nickname = null,
        bool? nicknamed = null, string? ot = null, int? otGender = null, ushort? tid = null, ushort? sid = null,
        uint? pid = null, int? form = null, int? gender = null, bool? shiny = null, int? item = null,
        IReadOnlyList<ushort>? moves = null) =>
        new(slot, species, NameOf(species), level, nickname, nicknamed, ot, otGender, tid, sid,
            pid, form, gender, shiny, item, moves ?? []);

    // ── Gen 1 ──

    private static List<HallOfFameTeam> ReadGen1(SAV1 s)
    {
        var fame = s.HallOfFame;
        var teams = new List<HallOfFameTeam>();
        for (var t = 0; t < HallOfFameReader1.TeamCount; t++)
        {
            var members = new List<HallOfFameMember>();
            for (var i = 0; i < Members; i++)
            {
                var e = fame.GetEntity(t, i);
                if (IsEmpty1(e))
                    break;
                members.Add(Member(i, e.Species, e.Level, e.Nickname));
            }
            if (members.Count != 0)
                teams.Add(new HallOfFameTeam(t, $"Team {t + 1}", members, null, null));
        }
        return teams;
    }

    private static void WriteGen1(SAV1 s, int team, int slot, HallOfFameMemberEdit edit)
    {
        var fame = s.HallOfFame;
        slot = Contiguous(slot, i => IsEmpty1(fame.GetEntity(team, i)));
        var e = fame.GetEntity(team, slot);
        var wasEmpty = IsEmpty1(e);
        if (wasEmpty)
            e.Clear();
        e.Species = edit.Species;
        if (edit.Level is { } lv)
            e.Level = (byte)lv;
        else if (wasEmpty)
            e.Level = 50;
        if (edit.Nickname is { } nick)
            e.Nickname = nick;
        else if (wasEmpty)
            e.Nickname = DefaultName(s, edit.Species, 1).ToUpperInvariant();
    }

    /// <summary>The game ends a team with 0xFF; a save that never reached the Hall of Fame holds zeros.</summary>
    private static bool IsEmpty1(HallOfFameEntity1 e) => e.IsEmpty || e.SpeciesInternal == 0;

    /// <summary>Gen 1/3 teams end at the first empty member: a write past it lands on that first gap.</summary>
    private static int Contiguous(int slot, Func<int, bool> isEmpty)
    {
        for (var i = 0; i < slot; i++)
            if (isEmpty(i))
                return i;
        return slot;
    }

    // ── Gen 3 ──

    private static List<HallOfFameTeam> ReadGen3(SAV3 s)
    {
        var teams = new List<HallOfFameTeam>();
        if (!s.IsFullSaveFile)
            return teams;
        var entries = HallFame3Entry.GetEntries(s);
        for (var t = 0; t < entries.Length; t++)
        {
            var members = new List<HallOfFameMember>();
            for (var i = 0; i < Members; i++)
            {
                var m = entries[t].GetMember(i);
                if (m.Species == 0)
                    break;
                members.Add(Member(i, m.Species, m.Level, m.Nickname, tid: m.TID16, sid: m.SID16,
                    pid: m.PID, shiny: m.IsShiny, form: m.DisplayForm(s.Version)));
            }
            if (members.Count != 0)
                teams.Add(new HallOfFameTeam(t, $"Team {t + 1}", members, null, null));
        }
        return teams;
    }

    private static void WriteGen3(SAV3 s, int team, int slot, HallOfFameMemberEdit edit)
    {
        var entries = HallFame3Entry.GetEntries(s);
        var entry = entries[team];
        slot = Contiguous(slot, i => entry.GetMember(i).Species == 0);
        var m = entry.GetMember(slot);
        var wasEmpty = m.Species == 0;
        m.Species = edit.Species;
        m.Level = edit.Level ?? (wasEmpty ? 50 : m.Level);
        if (edit.Nickname is { } nick)
            m.Nickname = nick;
        else if (wasEmpty)
            m.Nickname = DefaultName(s, edit.Species, 3).ToUpperInvariant();
        m.TID16 = edit.TrainerId ?? (wasEmpty ? s.TID16 : m.TID16);
        m.SID16 = edit.SecretId ?? (wasEmpty ? s.SID16 : m.SID16);
        if (edit.Pid is { } pid)
            m.PID = pid;
        HallFame3Entry.SetEntries(s, entries);
    }

    // ── Gen 6 ──

    private static HallOfFame6 Fame6(SAV6 s) => s switch
    {
        SAV6XY xy => xy.Blocks.HallOfFame,
        SAV6AO ao => ao.Blocks.HallOfFame,
        _ => throw Unsupported(),
    };

    private static List<HallOfFameTeam> ReadGen6(SAV6 s)
    {
        var fame = Fame6(s);
        var teams = new List<HallOfFameTeam>();
        for (var t = 0; t < HallOfFame6.Entries; t++)
        {
            var span = fame.GetEntry(t);
            var index = new HallFame6Index(span[^HallFame6Index.SIZE..]);
            var members = new List<HallOfFameMember>();
            for (var i = 0; i < Members; i++)
            {
                var e = new HallFame6Entity(fame.GetEntity(t, i), s.Language);
                if (e.Species == 0)
                    continue;
                members.Add(Member(i, e.Species, (int)e.Level, e.Nickname, e.IsNicknamed, e.OriginalTrainerName,
                    (int)e.OriginalTrainerGender, e.TID16, e.SID16, e.EncryptionConstant, e.Form, (int)e.Gender,
                    e.IsShiny, e.HeldItem, [e.Move1, e.Move2, e.Move3, e.Move4]));
            }
            if (!index.HasData && members.Count == 0)
                continue;
            DateOnly? date = index.Month is >= 1 and <= 12 && index.Day >= 1
                && index.Day <= DateTime.DaysInMonth((int)index.Year + 2000, (int)index.Month)
                ? new DateOnly((int)index.Year + 2000, (int)index.Month, (int)index.Day)
                : null;
            var label = t == 0 ? "First clear" : $"Clear #{index.ClearIndex}";
            teams.Add(new HallOfFameTeam(t, label, members, date, (int)index.ClearIndex));
        }
        return teams;
    }

    private static void WriteIndex6(HallOfFame6 fame, int team, DateOnly date, uint clear)
    {
        var span = fame.GetEntry(team);
        _ = new HallFame6Index(span[^HallFame6Index.SIZE..])
        {
            ClearIndex = clear,
            Year = (uint)(date.Year - 2000),
            Month = (uint)date.Month,
            Day = (uint)date.Day,
            HasData = true,
        };
    }

    private static HallOfFameMemberEdit Card(PKM pk) => new(pk.Species, pk.CurrentLevel,
        pk.IsNicknamed ? pk.Nickname : null, pk.OriginalTrainerName, pk.OriginalTrainerGender,
        pk.TID16, pk.SID16, pk.EncryptionConstant, pk.Form, pk.Gender, pk.IsShiny, pk.HeldItem,
        [pk.Move1, pk.Move2, pk.Move3, pk.Move4]);

    private static void WriteGen6(SAV6 s, int team, int slot, HallOfFameMemberEdit edit)
    {
        var fame = Fame6(s);
        WriteEntity6(s, fame.GetEntity(team, slot), edit);
        var index = new HallFame6Index(fame.GetEntry(team)[^HallFame6Index.SIZE..]);
        if (!index.HasData)
            WriteIndex6(fame, team, DateOnly.FromDateTime(DateTime.Today), (uint)team);
    }

    private static void WriteEntity6(SAV6 s, Span<byte> slice, HallOfFameMemberEdit edit)
    {
        var e = new HallFame6Entity(slice, s.Language);
        var wasEmpty = e.Species == 0;
        if (wasEmpty)
        {
            slice.Clear();
            e.Level = 50;
            e.TID16 = s.TID16;
            e.SID16 = s.SID16;
            e.OriginalTrainerName = s.OT;
            e.OriginalTrainerGender = s.Gender;
        }
        e.Species = edit.Species;
        if (edit.Level is { } lv) e.Level = (uint)lv;
        if (edit.HeldItem is { } item) e.HeldItem = (ushort)item;
        if (edit.Moves is { } moves)
        {
            e.Move1 = moves.ElementAtOrDefault(0);
            e.Move2 = moves.ElementAtOrDefault(1);
            e.Move3 = moves.ElementAtOrDefault(2);
            e.Move4 = moves.ElementAtOrDefault(3);
        }
        if (edit.Pid is { } ec) e.EncryptionConstant = ec;
        if (edit.TrainerId is { } tid) e.TID16 = tid;
        if (edit.SecretId is { } sid) e.SID16 = sid;
        if (edit.Form is { } form) e.Form = (byte)form;
        if (edit.Gender is { } gender) e.Gender = (uint)gender & 3;
        if (edit.IsShiny is { } shiny) e.IsShiny = shiny;
        if (edit.OriginalTrainer is { } ot) e.OriginalTrainerName = ot;
        if (edit.OriginalTrainerGender is { } otg) e.OriginalTrainerGender = (uint)otg & 1;
        if (edit.Nickname is { } nick)
        {
            e.Nickname = nick;
            e.IsNicknamed = true;
        }
        else
        {
            e.Nickname = DefaultName(s, edit.Species, 6);
            e.IsNicknamed = false;
        }
    }

    // ── Gen 7 ──

    private static List<HallOfFameTeam> ReadGen7(SAV7 s)
    {
        var fame = s.EventWork.Fame;
        var teams = new List<HallOfFameTeam>();
        string[] labels = ["First clear", "Latest"];
        for (var t = 0; t < 2; t++)
        {
            var members = new List<HallOfFameMember>();
            for (var i = 0; i < Members; i++)
            {
                var species = fame.GetEntry(t * Members + i);
                if (species != 0)
                    members.Add(Member(i, species));
            }
            if (members.Count != 0)
                teams.Add(new HallOfFameTeam(t, labels[t], members, null, null));
        }
        return teams;
    }
}
