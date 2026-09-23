using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One of Sinnoh's 21 honey trees as stored in a DPPt save.</summary>
/// <param name="Index">Tree index 0-20, the save's storage order.</param>
/// <param name="Location">Where the tree stands, in PKHeX's order.</param>
/// <param name="Time">Honey timer in minutes (0-1440); a slathered tree is catchable at 1080 or below.</param>
/// <param name="Group">Encounter group: 0 none, 1 common, 2 rare, 3 Munchlax.</param>
/// <param name="Slot">Slot 0-5 within the group.</param>
/// <param name="Shakes">How many times the tree shakes (0-3).</param>
/// <param name="Species">National species the group/slot resolves to; 0 when the group is empty.</param>
/// <param name="SpeciesName">Display name for <paramref name="Species"/>.</param>
/// <param name="IsMunchlaxTree">True when this is one of the trainer's Munchlax trees.</param>
public sealed record HoneyTree(int Index, string Location, uint Time, int Group, int Slot, int Shakes,
    ushort Species, string SpeciesName, bool IsMunchlaxTree)
{
    /// <summary>A tree holding a Pokémon whose timer is inside the catchable window.</summary>
    public bool IsReady => Group != HoneyTreeService.GroupNone && Time is > 0 and <= HoneyTreeService.CatchableTime;
}

/// <summary>
/// Sinnoh honey trees for Diamond/Pearl/Platinum, mirroring PKHeX exactly:
/// storage from <c>SAV4Sinnoh.GetHoneyTree/SetHoneyTree</c> (8-byte <c>HoneyTreeValue</c>),
/// the Munchlax trees from <c>HoneyTreeUtil.CalculateMunchlaxTrees(SAV.ID32)</c>, and the
/// edit semantics from the WinForms <c>SAV_HoneyTree</c> form (Time 0-1440, Group 0-3,
/// Slot 0-5, Shake 0-3, the "Catchable" button writing Time = 1080).
/// </summary>
public static class HoneyTreeService
{
    public const int TreeCount = 21;
    public const int GroupNone = (int)HoneyTreeSlotGroup.None;
    public const int GroupMunchlax = (int)HoneyTreeSlotGroup.Munchlax;
    public const int SlotCount = 6;
    public const int MaxShakes = 3;
    public const uint MaxTime = 1440;

    /// <summary>SAV_HoneyTree.B_Catchable_Click: <c>NUD_Time.Value = 1080</c>.</summary>
    public const uint CatchableTime = 1080;

    /// <summary>Munchlax is overwhelmingly a 3-shake result: HoneyTreeUtil.GetShakeCount gives 93% for 3.</summary>
    public const int MunchlaxShakes = 3;

    /// <summary>Tree names verbatim from SAV_HoneyTree.Designer.cs (CB_TreeList), index = save order.</summary>
    public static readonly IReadOnlyList<string> Locations =
    [
        "Route 205, Floaroma Town side", "Route 205, Eterna City side", "Route 206", "Route 207",
        "Route 208", "Route 209", "Route 210, Solaceon Town side", "Route 210, Celestic Town side",
        "Route 211", "Route 212, Hearthome City side", "Route 212, Pastoria City side", "Route 213",
        "Route 214", "Route 215", "Route 218", "Route 221", "Route 222", "Valley Windworks",
        "Eterna Forest", "Fuego Ironworks", "Floaroma Meadow",
    ];

    public static readonly IReadOnlyList<string> GroupNames = ["Empty", "Common", "Rare", "Munchlax"];

    /// <summary>True for Diamond, Pearl and Platinum saves (PKHeX shows the editor for <c>SAV4Sinnoh</c>).</summary>
    public static bool IsSupported(ISaveEngineSession session) => TryGetSave(session) is not null;

    /// <summary>
    /// The distinct Munchlax tree indices for this trainer. PKHeX's algorithm yields 3-4
    /// unique trees (its overlap fix can still collide, see HoneyTreeUtil.AdjustOverlap).
    /// </summary>
    public static IReadOnlyList<int> GetMunchlaxTrees(ISaveEngineSession session) =>
        GetMunchlaxTrees(RequireSave(session).ID32);

    /// <summary>Pure form of the calculation, keyed on the 32-bit trainer ID (SID16 &lt;&lt; 16 | TID16).</summary>
    public static IReadOnlyList<int> GetMunchlaxTrees(uint id32)
    {
        Span<byte> trees = stackalloc byte[4];
        HoneyTreeUtil.CalculateMunchlaxTrees(id32, trees);
        var result = new List<int>(4);
        foreach (var tree in trees)
            if (!result.Contains(tree))
                result.Add(tree);
        return result;
    }

