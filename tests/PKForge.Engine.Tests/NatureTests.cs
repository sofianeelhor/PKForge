
using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Gen 3 natures derive from the PID: setting Nature must re-roll the personality bits,
/// or the edit silently reverts on read. Reported on Emerald (1.2.0).
/// </summary>
public sealed class NatureTests
{
    private static string FirstPk3()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var root = Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests");
        return Directory.EnumerateFiles(root, "*.pk3", SearchOption.AllDirectories).First();
    }

    [Theory]
    [InlineData("*.pk3")]
    [InlineData("*.pk4")]
    [InlineData("*.pk5")]
    [InlineData("*.pk6")]
    public void NatureEditSticksOnPIDDerivedFormats(string pattern)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var root = Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests");
        var file = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).First();

        var engine = new SaveEngine();
        using var session = engine.OpenEntitySession(File.ReadAllBytes(file), "pid");
        Assert.NotNull(session);

        var before = session!.ReadEntity(0, 0);
        var target = (before.Nature + 5) % 25;
        session.ApplyEdit(0, 0, new EntityEdit(Nature: target));
        Assert.Equal(target, session.ReadEntity(0, 0).Nature);
    }

    [Fact]
    public void NatureEditSticksOnGen3()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenEntitySession(File.ReadAllBytes(FirstPk3()), "g3");
        Assert.NotNull(session);

        var before = session!.ReadEntity(0, 0);
        var target = ((before.Nature + 5) % 25);
        session.ApplyEdit(0, 0, new EntityEdit(Nature: target));
        var after = session.ReadEntity(0, 0);
        Assert.Equal(target, after.Nature);
    }
}
