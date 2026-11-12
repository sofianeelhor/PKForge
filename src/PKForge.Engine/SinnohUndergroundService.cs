using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>The four 40-slot Underground pouches of Diamond/Pearl/Platinum.</summary>
public enum UndergroundPocket { Goods, Traps, Treasures, Spheres }

/// <summary>One occupied pouch slot. Count is the sphere size; 1 for goods, traps and treasures.</summary>
public sealed record UndergroundSlot(int Slot, int Id, string Name, int Count);

/// <summary>
/// The Diamond/Pearl/Platinum Underground, mirroring PKHeX's SAV_Underground: the goods,
/// traps, treasures and spheres pouches (SAV4Sinnoh.GetUGI_*, 40 slots each, written
/// packed with empty slots at the end) and the thirteen Underground records, flags
/// included (UG_*, clamped to UG_MAX). Brilliant Diamond/Shining Pearl's Grand
/// Underground is the separate item store in <see cref="ISaveEngineSession.GetGrandUndergroundItems"/>.
/// </summary>
public static class SinnohUndergroundService
{
    public const int PocketSize = SAV4Sinnoh.UG_POUCH_SIZE;

    /// <summary>Largest sphere size offered; the games grow spheres up to 99.</summary>
    public const int MaxSphereSize = 99;

    /// <summary>SAV_Underground's record labels, in its order.</summary>
    public static readonly IReadOnlyList<string> RecordNames =
    [
        "Players met", "Gifts given", "Gifts received", "Spheres obtained", "Fossils dug up",
        "Players trapped", "Times trapped", "Base moved", "Flags obtained", "My flag taken",
        "My flag recovered", "Flags captured", "Helped others",
    ];

    public static bool IsSupported(ISaveEngineSession session) => TryGetSave(session) is not null;

    /// <summary>Every item the pouch can hold, by id (the empty "(None)" entry and blank ids left out).</summary>
    public static IReadOnlyList<(int Id, string Name)> GetChoices(UndergroundPocket pocket) =>
        [.. Names(pocket).Select((name, id) => (id, name)).Where(x => x.id != 0 && !string.IsNullOrWhiteSpace(x.name))];

    public static IReadOnlyList<UndergroundSlot> GetPocket(ISaveEngineSession session, UndergroundPocket pocket)
    {
        var save = RequireSave(session);
        var names = Names(pocket);
        var ids = Ids(save, pocket);
        var result = new List<UndergroundSlot>();
        for (var i = 0; i < PocketSize; i++)
        {
            var id = ids[i];
            if (id == 0 || id >= names.Length) continue;
            var count = pocket == UndergroundPocket.Spheres ? Sizes(save)[i] : 1;
            result.Add(new UndergroundSlot(i, id, names[id], count));
        }
        return result;
    }

    /// <summary>Adds an item to the first free slot. False when the pouch is full.</summary>
    public static bool Add(ISaveEngineSession session, UndergroundPocket pocket, int id, int sphereSize = MaxSphereSize)
    {
        var save = RequireSave(session);
        if (!GetChoices(pocket).Any(choice => choice.Id == id))
            throw new ArgumentOutOfRangeException(nameof(id), "Not an item of this pouch.");
        var entries = Read(save, pocket);
        if (entries.Count >= PocketSize) return false;
        entries.Add((id, Math.Clamp(sphereSize, 1, MaxSphereSize)));
        Write(save, pocket, entries);
        return true;
    }

