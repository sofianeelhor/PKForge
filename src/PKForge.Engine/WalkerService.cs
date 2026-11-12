using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>A Pokémon caught with the Poké Radar, legal and ready for the Bank.</summary>
public sealed record WalkerCatchResult(byte[] Data, string Format, int Species, int Level, string Name);

/// <summary>What coming home did to the Pokémon that walked.</summary>
public sealed record WalkerGrowth(int LevelBefore, int LevelAfter, uint ExpGained, int FriendshipGained);

/// <summary>What a HeartGold/SoulSilver sync did: steps added, watts written, the course it
/// opened in the save (one per sync, as the device), and what it learned from the save.</summary>
public sealed record WalkerSync(uint StepsAdded, uint Watts, int? CourseOpened, IReadOnlyList<int> SaveCourses, bool NationalDex);

/// <summary>
/// The PKForge Pokéwalker against real Pokémon data. Radar catches are PKHeX's own Pokéwalker
/// encounters (<see cref="EncounterStatic4Pokewalker.ConvertToPKM(ITrainerInfo, EncounterCriteria)"/>:
/// met at the Pokéwalker, Poké Ball, the walker's PID correlation, the course's level, moves and
/// gender), each checked with <see cref="LegalityAnalysis"/>. Coming home adds one EXP a step, never
/// more than one level (the device's rule), and friendship; a change that would make a legal
/// Pokémon illegal is refused. HeartGold/SoulSilver sync goes through <see cref="PokewalkerService"/>.
/// </summary>
public static class WalkerService
{
    private static readonly Lazy<EncounterStatic4Pokewalker[]> Table =
        new(() => EncounterStatic4Pokewalker.GetAll(Util.GetBinaryResource("encounter_walker4.pkl")));

    /// <summary>PKHeX's six slots of a course, in group order A, A, B, B, C, C.</summary>
    public static IReadOnlyList<EncounterStatic4Pokewalker> Slots(int course) =>
        [.. Table.Value.Where(s => (int)s.Course == course)];

    public static string SlotName(int course, int slot) =>
        Slots(course) is { Count: > 0 } slots && (uint)slot < (uint)slots.Count
            ? SpeciesName.GetSpeciesName(slots[slot].Species, (int)LanguageID.English) : "?";

    public static int SlotLevel(int course, int slot) => Slots(course) is { } slots && (uint)slot < (uint)slots.Count ? slots[slot].Level : 0;

    /// <summary>The trainer catches are made for: the open HeartGold/SoulSilver save's trainer, else
    /// the walker's own when it is a Gen 4 Pokémon, else a plain PKForge trainer.</summary>
    public static ITrainerInfo TrainerFor(ISaveEngineSession? session, byte[]? walker, string? format)
    {
        if (session is SaveEngineSession { SaveFile: SAV4HGSS save })
            return new SimpleTrainerInfo(save.Version is GameVersion.HG or GameVersion.SS ? save.Version : GameVersion.HG)
            {
                OT = save.OT, TID16 = save.TID16, SID16 = save.SID16, Gender = save.Gender,
                Language = save.Language == 0 ? (int)LanguageID.English : save.Language,
            };
        if (walker is not null && EntityBytes.Parse(walker, format) is PK4 pk && pk.OriginalTrainerName.Length > 0)
            return new SimpleTrainerInfo(pk.Version is GameVersion.HG or GameVersion.SS ? pk.Version : GameVersion.HG)
            {
                OT = pk.OriginalTrainerName, TID16 = pk.TID16, SID16 = pk.SID16, Gender = pk.OriginalTrainerGender,
                Language = pk.Language > 0 ? pk.Language : (int)LanguageID.English,
            };
        return new SimpleTrainerInfo(GameVersion.HG) { OT = "PKForge", TID16 = 31415, SID16 = 27182, Language = (int)LanguageID.English };
    }

