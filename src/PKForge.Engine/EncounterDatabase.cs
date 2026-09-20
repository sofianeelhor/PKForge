using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// The single implementation of "enumerate every legal way this species line can be
/// obtained in this game". Used by both the open-save session (scoped to the game you
/// are editing) and the save-free lookup (which walks one blank save per game), so the
/// two can never drift apart.
/// </summary>
internal static class EncounterDatabase
{
    /// <summary>Every encounter for the species line in one specific game, including
    /// pre-evolutions, de-duplicated for display. Format and context come from the save,
    /// but the game is passed explicitly: a blank Diamond/Pearl save reports the DP
    /// *group*, and the generator only accepts concrete games.</summary>
    public static List<IEncounterable> Enumerate(SaveFile save, int species, int form, GameVersion version)
    {
        if (species <= 0 || species > save.MaxSpeciesID)
            return [];

        var pk = save.BlankPKM.Clone();
        pk.Species = (ushort)species;
        pk.Form = (byte)Math.Clamp(form, 0, byte.MaxValue);
        pk.Version = version;
        EncounterMovesetGenerator.OptimizeCriteria(pk, save);
        var encounters = EncounterMovesetGenerator.GenerateEncounters(pk, save, default, version);
        return encounters
            .DistinctBy(e => (e.Name, e.LongName, ((IEncounterTemplate)e).LevelMin, ((IEncounterTemplate)e).LevelMax,
                e is ILocation l ? l.GetLocation() : 0))
            .ToList();
    }

    /// <summary>The enumeration as display cards, ordered by kind then level then place.</summary>
    public static IReadOnlyList<EncounterCard> Describe(SaveFile save, int species, int form, GameVersion version)
    {
        var cards = Enumerate(save, species, form, version).Select(ToCard).ToList();
        cards.Sort(Compare);
        return cards;
    }

    /// <summary>Gallery order: kind, then level, then location.</summary>
    public static int Compare(EncounterCard a, EncounterCard b)
    {
        var byKind = KindRank(a.Kind).CompareTo(KindRank(b.Kind));
        if (byKind != 0) return byKind;
        var byLevel = a.LevelMin.CompareTo(b.LevelMin);
        if (byLevel != 0) return byLevel;
        return string.CompareOrdinal(a.Location, b.Location);
    }

    /// <summary>Concrete games a stored version stands for. Saved games keep the pair
    /// id (a Diamond file parses as <see cref="GameVersion.DP"/>), while the encounter
    /// generator and every legality rule work in concrete games.</summary>
    public static IReadOnlyList<GameVersion> ConcreteVersions(GameVersion version) => version switch
    {
        GameVersion.RBY => [GameVersion.RD, GameVersion.BU, GameVersion.YW],
        GameVersion.GSC => [GameVersion.GD, GameVersion.SI, GameVersion.C],
        GameVersion.RS => [GameVersion.R, GameVersion.S],
        GameVersion.RSE => [GameVersion.R, GameVersion.S, GameVersion.E],
        GameVersion.FRLG => [GameVersion.FR, GameVersion.LG],
        GameVersion.DP => [GameVersion.D, GameVersion.P],
        GameVersion.DPPt => [GameVersion.D, GameVersion.P, GameVersion.Pt],
        GameVersion.HGSS => [GameVersion.HG, GameVersion.SS],
        GameVersion.BW => [GameVersion.B, GameVersion.W],
        GameVersion.B2W2 => [GameVersion.B2, GameVersion.W2],
        GameVersion.XY => [GameVersion.X, GameVersion.Y],
        GameVersion.ORAS => [GameVersion.OR, GameVersion.AS],
        GameVersion.SM => [GameVersion.SN, GameVersion.MN],
        GameVersion.USUM => [GameVersion.US, GameVersion.UM],
        GameVersion.GG => [GameVersion.GP, GameVersion.GE],
        GameVersion.SWSH => [GameVersion.SW, GameVersion.SH],
        GameVersion.BDSP => [GameVersion.BD, GameVersion.SP],
        GameVersion.SV => [GameVersion.SL, GameVersion.VL],
        _ => [version],
    };

    public static EncounterCard ToCard(IEncounterable enc)
    {
        var template = (IEncounterTemplate)enc;
        var location = template.GetEncounterLocation(template.Generation, template.Version) ?? string.Empty;
        var shiny = template.Shiny;
        return new EncounterCard(
            Kind(enc),
            location,
            template.LevelMin,
            template.LevelMax,
            GameInfo.GetVersionName(template.Version),
            enc.LongName,
            shiny is Shiny.Always or Shiny.AlwaysStar or Shiny.AlwaysSquare,
            shiny == Shiny.Never);
    }

    /// <summary>Card groups, in gallery order. Names come from the pinned Core's stable
    /// encounter names, so the classification travels with the submodule pin.</summary>
    public static string Kind(IEncounterable enc)
    {
        if (enc is IEncounterTemplate { IsEgg: true }) return "Egg";
        if (enc.Name.Contains("Wild", StringComparison.Ordinal)) return "Wild";
        if (enc.Name.Contains("Trade", StringComparison.Ordinal)) return "Trade";
        if (enc.Name.Contains("Static", StringComparison.Ordinal)) return "Static";
        if (enc.Name.Contains("Gift", StringComparison.Ordinal) || enc.Name.Contains("Mystery", StringComparison.Ordinal)) return "Event";
        return "Special";
    }

    public static int KindRank(string kind) => kind switch
    {
        "Egg" => 0,
        "Wild" => 1,
        "Static" => 2,
        "Trade" => 3,
        "Event" => 4,
        _ => 5,
    };
}