    public static IReadOnlyList<HoneyTree> GetTrees(ISaveEngineSession session)
    {
        var save = RequireSave(session);
        var munchlax = GetMunchlaxTrees(save.ID32);
        var trees = new HoneyTree[TreeCount];
        for (var i = 0; i < TreeCount; i++)
        {
            var value = save.GetHoneyTree(i);
            // Out-of-range bytes (corrupt or unused trees) are clamped for the species
            // lookup only, the same way SAV_HoneyTree.ReadTree clamps into its NUD maxima.
            var group = Math.Min(value.Group, GroupMunchlax);
            var slot = Math.Min(value.Slot, SlotCount - 1);
            var species = save.GetHoneyTreeSpecies(group, slot);
            trees[i] = new HoneyTree(i, Locations[i], value.Time, value.Group, value.Slot, value.Shake,
                species, SpeciesLabel(save, species), munchlax.Contains(i));
        }
        return trees;
    }

    /// <summary>Species offered by one group's six slots, for an encounter picker.</summary>
    public static IReadOnlyList<string> GetGroupSpecies(ISaveEngineSession session, int group)
    {
        var save = RequireSave(session);
        ArgumentOutOfRangeException.ThrowIfLessThan(group, GroupNone);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(group, GroupMunchlax);
        return Enumerable.Range(0, SlotCount).Select(slot => SpeciesLabel(save, save.GetHoneyTreeSpecies(group, slot))).ToArray();
    }

    /// <summary>Slathers the tree: the timer becomes PKHeX's "Catchable" value; the encounter is kept.</summary>
    public static HoneyTree Slather(ISaveEngineSession session, int index) =>
        Write(session, index, tree => tree.Time = CatchableTime);

    /// <summary>
    /// Writes a full tree state, clamped to SAV_HoneyTree's input ranges. Setting
    /// <see cref="HoneyTreeValue.Group"/> also rewrites SubTable (Group - 1), as PKHeX does.
    /// </summary>
    public static HoneyTree SetTree(ISaveEngineSession session, int index, uint time, int group, int slot, int shakes) =>
        Write(session, index, tree =>
        {
            tree.Time = Math.Min(time, MaxTime);
            tree.Shake = Math.Clamp(shakes, 0, MaxShakes);
            tree.Group = Math.Clamp(group, GroupNone, GroupMunchlax);
            tree.Slot = Math.Clamp(slot, 0, SlotCount - 1);
        });

    /// <summary>
    /// Makes Munchlax wait in the tree: catchable timer, Munchlax group (every slot of the
    /// Munchlax group is species 446 in both SAV4DP and SAV4Pt TreeSpecies), three shakes.
    /// Only valid on this trainer's Munchlax trees - SAV_HoneyTree warns that catching
    /// Munchlax elsewhere is illegal for the TID16/SID16, so we refuse instead.
    /// </summary>
    public static HoneyTree SetMunchlaxReady(ISaveEngineSession session, int index)
    {
        if (!GetMunchlaxTrees(session).Contains(index))
            throw new InvalidOperationException($"{Locations[index]} is not a Munchlax tree for this trainer; a Munchlax caught there would be illegal.");
        return SetTree(session, index, CatchableTime, GroupMunchlax, 0, MunchlaxShakes);
    }

    private static HoneyTree Write(ISaveEngineSession session, int index, Action<HoneyTreeValue> edit)
    {
        var save = RequireSave(session);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, TreeCount);
        var tree = save.GetHoneyTree(index);
        edit(tree);
        save.SetHoneyTree(tree, index);
        return GetTrees(session)[index];
    }

    /// <summary>SAV_HoneyTree.GetLabelText: DP's Silcoon slot is Cascoon in Pearl.</summary>
    private static string SpeciesLabel(SAV4Sinnoh save, ushort species)
    {
        if (species == 0)
            return "—";
        var name = SpeciesName.GetSpeciesName(species, (int)LanguageID.English);
        return species == (ushort)PKHeX.Core.Species.Silcoon && save is SAV4DP
            ? $"{name} (Diamond) / {SpeciesName.GetSpeciesName((ushort)(species + 2), (int)LanguageID.English)} (Pearl)"
            : name;
    }

    private static SAV4Sinnoh? TryGetSave(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile as SAV4Sinnoh : null;

    private static SAV4Sinnoh RequireSave(ISaveEngineSession session) =>
        TryGetSave(session) ?? throw new NotSupportedException("Honey trees exist only in Diamond, Pearl and Platinum saves.");
}