    /// <summary>A legal Pokéwalker catch of a course slot. Null (with the reason) when PKHeX finds
    /// no legal one, which would be a bug in the table, never a reason to write an illegal one.</summary>
    public static WalkerCatchResult? Catch(int course, int slot, ITrainerInfo trainer, out string why)
    {
        why = "";
        var slots = Slots(course);
        if ((uint)slot >= (uint)slots.Count) { why = "That course has no such Pokémon."; return null; }
        var encounter = slots[slot];
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var pk = encounter.ConvertToPKM(trainer, EncounterCriteria.Unrestricted);
            pk.RefreshChecksum();
            var legality = new LegalityAnalysis(pk);
            if (!legality.Valid) { why = legality.Report(); continue; }
            var data = new byte[pk.SIZE_PARTY];
            pk.WriteDecryptedDataParty(data);
            return new WalkerCatchResult(data, EntityBytes.FormatOf(pk), pk.Species, pk.CurrentLevel,
                SpeciesName.GetSpeciesName(pk.Species, (int)LanguageID.English));
        }
        return null;
    }

    /// <summary>What coming home would do, without writing: EXP up to the next level only, and
    /// friendship. Eggs and level 100 Pokémon gain no EXP.</summary>
    public static WalkerGrowth Preview(PKM pk, WalkerHomecoming home)
    {
        if (pk.IsEgg) return new(pk.CurrentLevel, pk.CurrentLevel, 0, 0);
        var level = pk.CurrentLevel;
        var growth = pk.PersonalInfo.EXPGrowth;
        uint gained = 0;
        if (level < Experience.MaxLevel)
        {
            var next = Experience.GetEXP((byte)(level + 1), growth);
            var after = Math.Min(next, pk.EXP + home.Exp);
            gained = after > pk.EXP ? after - pk.EXP : 0;
        }
        var friendship = pk.Format <= 1 ? 0 : Math.Min(home.Friendship, 255 - pk.CurrentFriendship);
        return new(level, Experience.GetLevel(pk.EXP + gained, growth), gained, Math.Max(0, friendship));
    }

    /// <summary>Applies the homecoming to a Pokémon. Null (with the reason) when nothing changes or
    /// when the change would turn a legal Pokémon illegal.</summary>
    public static (PKM Pokemon, WalkerGrowth Growth)? ComeHome(PKM pk, WalkerHomecoming home, out string why)
    {
        why = "";
        var growth = Preview(pk, home);
        if (growth.ExpGained == 0 && growth.FriendshipGained == 0) { why = "It is already as strong and as close to you as a walk can make it."; return null; }
        var wasLegal = new LegalityAnalysis(pk).Valid;
        var copy = pk.Clone();
        if (growth.ExpGained > 0)
        {
            copy.EXP += growth.ExpGained;
            copy.Stat_Level = (byte)growth.LevelAfter;
            copy.ResetPartyStats();
        }
        if (growth.FriendshipGained > 0) copy.CurrentFriendship = (byte)(copy.CurrentFriendship + growth.FriendshipGained);
        copy.RefreshChecksum();
        if (wasLegal && new LegalityAnalysis(copy) is { Valid: false } after)
        {
            why = "The walk would make it fail legality, so it comes back unchanged: " + after.Report();
            return null;
        }
        return (copy, growth);
    }

    /// <summary>The homecoming on a Bank Pokémon's bytes.</summary>
    public static (byte[] Data, WalkerGrowth Growth)? ComeHome(byte[] data, string? format, WalkerHomecoming home, out string why)
    {
        why = "It could not be read.";
        if (EntityBytes.Parse(data, format) is not { } pk) return null;
        if (ComeHome(pk, home, out why) is not { } done) return null;
        var bytes = new byte[done.Pokemon.SIZE_PARTY];
        done.Pokemon.WriteDecryptedDataParty(bytes);
        return (bytes, done.Growth);
    }

    /// <summary>The homecoming on a save slot (box -1 is the party), surgically.</summary>
    public static WalkerGrowth? ComeHomeSlot(ISaveEngineSession session, int box, int slot, WalkerHomecoming home, out string why)
    {
        if (session is not SaveEngineSession engine) throw new NotSupportedException("The Pokéwalker can only write to PKHeX-backed saves.");
        var pk = engine.GetEntity(box, slot);
        if (pk.Species == 0) { why = "That slot is empty now."; return null; }
        if (ComeHome(pk, home, out why) is not { } done) return null;
        if (box == -1) engine.SaveFile.SetPartySlotAtIndex(done.Pokemon, slot, EntityImportSettings.None);
        else engine.SaveFile.SetBoxSlotAtIndex(done.Pokemon, box, slot, EntityImportSettings.None);
        return done.Growth;
    }

    private static readonly int MaxItemId = new PK4().MaxItemID;

    /// <summary>The HeartGold/SoulSilver item id of a Dowsing find (null when the name is unknown).</summary>
    public static int? ItemId(string name)
    {
        var key = ItemBankKeys.NameKey(name);
        // Today's names first ("Energy Powder"), then the Gen 4 spellings ("EnergyPowder"); the ids are shared.
        var strings = GameInfo.GetStrings("en");
        foreach (var items in new[] { strings.itemlist, strings.GetItemStrings(EntityContext.Gen4, GameVersion.HGSS) })
            for (var id = 1; id <= Math.Min(MaxItemId, items.Length - 1); id++)
                if (ItemBankKeys.NameKey(items[id]) == key) return id;
        return null;
    }

    /// <summary>The Item Bank key and name a find is banked under (machines keyed to the Gen 4 family).</summary>
    public static (string Key, string Name)? BankKey(string name)
    {
        if (ItemId(name) is not { } id) return null;
        var items = GameInfo.GetStrings("en").GetItemStrings(EntityContext.Gen4, GameVersion.HGSS);
        var shown = items[id];
        return ItemBankKeys.IsMachineName(shown)
            ? (ItemBankKeys.Machine(shown, EntityContext.Gen4.ToString()), shown)
            : (ItemBankKeys.Item(id), shown);
    }

    /// <summary>
    /// Syncs with a HeartGold/SoulSilver save: adds <paramref name="steps"/> to its Pokéwalker steps,
    /// writes PKForge's watts, and opens one course PKForge has open (the device opens one a sync).
    /// Returns what the save has, for PKForge to learn (its courses, events included, and the
    /// National Dex).
    /// </summary>
    public static WalkerSync Sync(ISaveEngineSession session, WalkerState state)
    {
        var before = PokewalkerService.GetState(session);
        var steps = (uint)Math.Min(PokewalkerService.MaxCounter - Math.Min(before.Steps, PokewalkerService.MaxCounter), state.UnsyncedSteps);
        var watts = (uint)Math.Min(PokewalkerService.MaxCounter, state.Watts);
        PokewalkerService.SetCounters(session, before.Steps + steps, watts);
        var open = WalkerRules.CourseToSend(state, i => before.Courses[i].Unlocked, i => before.Courses[i].AvailableForLanguage);
        if (open is { } course) PokewalkerService.SetCourse(session, course, true);
        var after = PokewalkerService.GetState(session);
        var dex = session is SaveEngineSession { SaveFile: SAV4 sav } && sav.NationalDex;
        return new WalkerSync(steps, watts, open, [.. after.Courses.Where(c => c.Unlocked).Select(c => c.Index)], dex);
    }
}
