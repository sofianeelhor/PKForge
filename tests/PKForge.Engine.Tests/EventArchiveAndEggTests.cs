using PKHeX.Core;
using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

[Collection(nameof(EventDatabaseCollection))] // swaps the process-wide event database
public sealed class EventArchiveAndEggTests
{
    [Fact]
    public void GalleryFolderGiftsAppearInWonderCardList()
    {
        // Same mechanism EventArchive uses at runtime: drop a folder of gallery files
        // into PKHeX's local tables and the service must merge EGDB with MGDB.
        var folder = Path.Combine(Path.GetTempPath(), $"pkforge-events-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var gift = new PGF
            {
                CardTitle = "PKForge Test Distribution",
                Species = (ushort)Species.Pikachu,
                IsEntity = true,
            };
            File.WriteAllBytes(Path.Combine(folder, "test.pgf"), gift.Write());

            EncounterEvent.RefreshMGDB(folder);

            var engine = new SaveEngine();
            using var session = engine.OpenBlankSession(5);
            var service = new EventDatabaseService();
            var gifts = service.GetGifts(session);

            Assert.Contains(gifts, g => g.Title.Contains("PKForge Test", StringComparison.Ordinal));
        }
        finally
        {
            EncounterEvent.RefreshMGDB();
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void EggFactoryLaysHatchableEggs()
    {
        var engine = new SaveEngine();
        using var raw = engine.OpenBlankSession(5);
        var session = (SaveEngineSession)raw;
        var legalizer = new LegalizerService();

        var outcome = legalizer.GenerateEggs(session, [25, 6], new EggOptions(MaxIv: true, Shiny: true));

        Assert.True(outcome.Success, outcome.Message);
        // Real eggs of each line's first stage (issue #75: never a level-100 "hatched" Pokémon).
        var pikachu = session.GetEntity(0, 0);
        var charizard = session.GetEntity(0, 1);
        Assert.True(pikachu.IsEgg);
        Assert.Equal(172, pikachu.Species); // Pichu
        Assert.True(charizard.IsEgg);
        Assert.Equal(4, charizard.Species); // Charmander
        Assert.Equal(1, pikachu.CurrentLevel);
        Assert.Equal(EggStateLegality.GetMaximumEggHatchCycles(pikachu), pikachu.OriginalTrainerFriendship);
        Span<int> ivs = stackalloc int[6];
        pikachu.GetIVs(ivs);
        Assert.All(ivs.ToArray(), iv => Assert.Equal(31, iv));
        Assert.True(charizard.IsShiny);

        var bytes = session.Serialize().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var reopened));
        using var reloaded = new SaveEngineSession(reopened!, null);
        Assert.True(reloaded.GetEntity(0, 0).IsEgg);
        Assert.Equal(172, reloaded.GetEntity(0, 0).Species);
    }
}
