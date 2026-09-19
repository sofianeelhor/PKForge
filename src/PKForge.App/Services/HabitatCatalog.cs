using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.App.Services;

public sealed record HabitatProfile(string Name, string Subtitle, string Ground, string Water, string Accent);

public static class HabitatCatalog
{
    // Park snapshots have species and form, but no source generation. Prefer modern
    // typing, then a game that actually contains this form (including regional forms).
    private static readonly IPersonalTable[] Tables =
        [PersonalTable.SV, PersonalTable.ZA, PersonalTable.SWSH, PersonalTable.LA, PersonalTable.USUM];

    /// <summary>Canonical 1–18 park type IDs for a species/form. Never returns raw PKHeX values.</summary>
    public static IReadOnlyList<int> TypesFor(int species, int form = 0)
    {
        if (species <= 0 || species > ushort.MaxValue || form < 0 || form > byte.MaxValue)
            return [ParkType.Unknown];
        foreach (var table in Tables)
        {
            if (!table.IsPresentInGame((ushort)species, (byte)form)) continue;
            var entry = table.GetFormEntry((ushort)species, (byte)form);
            var first = ParkType.FromPkhex(entry.Type1);
            var second = ParkType.FromPkhex(entry.Type2);
            // Single-typed forms report the same type twice; collapse to one entry.
            return first == second ? [first] : [first, second];
        }
        // Invalid/stale form metadata must not accidentally select another species.
        return form == 0 ? [ParkType.Unknown] : TypesFor(species);
    }

    public static HabitatProfile Profile(IReadOnlyList<int> types)
    {
        if (types.Contains(ParkType.Water)) return new("Cascade Lake", "Cool shallows and open swimming water", "#B7DCE0", "#4DA6BB", "#E6FBFF");
        if (types.Contains(ParkType.Fire)) return new("Ember Grove", "Warm basalt shelves and sleepy sparks", "#D99B70", "#BA604A", "#FFE0A8");
        if (types.Contains(ParkType.Flying) || types.Contains(ParkType.Ice)) return new("Sky Summit", "Mountain breezes above the cloud line", "#D5E2ED", "#8DA9C5", "#FFFFFF");
        if (types.Contains(ParkType.Grass)) return new("Verdant Grove", "Broad leaves, flowers, and soft moss", "#B9D990", "#6FAE76", "#F0F6BE");
        if (types.Contains(ParkType.Electric)) return new("Static Meadow", "Tall grass humming with little lights", "#D7D38E", "#93B06A", "#FFF5A6");
        return new("Meadow Commons", "A welcoming green meadow for everyone", "#C8E5A0", "#80B477", "#F8E7B4");
    }
}
