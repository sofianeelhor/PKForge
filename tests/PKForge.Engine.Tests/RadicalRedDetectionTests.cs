using System.Buffers.Binary;
using PKHeX.Core;
using PKForge.Engine;
using PKForge.Engine.RadicalRed;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Radical Red keeps FireRed's save envelope AND its retail signature, so detection
/// is structural: the CFRU chunk-window checksums validate 14/14 where every vintage
/// FRLG window table fails somewhere. Unbound shares the CFRU windows but stamps its
/// own signature, so it must be excluded first everywhere. Verified against real
/// saves for all three formats (Radical Red, vanilla FireRed, Unbound).
/// </summary>
public sealed class RadicalRedDetectionTests
{
    private static string? LocalArtifact(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", file);
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>A vanilla-shaped 0x20000 save: both rotating slots stamped, checksums
    /// computed over the given window table, a sane plaintext party in section 1, and
    /// nonzero bytes everywhere (so windows that include more than the stored window
    /// fail, never accidentally pass).</summary>
    private static byte[] WindowedFixture(ushort[] windows)
    {
        var data = new byte[0x20_000];
        for (var id = 0; id < 14; id++)
        {
            for (var slot = 0; slot < 2; slot++)
            {
                var off = (slot * 14 + id) * 0x1000;
                for (var i = 0; i < 0xFF4; i++)
                    data[off + i] = (byte)(id * 41 + i * 7 + 1);
                if (id == 1)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0x34), 6u);
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(off + 0x38 + 0x20), 25); // Raichu
                    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0x38 + 0x24), 125_000);
                }
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(off + 0xFF4), (ushort)id);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0xFF8), 0x0801_2025u);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0xFFC), slot == 0 ? 10u : 11u);
                var checksum = RadicalRedFormat.Checksum(data.AsSpan(off, windows[id]), windows[id]);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(off + 0xFF6), checksum);
            }
        }
        return data;
    }

    [Fact]
    public void CfruWindowsDetectRadicalRed()
    {
        var fixture = WindowedFixture(RadicalRedFormat.CfruWindows);
        Assert.True(SaveParser.IsPokemonRadicalRed(fixture));
        Assert.False(SaveParser.IsPokemonUnbound(fixture));
    }

    [Fact]
    public void VanillaFireRedWindowsAreNotRadicalRed()
    {
        // A save checksummed with the vintage FRLG table fails the CFRU table outright.
        var fixture = WindowedFixture(RadicalRedFormat.VanillaWindows);
        Assert.False(SaveParser.IsPokemonRadicalRed(fixture));
    }

    [Fact]
    public void UnboundSignatureIsNeverRadicalRed()
    {
        // Unbound passes the CFRU window test too; only its signature separates it.
        var fixture = WindowedFixture(RadicalRedFormat.CfruWindows);
        for (var sector = 0; sector < 28; sector++)
            BinaryPrimitives.WriteUInt32LittleEndian(fixture.AsSpan(sector * 0x1000 + 0xFF8), 0x0112_1999u);
        Assert.True(SaveParser.IsPokemonUnbound(fixture));
        Assert.False(SaveParser.IsPokemonRadicalRed(fixture));
    }

    [Fact]
    public void BlankBytesAreNotRadicalRed()
    {
        Assert.False(SaveParser.IsPokemonRadicalRed(new byte[0x20_000]));
        Assert.False(SaveParser.IsPokemonRadicalRed(new byte[0x10_000]));
    }

    [Fact]
    public void ARadicalRedSaveWithoutAPartyStillHasASaneSection()
    {
        // An unstarted save (count 0) must not be handed to the stock FRLG engine.
        var fixture = WindowedFixture(RadicalRedFormat.CfruWindows);
        foreach (var sector in new[] { 1, 15 }) // both copies of section 1
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fixture.AsSpan(sector * 0x1000 + 0x34), 0u);
            fixture.AsSpan(sector * 0x1000 + 0x38, 100).Clear(); // zeroed party slot, species 0
            var checksum = RadicalRedFormat.Checksum(fixture.AsSpan(sector * 0x1000), RadicalRedFormat.CfruWindows[1]);
            BinaryPrimitives.WriteUInt16LittleEndian(fixture.AsSpan(sector * 0x1000 + 0xFF6), checksum);
        }
        Assert.True(SaveParser.IsPokemonRadicalRed(fixture));
    }

    [Fact]
    public void AnInsanePartyRejectsDetection()
    {
        var fixture = WindowedFixture(RadicalRedFormat.CfruWindows);
        foreach (var sector in new[] { 1, 15 })
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fixture.AsSpan(sector * 0x1000 + 0x34), 9u); // > 6
            var checksum = RadicalRedFormat.Checksum(fixture.AsSpan(sector * 0x1000), RadicalRedFormat.CfruWindows[1]);
            BinaryPrimitives.WriteUInt16LittleEndian(fixture.AsSpan(sector * 0x1000 + 0xFF6), checksum);
        }
        Assert.False(SaveParser.IsPokemonRadicalRed(fixture));
    }

    [Fact]
    public void RealRadicalRedSaveIsLabeledAndOpensInTheRadicalRedSession()
    {
        var path = LocalArtifact("radicalred-champ.sav");
        if (path is null) return; // ground truth lives gitignored on the dev machine

        var bytes = File.ReadAllBytes(path);
        Assert.True(SaveParser.IsPokemonRadicalRed(bytes));
        Assert.False(SaveParser.IsPokemonUnbound(bytes));

        var engine = new SaveEngine();
        var description = engine.TryDescribe(bytes);
        Assert.NotNull(description);
        Assert.Equal("Radical Red", description!.GameName);

        using var session = engine.OpenSession(bytes);
        Assert.Equal(3, session.Generation);
        Assert.Equal(1024, session.ReadEntity(-1, 0).Species); // national id of the trainer's Terapagos
    }
}
