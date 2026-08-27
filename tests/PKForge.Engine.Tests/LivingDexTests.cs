
using PKForge.Engine;
using PKHeX.Core;
using PKHeX.Core.AutoMod;
using Xunit;
using static PKHeX.Core.GameVersion;

namespace PKForge.Engine.Tests;

public sealed class LivingDexProbe
{
    [Fact]
    public void AlmPatternOnOR()
    {
        APILegality.EnableDevMode = false;
        APILegality.UseTrainerData = false;
        var trainer = new SimpleTrainerInfo(OR) { OT = "ALMUT" };
        var personal = GameData.GetPersonal(OR);
        RecentTrainerCache.SetRecentTrainer(trainer);
        var all = trainer.GenerateLivingDex(personal).ToArray();
        Assert.True(all.Length > 0, $"yielded {all.Length}");
    }
}
