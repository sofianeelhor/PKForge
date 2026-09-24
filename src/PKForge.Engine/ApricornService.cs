using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One Apricorn Box pocket.</summary>
/// <param name="Index">Storage index 0-6 (SAV4HGSS.GetApricornCount).</param>
/// <param name="ItemId">The Apricorn's item id, for the name and the icon.</param>
public sealed record ApricornCount(int Index, ushort ItemId, string Name, int Count, string BallMade);

/// <summary>
/// HeartGold/SoulSilver Apricorn Box, after PKHeX's WinForms <c>SAV_Apricorn</c>: seven counts
/// at General 0xE558 (<c>SAV4HGSS.GetApricornCount/SetApricornCount</c>), stored in the order
/// Red, Yellow, Blue, Green, Pink, White, Black - item ids 485 + {0,2,1,3,4,5,6}
/// (SAV_Apricorn.ItemNameOffset, "out of order"). PKHeX writes up to 255; its "Give all"
/// button writes 99, the in-game cap, which is what we allow.
/// </summary>
public static class ApricornService
{
    public const int Count = 7;

    /// <summary>SAV_Apricorn.B_All_Click writes 99 per Apricorn.</summary>
    public const int MaxCount = 99;

    private const ushort ItemNameBase = 485; // Red Apricorn
    private static readonly byte[] ItemNameOffset = [0, 2, 1, 3, 4, 5, 6];

    // Kurt's Poké Ball for each Apricorn, in storage order (Red→Level, Yellow→Moon, Blue→Lure,
    // Green→Friend, Pink→Love, White→Fast, Black→Heavy), the same pairing as Gold/Silver/Crystal.
    private static readonly string[] Balls = ["Level Ball", "Moon Ball", "Lure Ball", "Friend Ball", "Love Ball", "Fast Ball", "Heavy Ball"];

    public static bool IsSupported(ISaveEngineSession session) => TryGetSave(session) is not null;

    public static IReadOnlyList<ApricornCount> GetApricorns(ISaveEngineSession session)
    {
        var save = Require(session);
        var names = session.GetItemNames();
        return Enumerable.Range(0, Count).Select(i =>
        {
            var item = (ushort)(ItemNameBase + ItemNameOffset[i]);
            var name = item < names.Count && names[item].Length != 0 ? names[item] : $"Apricorn {i + 1}";
            return new ApricornCount(i, item, name, save.GetApricornCount(i), Balls[i]);
        }).ToArray();
    }

    public static IReadOnlyList<ApricornCount> SetCount(ISaveEngineSession session, int index, int count)
    {
        var save = Require(session);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
        save.SetApricornCount(index, Math.Clamp(count, 0, MaxCount));
        return GetApricorns(session);
    }

    /// <summary>SAV_Apricorn "All" (99 each) or "None" (0 each).</summary>
    public static IReadOnlyList<ApricornCount> SetAll(ISaveEngineSession session, int count)
    {
        var save = Require(session);
        for (var i = 0; i < Count; i++)
            save.SetApricornCount(i, Math.Clamp(count, 0, MaxCount));
        return GetApricorns(session);
    }

    private static SAV4HGSS? TryGetSave(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile as SAV4HGSS : null;

    private static SAV4HGSS Require(ISaveEngineSession session) =>
        TryGetSave(session) ?? throw new NotSupportedException("The Apricorn Box exists only in HeartGold and SoulSilver.");
}
