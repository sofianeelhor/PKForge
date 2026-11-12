using PKForge.Domain;
using PKHeX.Core;
using PKHeX.Core.AutoMod;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// "After a transfer some Pokémon have ALM as handling trainer." ALM is Auto-Legality's
/// built-in stand-in trainer, used whenever a Pokémon is built from another game's
/// encounter. PKForge's generation and repairs must use the open save's trainer instead.
/// The stand-in is process-wide, so these run apart from the parallel tests.
/// </summary>
[Collection(nameof(LegalityEraCollection))]
public sealed class AutoModTrainerTests
{
    [Fact]
    public void TheStandInIsTheSaveTrainerOnlyWhileTheScopeLasts()
    {
        var save = BlankSaveFile.Get(GameVersion.SW, "Serena", LanguageID.English);
        save.TID16 = 1111;
        save.SID16 = 2222;

        using (new AutoModTrainerScope(save))
        {
            var standIn = TrainerSettings.GetSavedTrainerData(GameVersion.US);
            Assert.Equal("Serena", standIn.OT);
            Assert.Equal(1111, standIn.TID16);
            Assert.Equal(2222, standIn.SID16);
        }

        Assert.Equal("ALM", TrainerSettings.DefaultOT);
        Assert.Equal("ALM", TrainerSettings.GetSavedTrainerData(GameVersion.US).OT);
    }

    [Fact]
    public void ADefaultScopeChangesNothing()
    {
        using (default(AutoModTrainerScope)) { }
        Assert.Equal("ALM", TrainerSettings.DefaultOT);
    }

    [Fact]
    public void RepairingATransferredPokemonNeverLeavesAlm()
    {
        // A Gen 7 catch now in Sword, broken by an impossible level-1 met level: the repair
        // rebuilds it from its Ultra Sun origin, a game the open save is not.
        var save = BlankSaveFile.Get(GameVersion.SW, "Serena", LanguageID.English);
        var pk7 = new PK7
        {
            Species = 25, Version = GameVersion.US, CurrentLevel = 30, MetLevel = 1, MetLocation = 6,
            OriginalTrainerName = "Ash", TID16 = 4242, SID16 = 1717, Language = (int)LanguageID.English,
            Nickname = "PIKACHU", Move1 = 84, Ball = 4, PID = 0x1234_5678, EncryptionConstant = 0x1357_9BDF,
        };
        pk7.RefreshChecksum();
        RecentTrainerCache.SetRecentTrainer(save);
        var current = EntityConverter.ConvertToType(pk7, typeof(PK8), out _);
        Assert.NotNull(current);

        var repaired = LegalizerService.LegalizeKeepingOrigin(save, current!);

        Assert.NotEqual("ALM", repaired.OriginalTrainerName);
        Assert.NotEqual("ALM", repaired.HandlingTrainerName);
        Assert.Equal("ALM", TrainerSettings.DefaultOT); // restored afterwards
    }
}
