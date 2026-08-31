using System.Buffers.Binary;
using PKHeX.Core;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Unbound keeps FireRed's save envelope, so the only safe separator is the CFRU
/// sector-footer signature. Vanilla FireRed stamps 0x080120xx on every sector;
/// Unbound stamps 0x01121999. Verified against real saves for both sides.
/// </summary>
public sealed class UnboundDetectionTests
{
    private static string? LocalArtifact(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", file);
        return path is not null && File.Exists(path) ? path : null;
    }

    private static byte[] Fixture(uint signature, int stampedSectors)
    {
        var data = new byte[0x20_000];
        for (var sector = 0; sector < stampedSectors; sector++)
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(sector * 0x1000 + 0xFF8), signature);
        return data;
    }

    [Fact]
    public void DetectsTheUnboundSignature()
    {
        Assert.True(SaveParser.IsPokemonUnbound(Fixture(SaveParser.UnboundSectorSignature, 14)));
    }

    [Theory]
    [InlineData(0x0801_2025u)] // vanilla FireRed
    [InlineData(0u)]
    public void VanillaSignaturesAreNotUnbound(uint signature)
    {
        Assert.False(SaveParser.IsPokemonUnbound(Fixture(signature, 14)));
    }

    [Fact]
    public void ASparseSignatureIsNotEnough()
    {
        Assert.False(SaveParser.IsPokemonUnbound(Fixture(SaveParser.UnboundSectorSignature, 3)));
    }

    [Fact]
    public void RealUnboundSaveIsLabeledAndOpensInTheUnboundSession()
    {
        var path = LocalArtifact("unbound-v2111.srm");
        if (path is null) return; // ground truth lives gitignored on the dev machine

        var bytes = File.ReadAllBytes(path);
        Assert.True(SaveParser.IsPokemonUnbound(bytes));

        var engine = new SaveEngine();
        var description = engine.TryDescribe(bytes);
        Assert.NotNull(description);
        Assert.Equal("Unbound", description!.GameName);

        using var session = engine.OpenSession(bytes);
        Assert.Equal(3, session.Generation);
        Assert.Equal(246, session.ReadEntity(-1, 0).Species); // the trainer's Larvitar
    }

    [Fact]
    public void RealVanillaFireRedStaysFullyEditable()
    {
        var path = LocalArtifact("firered-vanilla.sav");
        if (path is null) return;

        var bytes = File.ReadAllBytes(path);
        Assert.False(SaveParser.IsPokemonUnbound(bytes));

        var engine = new SaveEngine();
        var description = engine.TryDescribe(bytes);
        Assert.NotNull(description);
        Assert.NotEqual("Unbound", description!.GameName);

        using var session = engine.OpenSession(bytes);
        Assert.Equal(3, session.Generation);

        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var save));
        Assert.IsType<SAV3FRLG>(save);
        Assert.True(save!.ChecksumsValid);
    }
}
