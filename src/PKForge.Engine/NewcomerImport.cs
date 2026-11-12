using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// How a Pokémon that did not exist in the save a moment ago is written to it: generated,
/// received as a gift, pasted from Showdown. The game registers what the player obtains, so
/// the Pokédex is updated the way PKHeX does it; the handler and record rewrites stay off,
/// so the newcomer keeps the identity it was built with.
/// </summary>
internal static class NewcomerImport
{
    private static readonly EntityImportSettings DexOnly =
        new(EntityImportOption.Disable, EntityImportOption.Enable, EntityImportOption.Disable);

    /// <summary>
    /// The settings for <paramref name="newcomer"/> entering <paramref name="save"/>. An egg
    /// is registered when it hatches, not before, and a species the game has no data for
    /// (HaX generation) has no Pokédex entry to mark.
    /// </summary>
    public static EntityImportSettings For(SaveFile save, PKM newcomer) =>
        !newcomer.IsEgg && newcomer.Species <= save.MaxSpeciesID && save.Personal.IsSpeciesInGame(newcomer.Species)
            ? DexOnly
            : EntityImportSettings.None;
}
