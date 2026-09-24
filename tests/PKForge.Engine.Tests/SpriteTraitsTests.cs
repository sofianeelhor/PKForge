using PKForge.Domain;
using PKForge.Engine;
using PKForge.Engine.RadicalRed;
using PKForge.Infrastructure;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The sprite key survives the trip from entity bytes into every model: gender, Alcremie's
/// decoration and Gen 6 cosplay come from the PKM itself, and a CFRU romhack form species
/// ("Charizard-Mega-Y") recovers its PKHeX form instead of drawing as the base species.
/// </summary>
public sealed class SpriteTraitsTests
{
    [Fact]
    public void Entity_traits_carry_gender_decoration_and_cosplay()
    {
        var alcremie = new PK8 { Species = 869, Form = 3, FormArgument = 5, Gender = 1 };
        Assert.Equal(new SpriteTraits(Female: true, FormArgument: 5), EntitySprite.Traits(alcremie));

        var cosplay = new PK6 { Species = 25, Form = 4, Gender = 1, PID = 0x12345678 };
        Assert.Equal(new SpriteTraits(Female: true, Cosplay: true), EntitySprite.Traits(cosplay));

        var cap = new PK7 { Species = 25, Form = 4, Gender = 0, PID = 0x12345678 };
        Assert.Equal(default, EntitySprite.Traits(cap));

        // The resolved bundled asset for the ORAS cosplay is the 'c' sprite, not the Gen 7 cap.
        Assert.Equal("sprites/b_25-4c.png", SpriteCatalog.BundledCandidates(EntitySprite.Look(cosplay))[0].Path);
        Assert.Equal("sprites/b_25-4.png", SpriteCatalog.BundledCandidates(EntitySprite.Look(cap))[0].Path);
    }

    [Theory]
    [InlineData("Charizard-Mega-Y", 6, 2, false, false)]
    [InlineData("Charizard-Mega-X", 6, 1, false, false)]
    [InlineData("Raichu-Alola", 26, 1, false, false)]
    [InlineData("Tauros-Paldea-Combat", 128, 1, false, false)]
    [InlineData("Tauros-Paldea-Blaze", 128, 2, false, false)]
    [InlineData("Tauros-Paldea-Aqua", 128, 3, false, false)]
    [InlineData("Unown-!", 201, 26, false, false)]
    [InlineData("Unown-?", 201, 27, false, false)]
    [InlineData("Pikachu-Rock-Star", 25, 1, true, false)]
    [InlineData("Pikachu-Original", 25, 1, false, false)]
    [InlineData("Charizard-Gmax", 6, 0, false, true)]
    [InlineData("Kyogre-Primal", 382, 1, false, false)]
    [InlineData("Pikachu-Surfing", 25, 0, false, false)] // no PKHeX form: stays the base
    [InlineData("Charizard", 6, 0, false, false)]
    public void Romhack_form_species_resolve_to_the_PKHeX_form(string name, int national, int form, bool cosplay, bool gmax)
    {
        var (resolved, traits) = RomhackForms.Resolve(national, name);
        Assert.Equal((form, cosplay, gmax), (resolved, traits.Cosplay, traits.Gigantamax));
    }

    [Fact]
    public void Radical_Red_form_ids_bridge_to_their_forms()
    {
        // Ids from RadicalRed/Data/species.txt (CFRU order).
        foreach (var (id, national, form) in new[] { (871, 6, 2), (870, 6, 1), (1022, 26, 1), (1234, 128, 1), (438, 201, 26) })
        {
            Assert.Equal(national, RadicalRedData.NationalIdOf(id));
            Assert.Equal(form, RomhackForms.Resolve(national, RadicalRedData.SpeciesName(id)).Form);
        }
    }

    [Fact]
    public void Legacy_bank_entries_get_their_sprite_traits_from_the_stored_bytes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pkforge-traits-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bank = new FileBankService(dir);
            var hippowdon = new PK8 { Species = 450, Gender = 1, PID = 0x12345678 };
            var alcremie = new PK8 { Species = 869, Form = 2, FormArgument = 4, Gender = 1, PID = 0x12345678 };
            var ids = new List<Guid>();
            foreach (var pk in new PKM[] { hippowdon, alcremie })
            {
                var data = new byte[pk.SIZE_PARTY];
                pk.WriteDecryptedDataParty(data);
                // As deposited before the field existed: no traits recorded.
                ids.Add(bank.Add(data, new BankEntryInfo(pk.Species, pk.Form, false, "x", 50, 8, "test", "PK8")).Id);
            }

            Assert.Equal(2, EntityBytes.MigrateSpriteTraits(bank));
            var byId = new FileBankService(dir).GetAll().ToDictionary(e => e.Id);
            Assert.Equal("sprites/b_450f.png", SpriteCatalog.BundledCandidates(byId[ids[0]].Info.Look)[0].Path);
            Assert.Equal("sprites/b_869-2-4.png", SpriteCatalog.BundledCandidates(byId[ids[1]].Info.Look)[0].Path);
            Assert.Equal(0, EntityBytes.MigrateSpriteTraits(new FileBankService(dir))); // idempotent
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
