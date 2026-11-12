using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class DexModeSourcesTests
{
    private static readonly DexHolder Here = new(LivingDexPlanner.BankId, "Bank", 0, 0, Guid.NewGuid());

    [Fact]
    public void CatalogFlagsRegionalFormsGenderDifferencesGigantamaxAndSweets()
    {
        var catalog = DexModeSources.Catalog().ToDictionary(s => s.Species);
        var meowth = catalog[(int)Species.Meowth];
        Assert.Contains(meowth.Forms, f => f.Form == 1 && f.Regional);
        Assert.Contains(meowth.Forms, f => f.Form == 2 && f.Regional);
        Assert.Equal([0], meowth.GigantamaxForms!);
        Assert.True(catalog[(int)Species.Pikachu].GenderDifference);
        Assert.True(catalog[(int)Species.Venusaur].GenderDifference);
        Assert.False(catalog[(int)Species.Bulbasaur].GenderDifference);
        Assert.Equal(7, catalog[(int)Species.Alcremie].Sweets);
        Assert.Equal(9, catalog[(int)Species.Alcremie].Forms.Count);
        Assert.Equal([0, 1], catalog[(int)Species.Urshifu].GigantamaxForms!);
        Assert.Null(catalog[(int)Species.Bulbasaur].GigantamaxForms);
        Assert.True(catalog[(int)Species.Unown].Forms.Count >= 28);
        Assert.DoesNotContain(catalog[(int)Species.Unown].Forms, f => f.Regional);
        Assert.Contains(catalog[(int)Species.Tauros].Forms, f => f.Regional); // Paldean breeds
        // Battle-only looks never become an entry.
        Assert.DoesNotContain(catalog[(int)Species.Charizard].Forms, f => f.Form > 0);

        // The whole national dex: a full extended count is far bigger than one per species.
        var all = ExtendedDex.Entries(catalog.Values, DexFormCategories.All);
        Assert.True(all.Count > 1300, $"{all.Count}");
        Assert.Equal(CollectionDex.MaxSpecies, ExtendedDex.Entries(catalog.Values, DexFormCategories.None).Count);
    }

    [Fact]
    public void EntityOriginIsItsGameGeneration()
    {
        var treecko = new PK3 { Species = (ushort)Species.Treecko, Version = GameVersion.E };
        var mon = DexModeSources.FromEntity(treecko, Here);
        Assert.Equal(3, mon.OriginGeneration);
        Assert.True(OriginDex.IsNative(mon));

        var bulbasaur = new PK8 { Species = (ushort)Species.Bulbasaur, Version = GameVersion.SW };
        Assert.Equal(8, DexModeSources.FromEntity(bulbasaur, Here).OriginGeneration);
        Assert.False(OriginDex.IsNative(DexModeSources.FromEntity(bulbasaur, Here)));

        var vc = new PK7 { Species = (ushort)Species.Bulbasaur, Version = GameVersion.RD };
        Assert.True(OriginDex.IsNative(DexModeSources.FromEntity(vc, Here)));

        var go = new PK8 { Species = (ushort)Species.Bulbasaur, Version = GameVersion.GO };
        Assert.Equal(0, DexModeSources.FromEntity(go, Here).OriginGeneration);

        var gen1 = new PK1 { Species = (ushort)Species.Bulbasaur };
        Assert.True(OriginDex.IsNative(DexModeSources.FromEntity(gen1, Here)));
    }

    [Fact]
    public void EntityTraitsReachTheExtendedDex()
    {
        var alcremie = new PK8 { Species = (ushort)Species.Alcremie, Form = 3, FormArgument = 5, Version = GameVersion.SH, CanGigantamax = true, Gender = 1 };
        var mon = DexModeSources.FromEntity(alcremie, Here);
        Assert.Equal((3, 5, true, true), (mon.Form, mon.FormArgument, mon.Gigantamax, mon.Female));
    }
}
