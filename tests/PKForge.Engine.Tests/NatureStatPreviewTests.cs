using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The nature pickers' labels and live stat preview: the label table must match
/// PKHeX's NatureAmp, and the preview must match what a nature edit really does.
/// </summary>
public sealed class NatureStatPreviewTests
{
    private const int Garchomp = 445;
    private const int Adamant = 3, Jolly = 13, Modest = 15, Hardy = 0;

    [Fact]
    public void LabelsMatchPkhexNatureAmpForEveryNature()
    {
        // PKHeX stat order H/A/B/S/C/D; amp index + 1 addresses it. Display order is H/A/B/C/D/S.
        int[] pkhexToDisplay = [0, 1, 2, 5, 3, 4];
        for (var n = 0; n < 25; n++)
        {
            var (up, dn) = ((Nature)n).GetNatureModification();
            Assert.Equal(((Nature)n).IsNeutral, NatureFacts.IsNeutral(n));
            if (up == dn)
            {
                Assert.Null(NatureFacts.Raised(n));
                Assert.Equal("neutral", NatureFacts.EffectLabel(n));
                continue;
            }
            Assert.Equal(pkhexToDisplay[up + 1], NatureFacts.Raised(n));
            Assert.Equal(pkhexToDisplay[dn + 1], NatureFacts.Lowered(n));
        }
        Assert.Equal("+Atk −SpA", NatureFacts.EffectLabel(Adamant));
        Assert.Equal("+SpA −Atk", NatureFacts.EffectLabel(Modest));
        Assert.Equal("likes Spicy · dislikes Dry", NatureFacts.FlavorLabel(Adamant));
        Assert.Equal("likes Dry · dislikes Spicy", NatureFacts.FlavorLabel(Modest));
        Assert.Equal("likes Sweet · dislikes Dry", NatureFacts.FlavorLabel(Jolly));
    }

    [Fact]
    public void GarchompAdamantMatchesKnownLevel100Stats()
    {
        using var session = Seed(9, pk =>
        {
            pk.Species = Garchomp;
            pk.CurrentLevel = 100; // level is stored as EXP: re-set it for the new growth rate
            pk.Nature = pk.StatAlignment = (Nature)Jolly;
            pk.SetIVs([31, 31, 31, 31, 31, 31]);
            pk.SetEVs([4, 252, 0, 252, 0, 0]); // H/A/B/S/C/D
        });

        var preview = new StatPreviewService().PreviewSlot(session, 0, 0)!;
        Assert.NotNull(preview);
        Assert.Equal(Jolly, preview.CurrentNature);
        Assert.Equal([358, 359, 226, 176, 206, 333], preview.Current);          // Jolly: +Spe -SpA
        Assert.Equal([358, 394, 226, 176, 206, 303], preview.StatsFor(Adamant)); // Adamant: +Atk -SpA
        Assert.Equal([0, 35, 0, 0, 0, -30], preview.DeltaFor(Adamant));
        Assert.Equal(session.ReadEntity(0, 0).Stats, preview.Current);           // same calc as the editor's STATS row
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(9)]
    public void NeutralNaturesHaveZeroDeltaFromEachOther(int generation)
    {
        using var session = Seed(generation, pk => pk.CurrentLevel = 50);
        var preview = new StatPreviewService().PreviewSlot(session, 0, 0)!;
        foreach (var neutral in new[] { 0, 6, 12, 18, 24 })
            Assert.Equal(preview.StatsFor(Hardy), preview.StatsFor(neutral));
    }

    [Fact]
    public void Gen3PreviewMatchesThePidRerollTheEditPerforms()
    {
        using var session = Seed(3, pk => pk.CurrentLevel = 60);
        var preview = new StatPreviewService().PreviewSlot(session, 0, 0)!;
        var target = (preview.CurrentNature + 3) % 25;
        var predicted = preview.StatsFor(target);

        session.ApplyEdit(0, 0, new EntityEdit(Nature: target));
        var after = session.ReadEntity(0, 0);
        Assert.Equal(target, after.Nature);
        Assert.Equal(predicted, after.Stats);
    }