    /// <summary>Removes one slot; the pouch is rewritten packed, as SAV_Underground saves it.</summary>
    public static void Remove(ISaveEngineSession session, UndergroundPocket pocket, int slot)
    {
        var save = RequireSave(session);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)slot, (uint)PocketSize);
        var ids = Ids(save, pocket);
        if (ids[slot] == 0) return;
        ids[slot] = 0;
        Write(save, pocket, Read(save, pocket));
    }

    /// <summary>Resizes one sphere, clamped to 1-99. Returns the stored size.</summary>
    public static int SetSphereSize(ISaveEngineSession session, int slot, int size)
    {
        var save = RequireSave(session);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)slot, (uint)PocketSize);
        if (Ids(save, UndergroundPocket.Spheres)[slot] == 0)
            throw new ArgumentOutOfRangeException(nameof(slot), "That sphere slot is empty.");
        Sizes(save)[slot] = (byte)Math.Clamp(size, 1, MaxSphereSize);
        return Sizes(save)[slot];
    }

    /// <summary>
    /// Fills the free slots. Goods, traps and treasures get one of each item not held yet, in id
    /// order; spheres get large spheres of every colour in turn at size 99. Returns how many were added.
    /// </summary>
    public static int Fill(ISaveEngineSession session, UndergroundPocket pocket)
    {
        var save = RequireSave(session);
        var entries = Read(save, pocket);
        var before = entries.Count;
        if (pocket == UndergroundPocket.Spheres)
        {
            var large = GetChoices(pocket).Where(c => c.Name.EndsWith("(L)", StringComparison.Ordinal)).Select(c => c.Id).ToArray();
            for (var i = 0; entries.Count < PocketSize && large.Length != 0; i++)
                entries.Add((large[i % large.Length], MaxSphereSize));
        }
        else
        {
            foreach (var (id, _) in GetChoices(pocket))
            {
                if (entries.Count >= PocketSize) break;
                if (entries.All(e => e.Id != id))
                    entries.Add((id, 1));
            }
        }
        Write(save, pocket, entries);
        return entries.Count - before;
    }

    public static void Clear(ISaveEngineSession session, UndergroundPocket pocket) =>
        Write(RequireSave(session), pocket, []);

    /// <summary>The thirteen Underground records, each 0-999,999.</summary>
    public static IReadOnlyList<CountedEntry> GetRecords(ISaveEngineSession session)
    {
        var save = RequireSave(session);
        uint[] values =
        [
            save.UG_PeopleMet, save.UG_GiftsGiven, save.UG_GiftsReceived, save.UG_Spheres, save.UG_Fossils,
            save.UG_TrapPlayers, save.UG_TrapSelf, save.UG_MyBaseMoved, save.UG_FlagsTaken, save.UG_FlagsFromMe,
            save.UG_FlagsRecovered, save.UG_FlagsCaptured, save.UG_HelpedOthers,
        ];
        return [.. values.Select((v, i) => new CountedEntry(i, RecordNames[i], (int)Math.Min(v, SAV4Sinnoh.UG_MAX), (int)SAV4Sinnoh.UG_MAX))];
    }

    /// <summary>Sets one record, clamped to 0-999,999. Returns the stored value.</summary>
    public static int SetRecord(ISaveEngineSession session, int index, int value)
    {
        var save = RequireSave(session);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)index, (uint)RecordNames.Count);
        var v = (uint)Math.Clamp(value, 0, (int)SAV4Sinnoh.UG_MAX);
        switch (index)
        {
            case 0: save.UG_PeopleMet = v; break;
            case 1: save.UG_GiftsGiven = v; break;
            case 2: save.UG_GiftsReceived = v; break;
            case 3: save.UG_Spheres = v; break;
            case 4: save.UG_Fossils = v; break;
            case 5: save.UG_TrapPlayers = v; break;
            case 6: save.UG_TrapSelf = v; break;
            case 7: save.UG_MyBaseMoved = v; break;
            case 8: save.UG_FlagsTaken = v; break;
            case 9: save.UG_FlagsFromMe = v; break;
            case 10: save.UG_FlagsRecovered = v; break;
            case 11: save.UG_FlagsCaptured = v; break;
            default: save.UG_HelpedOthers = v; break;
        }
        return (int)v;
    }

    private static List<(int Id, int Count)> Read(SAV4Sinnoh save, UndergroundPocket pocket)
    {
        var limit = Names(pocket).Length;
        var ids = Ids(save, pocket);
        var result = new List<(int, int)>();
        for (var i = 0; i < PocketSize; i++)
        {
            if (ids[i] == 0 || ids[i] >= limit) continue;
            result.Add((ids[i], pocket == UndergroundPocket.Spheres ? Sizes(save)[i] : 0));
        }
        return result;
    }

    /// <summary>SAV_Underground.SaveUGData: clear the pouch, then write the entries packed from slot 0.</summary>
    private static void Write(SAV4Sinnoh save, UndergroundPocket pocket, IReadOnlyList<(int Id, int Count)> entries)
    {
        var ids = Ids(save, pocket);
        ids.Clear();
        if (pocket == UndergroundPocket.Spheres) Sizes(save).Clear();
        for (var i = 0; i < entries.Count && i < PocketSize; i++)
        {
            ids[i] = (byte)entries[i].Id;
            if (pocket == UndergroundPocket.Spheres)
                Sizes(save)[i] = (byte)entries[i].Count;
        }
    }

    private static Span<byte> Ids(SAV4Sinnoh save, UndergroundPocket pocket) => pocket switch
    {
        UndergroundPocket.Goods => save.GetUGI_Goods(),
        UndergroundPocket.Traps => save.GetUGI_Traps(),
        UndergroundPocket.Treasures => save.GetUGI_Treasures(),
        _ => save.GetUGI_Spheres()[..PocketSize],
    };

    private static Span<byte> Sizes(SAV4Sinnoh save) => save.GetUGI_Spheres()[PocketSize..];

    private static string[] Names(UndergroundPocket pocket)
    {
        var strings = GameInfo.GetStrings("en");
        return pocket switch
        {
            UndergroundPocket.Goods => strings.uggoods,
            UndergroundPocket.Traps => strings.ugtraps,
            UndergroundPocket.Treasures => strings.ugtreasures,
            _ => strings.ugspheres,
        };
    }

    private static SAV4Sinnoh? TryGetSave(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile as SAV4Sinnoh : null;

    private static SAV4Sinnoh RequireSave(ISaveEngineSession session) =>
        TryGetSave(session) ?? throw new NotSupportedException("The Underground pouches exist only in Diamond, Pearl and Platinum saves.");
}
