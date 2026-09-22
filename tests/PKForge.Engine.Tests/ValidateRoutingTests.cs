using System.Buffers.Binary;
using PKForge.Engine;
using PKForge.Engine.RadicalRed;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// <see cref="SaveEngine.Validate"/> is the gate every write passes through
/// (SafeSaveWriter), so it must accept exactly the bytes Open/OpenSession would
/// reopen — including the romhack saves stock PKHeX cannot parse (Unbound's CFRU
/// sector signature) or would misparse under the wrong engine (Radical Red), and
/// the RetroArch containers those saves ride in. The synthetic fixtures mirror the
/// detection suites; the ground-truth device saves run when they are present.
/// </summary>
public sealed class ValidateRoutingTests
{
    private static string? LocalArtifact(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", file);
        return path is not null && File.Exists(path) ? path : null;
    }

    /// <summary>The same windowed FRLG-shaped fixture as the detection suite: both rotating
    /// slots stamped, checksums computed over the given window table (see
    /// RadicalRedDetectionTests for why every byte is nonzero).</summary>
    private static byte[] WindowedFixture(ushort[] windows, uint signature = 0x0801_2025u)
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
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0xFF8), signature);
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(off + 0xFFC), slot == 0 ? 10u : 11u);
                var checksum = RadicalRedFormat.Checksum(data.AsSpan(off, windows[id]), windows[id]);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(off + 0xFF6), checksum);
            }
        }
        return data;
    }

    [Fact]
    public void UnboundRetroArchContainerValidatesThroughTheUnboundRoute()
    {
        // PKHeX rejects Unbound's 0x01121999 sector signatures outright, so before the
        // routing fix this container failed Validate even though OpenSession opens it.
        var inner = WindowedFixture(RadicalRedFormat.CfruWindows, SaveParser.UnboundSectorSignature);
        var container = RetroArchSaveContainer.Encode(inner);
        Assert.True(RetroArchSaveContainer.IsCompressed(container));
        Assert.True(SaveParser.IsPokemonUnbound(RetroArchSaveContainer.Decode(container)));

        var engine = new SaveEngine();
        Assert.True(engine.Validate(container));
        // The session's Serialize output is exactly the shape SafeSaveWriter writes back.
        using var session = engine.OpenSession(container);
        Assert.True(engine.Validate(session.Serialize()));
    }

    [Fact]
    public void RadicalRedSaveValidates()
    {
        var fixture = WindowedFixture(RadicalRedFormat.CfruWindows);
        Assert.True(SaveParser.IsPokemonRadicalRed(fixture));
        Assert.True(new SaveEngine().Validate(fixture));
    }

    [Fact]
    public void RadicalRedGroundTruthSerializeOutputValidates()
    {
        var path = LocalArtifact("radicalred-champ.sav");
        if (path is null) return; // ground truth lives gitignored on the dev machine

        var engine = new SaveEngine();
        using var session = engine.OpenSession(File.ReadAllBytes(path), "Radical Red");
        session.ApplyEdit(-1, 0, new Domain.EntityEdit(Nickname: "HEXEDIT"));

        // The hex editor commits Serialize()-shaped bytes; those must clear the gate.
        var candidate = session.Serialize();
        Assert.True(engine.Validate(candidate));
        using var reopened = engine.OpenSession(candidate);
        Assert.Equal("HEXEDIT", reopened.ReadEntity(-1, 0).Nickname);
    }

    [Fact]
    public void UnboundGroundTruthSerializeOutputValidates()
    {
        var path = LocalArtifact("unbound-v2111.srm");
        if (path is null) return; // ground truth lives gitignored on the dev machine

        var engine = new SaveEngine();
        var bytes = File.ReadAllBytes(path);
        Assert.True(engine.Validate(bytes), "the untouched .srm container must validate");

        using var session = engine.OpenSession(bytes, "Unbound");
        session.ApplyEdit(-1, 0, new Domain.EntityEdit(Nickname: "HEXEDIT"));
        // The owner's .srm is a raw 0x20000 battery dump; Serialize keeps that shape
        // (the compressed-container shape is covered by the synthetic test above).
        var candidate = session.Serialize();
        Assert.False(RetroArchSaveContainer.IsCompressed(candidate.Span));
        Assert.Equal(0x20_000, candidate.Length);
        Assert.True(engine.Validate(candidate));
        using var reopened = engine.OpenSession(candidate);
        Assert.Equal("HEXEDIT", reopened.ReadEntity(-1, 0).Nickname);
    }

    [Fact]
    public void VanillaFireRedGroundTruthValidates()
    {
        var path = LocalArtifact("firered-vanilla.sav");
        if (path is null) return;

        var bytes = File.ReadAllBytes(path);
        Assert.False(SaveParser.IsPokemonUnbound(bytes));
        Assert.False(SaveParser.IsPokemonRadicalRed(bytes));
        Assert.True(new SaveEngine().Validate(bytes));
    }

    [Fact]
    public void Gen9GroundTruthValidates()
    {
        // Stock-path control: the modern format that never touches the romhack routes.
        var path = LocalArtifact("violet-indigo-disk-main");
        if (path is null) return;

        var engine = new SaveEngine();
        using var session = engine.OpenSession(File.ReadAllBytes(path), "violet-indigo-disk-main");
        Assert.Equal(9, session.Generation);
        Assert.True(engine.Validate(session.Serialize()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(0x20_000)]
    public void GarbageIsRejected(int length)
    {
        var garbage = new byte[length];
        new Random(7).NextBytes(garbage);
        Assert.False(new SaveEngine().Validate(garbage));
    }

    [Fact]
    public void TruncatedContainerIsRejected()
    {
        var container = RetroArchSaveContainer.Encode(WindowedFixture(RadicalRedFormat.CfruWindows));
        var cut = container.AsSpan(0, container.Length - 1).ToArray();
        Assert.False(new SaveEngine().Validate(cut));
    }
}