    [Fact]
    public void Gen8UnmintedStatsFollowTheNewNature()
    {
        using var session = Seed(8, pk => pk.Nature = pk.StatAlignment = (Nature)Hardy);
        var preview = new StatPreviewService().PreviewSlot(session, 0, 0)!;
        Assert.Null(preview.StatNatureLock);
        Assert.NotEqual(preview.Current, preview.StatsFor(Adamant));

        session.ApplyEdit(0, 0, new EntityEdit(Nature: Adamant));
        Assert.Equal(preview.StatsFor(Adamant), session.ReadEntity(0, 0).Stats);
    }

    [Fact]
    public void Gen8MintKeepsStatsWhateverNatureIsPicked()
    {
        using var session = Seed(8, pk =>
        {
            pk.Nature = (Nature)Hardy;
            pk.StatAlignment = (Nature)Modest; // minted
        });
        var preview = new StatPreviewService().PreviewSlot(session, 0, 0)!;
        Assert.Equal(Modest, preview.StatNatureLock);
        Assert.All(Enumerable.Range(0, 25), n => Assert.Equal(preview.Current, preview.StatsFor(n)));

        session.ApplyEdit(0, 0, new EntityEdit(Nature: Adamant));
        Assert.Equal(Adamant, session.ReadEntity(0, 0).Nature);
        Assert.Equal(preview.Current, session.ReadEntity(0, 0).Stats);
    }

    [Fact]
    public void PendingEditorValuesDriveThePreview()
    {
        using var session = Seed(9, pk => pk.CurrentLevel = 5);
        var overrides = new StatPreviewOverrides(Species: Garchomp, Level: 100,
            IVs: [31, 31, 31, 31, 31, 31], EVs: [4, 252, 0, 0, 0, 252]); // display order
        var preview = new StatPreviewService().PreviewSlot(session, 0, 0, overrides)!;
        Assert.Equal(394, preview.StatsFor(Adamant)[1]);
        Assert.Equal(333, preview.StatsFor(Jolly)[5]);
        Assert.Equal(5, session.ReadEntity(0, 0).Level); // the stored mon is untouched
    }

    [Fact]
    public void GenerationPreviewUsesPerfectIvsAndNoEvs()
    {
        using var session = new SaveEngine().OpenBlankSession(9);
        var preview = new StatPreviewService().PreviewSpecies(session, Garchomp, 0, 100)!;
        Assert.Equal([357, 296, 226, 196, 206, 240], preview.StatsFor(Hardy));
        Assert.Equal(325, preview.StatsFor(Adamant)[1]);
        Assert.Equal(176, preview.StatsFor(Adamant)[3]);
        // The Domain formula agrees with PKHeX's for the same inputs.
        Assert.Equal(preview.StatsFor(Adamant),
            NatureStatMath.Compute(session.GetBaseStats(Garchomp), 100, [31, 31, 31, 31, 31, 31], [0, 0, 0, 0, 0, 0], Adamant));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Gen1And2HaveNoNaturePreview(int generation)
    {
        using var session = new SaveEngine().OpenBlankSession(generation);
        Assert.Null(new StatPreviewService().PreviewSpecies(session, 25, 0, 50));
        Assert.Null(new StatPreviewService().PreviewSlot(session, 0, 0));
    }

    private static ISaveEngineSession Seed(int generation, Action<PKM> shape)
    {
        PKM mon = generation switch
        {
            3 => new PK3(),
            5 => new PK5(),
            8 => new PK8(),
            _ => new PK9(),
        };
        mon.Species = generation == 8 ? (ushort)Garchomp : (ushort)25;
        mon.CurrentLevel = 100;
        mon.Version = generation switch
        {
            3 => GameVersion.FR,
            5 => GameVersion.B,
            8 => GameVersion.SW,
            _ => GameVersion.VL,
        };
        shape(mon);
        mon.RefreshChecksum();
        var bytes = new byte[mon.SIZE_STORED];
        mon.WriteDecryptedDataStored(bytes);
        var session = new SaveEngine().OpenBlankSession(generation);
        Assert.True(session.ImportSlot(0, 0, bytes), $"gen {generation}: could not seed a mon");
        return session;
    }
}
