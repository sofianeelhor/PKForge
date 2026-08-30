using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Luminescent Platinum uses BDSP's v1.3 layout but marks its save revision
/// FFFF0134. This fixture is generated locally so no player's save is checked in.
/// </summary>
public sealed class LuminescentPlatinumTests
{
    private const uint Revision13Rev1 = 0xFFFF0134;

    private static byte[] LuminescentSave()
    {
        var save = new SAV8BS { OT = "PKForge", Version = GameVersion.BD, TID16 = 12345, SID16 = 54321 };
        var bytes = save.Write().ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Revision13Rev1);
        // Recalculate the shared BDSP MD5 after changing the revision, exactly as
        // PKForge does when it writes a Luminescent candidate.
        return new SAV8BS(bytes).Write().ToArray();
    }

    [Fact]
    public void RecognizesTheReportedLuminescentRevision()
    {
        var bytes = LuminescentSave();

        Assert.True(SaveUtil.TryGetSaveFile(bytes, out var save));
        Assert.IsType<SAV8BSLuminescent>(save);

        var description = new SaveEngine().TryDescribe(bytes);

        Assert.NotNull(description);
        Assert.Equal("Luminescent Platinum", description.GameName);
        Assert.Equal(8, description.Generation);
    }

    [Fact]
    public void OpenSerializePreservesLuminescentHeaderAndRemainsValid()
    {
        var bytes = LuminescentSave();
        var engine = new SaveEngine();

        using var session = engine.OpenSession(bytes);
        var serialized = session.Serialize();

        Assert.Equal(Revision13Rev1, BinaryPrimitives.ReadUInt32LittleEndian(serialized.Span));
        Assert.True(serialized.Span.SequenceEqual(bytes));
        Assert.True(engine.Validate(serialized));
    }

    [Theory]
    [InlineData(0x00000034u)]
    public void DoesNotMistakeRetailRevisionForLuminescent(uint revision)
    {
        var bytes = LuminescentSave();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, revision);

        var description = new SaveEngine().TryDescribe(bytes);

        Assert.NotNull(description);
        Assert.NotEqual("Luminescent Platinum", description.GameName);
    }

    [Fact]
    public void RealFixtureRoundTripsWhenExplicitlyProvided()
    {
        var path = Environment.GetEnvironmentVariable("PKFORGE_LUMI_FIXTURE");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return; // Player saves are intentionally never checked into the repository.

        var original = File.ReadAllBytes(path);
        Assert.True(SaveUtil.TryGetSaveFile(original, out var save));
        Assert.IsType<SAV8BSLuminescent>(save);
        // Luminescent writes a different checksum than retail BDSP. A no-op open must
        // preserve its original bytes rather than "repairing" that mod-specific hash.

        using var session = new SaveEngine().OpenSession(original);
        Assert.True(session.Serialize().Span.SequenceEqual(original));
    }

    [Fact]
    public void GeneratorCanCreateAStarterInLuminescent()
    {
        using var session = new SaveEngine().OpenSession(LuminescentSave());
        var outcome = new LegalizerService().Generate(session, 0, 0,
            new GenerationRequest(25, 20, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null));

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(25, session.ReadEntity(0, 0).Species);
    }

    [Fact]
    public void LuminescentExclusiveItemsHaveNamesPouchesAndRoundTrip()
    {
        using var session = new SaveEngine().OpenSession(LuminescentSave());
        Assert.Contains(1823, session.GetPouchLegalItems("KeyItems"));
        Assert.Equal("GS Ball", session.GetItemNames()[1823]);

        session.SetItemCount("KeyItems", 1823, 1);
        var saved = session.Serialize();
        using var reloaded = new SaveEngine().OpenSession(saved);
        Assert.Contains(reloaded.GetBag().Single(p => p.Name == "KeyItems").Items, i => i.Id == 1823 && i.Count == 1);
    }

    [Fact]
    public void LuminescentDexUsesItsPackedExpandedLayout()
    {
        using var session = new SaveEngine().OpenSession(LuminescentSave());
        session.SetDexEntry(494, seen: true, caught: true);
        var saved = session.Serialize().ToArray();

        const int dex = 0x7A328;
        Assert.Equal(3, saved[dex + 493 / 2] >> 4); // species 494 is the high nibble
        using var reloaded = new SaveEngine().OpenSession(saved);
        Assert.True(reloaded.GetDexEntry(494).Caught);
    }
}
