using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine.OriginHeartGold;

/// <summary>
/// Names and species facts for Origin HeartGold. The hack publishes no tables, but its
/// records carry modern national ids (see <see cref="OriginHeartGoldFormat"/>), so names
/// come from PKHeX's current English lists. An id past those lists is shown as "Move #N"
/// rather than guessed. Growth rates and types come from the latest main-series data for
/// that species (Ultra Sun/Moon through 807, Scarlet/Violet after): the sample's six party
/// Pokémon store exactly the level their EXP gives under those growth rates.
/// </summary>
internal static class OriginHeartGoldData
{
    /// <summary>Species ids a record may name: PKHeX's national list (Pecharunt, 1025).</summary>
    public static int MaxStoredSpecies => Strings.specieslist.Length - 1;

    private static GameStrings Strings => GameInfo.GetStrings("en");

    public static string SpeciesName(int species) => Named(Strings.specieslist, species, "Species");
    public static string MoveName(int move) => move == 0 ? string.Empty : Named(Strings.movelist, move, "Move");
    public static string ItemName(int item) => item == 0 ? string.Empty : Named(Strings.itemlist, item, "Item");
    public static string AbilityName(int ability) => ability == 0 ? string.Empty : Named(Strings.abilitylist, ability, "Ability");

    private static string Named(string[] list, int id, string kind) =>
        id > 0 && id < list.Length && list[id].Length > 0 ? list[id] : $"{kind} #{id}";

    /// <summary>Every item id with its name (unnamed ids as "Item #N"), for the item picker.</summary>
    public static IReadOnlyList<string> ItemNames()
    {
        var list = Strings.itemlist;
        var names = new string[list.Length];
        for (var id = 0; id < list.Length; id++)
            names[id] = id == 0 ? "None" : ItemName(id);
        return names;
    }

    private static IPersonalInfo? Personal(int species, int form)
    {
        if (species <= 0 || species > MaxStoredSpecies)
            return null;
        return species <= PersonalTable.USUM.MaxSpeciesID
            ? PersonalTable.USUM.GetFormEntry((ushort)species, (byte)form)
            : PersonalTable.SV.GetFormEntry((ushort)species, (byte)form);
    }

    /// <summary>The level an EXP total gives, or 0 when the species has no known growth rate.</summary>
    public static int LevelFor(int species, uint experience) =>
        Personal(species, 0) is { } info ? PKHeX.Core.Experience.GetLevel(experience, info.EXPGrowth) : 0;

    public static IReadOnlyList<int> Types(int species, int form) =>
        Personal(species, form) is { } info
            ? info.Type1 == info.Type2 ? [info.Type1] : [info.Type1, info.Type2]
            : [0];

    public static BaseStats BaseStatsOf(int species) =>
        Personal(species, 0) is { } info
            ? new BaseStats(info.HP, info.ATK, info.DEF, info.SPA, info.SPD, info.SPE)
            : new BaseStats(0, 0, 0, 0, 0, 0);

    /// <summary>The abilities the latest games give this species (for the read-only ability picker).</summary>
    public static IReadOnlyList<int> Abilities(int species, int form)
    {
        if (Personal(species, form) is not PersonalInfo info)
            return [];
        var abilities = new List<int>();
        for (var i = 0; i < info.AbilityCount; i++)
        {
            var ability = info.GetAbilityAtIndex(i);
            if (ability != 0 && !abilities.Contains(ability))
                abilities.Add(ability);
        }
        return abilities;
    }
}
