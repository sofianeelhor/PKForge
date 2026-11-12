using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// A Pokémon's met or egg location by name. PKHeX's <c>GameInfo.GetLocationName</c> answers
/// nothing for a Gen 3 Pokémon stored in a Gen 4 format (it maps origin 3 + format 4 to no
/// location table), so a FireRed mon sent to Platinum read "Met location: #55" instead of Pal
/// Park (QA 2026-11-12). Such a mon's location was rewritten on transfer and is a Gen 4 id: it is
/// looked up in its format's table.
/// </summary>
public static class LocationNames
{
    /// <summary>The location's name, or an empty string when no table has it.</summary>
    public static string Of(PKM pk, ushort location, bool egg = false)
    {
        var name = GameInfo.GetLocationName(egg, location, pk.Format, pk.Generation, pk.Version);
        if (name is { Length: > 0 } || pk.Format < 4 || pk.Generation >= pk.Format) return name ?? "";
        return GameInfo.GetLocationName(egg, location, pk.Format, pk.Format, pk.Version) ?? "";
    }
}
