using PKForge.Domain;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The 3.2 save pipeline end to end for the Recharged Yellow, Quetzal, Soul Gold and
/// pokeemerald-expansion engines: the documented routing order, the shelf description
/// (name, trainer id, "h:mm:ss" play time), the identity sheet's family and choices, the
/// format-pinned open that refuses a wrong route, and the write-safety contract (no layout
/// risk, a no-op write is byte-identical and passes the structural check). The real saves
/// live in .local-testdata/romhacks (gitignored); every case skips when its sample is absent.
/// </summary>
public sealed class RomHackRoutingTests
{
    private static byte[]? Sample(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", "romhacks", file);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    [Theory]
    [InlineData("recharged-yellow.sav", SaveFormat.RechargedYellow, "Recharged Yellow", SaveLayoutFamily.RechargedYellow)]
    [InlineData("quetzal-9.1.sav", SaveFormat.Quetzal, "Quetzal", SaveLayoutFamily.Quetzal)]
    [InlineData("quetzal-bills.sav", SaveFormat.Quetzal, "Quetzal", SaveLayoutFamily.Quetzal)]
    [InlineData("soulgold.sav", SaveFormat.SoulGold, "Soul Gold", SaveLayoutFamily.SoulGold)]
    [InlineData("heart-and-soul.sav", SaveFormat.Expansion, "Heart and Soul 2.0", SaveLayoutFamily.Expansion)]
    [InlineData("lazarus.sav", SaveFormat.Expansion, "Lazarus", SaveLayoutFamily.Expansion)]
    [InlineData("emerald-seaglass.sav", SaveFormat.Expansion, "pokeemerald-expansion", SaveLayoutFamily.Expansion)]
    [InlineData("emerald-enhanced.sav", SaveFormat.Expansion, "Emerald Enhanced", SaveLayoutFamily.Expansion)]
    [InlineData("perfect-emerald.sav", SaveFormat.Expansion, "Perfect Emerald", SaveLayoutFamily.Expansion)]
    public void SampleRoutesDescribesAndOpensOnlyThroughItsOwnEngine(string file, SaveFormat route, string game, SaveLayoutFamily family)
    {
        if (Sample(file) is not { } bytes) return;
        var engine = new SaveEngine();

        Assert.Equal(route, SaveParser.HackRoute(RetroArchSaveContainer.Decode(bytes)));
        Assert.True(engine.Validate(bytes));

        var description = engine.TryDescribe(bytes, file);
        Assert.NotNull(description);
        Assert.Equal(game, description.GameName);
        Assert.Equal(3, description.Generation);
        Assert.Matches(@"^\d+:\d\d:\d\d$", description.PlayTime);

        var guess = SaveIdentityRules.Guess(description.GameName, description.Generation, file, null, file);
        Assert.Equal(family, guess.Family);
        Assert.Equal(route, guess.Format);
        var choices = SaveIdentityRules.ChoicesFor(guess.Family, guess.Label);
        Assert.All(choices, c => Assert.Equal(route, c.Format));

        using (var session = engine.OpenSession(bytes, file, route))
            Assert.True(session.Serialize().Span.SequenceEqual(bytes), "an untouched save must round-trip byte for byte");
        Assert.Throws<InvalidDataException>(() => engine.OpenSession(bytes, file, SaveFormat.Standard));
        Assert.Throws<InvalidDataException>(() => engine.OpenSession(bytes, file, SaveFormat.RadicalRed));

        Assert.Null(engine.AssessLayoutRisk(bytes));
        Assert.Null(engine.CheckWriteSafety(bytes, bytes, null));
    }

    [Fact]
    public void TrainerIdSeparatesPlaythroughsOnTheShelf()
    {
        foreach (var file in new[] { "quetzal-9.1.sav", "soulgold.sav", "heart-and-soul.sav", "lazarus.sav", "recharged-yellow.sav" })
        {
            if (Sample(file) is not { } bytes) continue;
            using var session = new SaveEngine().OpenSession(bytes, file);
            var description = new SaveEngine().TryDescribe(bytes, file);
            Assert.NotNull(description);
            Assert.Equal(session.GetTrainer().TID, description.TrainerId);
        }
    }
}
