using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>The sprite traits of a PKHeX entity: the one place entity fields become a sprite key.</summary>
public static class EntitySprite
{
    /// <summary>
    /// Gender (female art), Alcremie's decoration (IFormArgument, 0-6) and Gen 6 Pikachu
    /// cosplay - exactly the inputs PKHeX's SpriteUtil.GetSprite(PKM) feeds SpriteName.
    /// Eggs draw as eggs, so their traits are irrelevant but harmless.
    /// </summary>
    public static SpriteTraits Traits(PKM pk) => new(
        Female: pk.Gender == 1,
        FormArgument: pk.Species == SpriteCatalog.Alcremie && pk is IFormArgument f ? (int)Math.Min(f.FormArgument, 6u) : 0,
        Cosplay: pk.Species == SpriteCatalog.Pikachu && pk.Context == EntityContext.Gen6 && pk.Form is >= 1 and <= 6);

    public static SpriteLook Look(PKM pk) => new(pk.Species, pk.Form, pk.IsShiny, Traits(pk));
}
