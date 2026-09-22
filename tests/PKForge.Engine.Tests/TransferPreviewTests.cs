using PKForge.Domain;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The pre-flight diff behind transfer confirmation: a dry-run import into a throwaway
/// session must report exactly what the conversion changes, carry a legality verdict,
/// and never disturb the save the caller holds. Gen5→Gen7 is this engine's most
/// conservative route (move IDs and the PID are stable across it), so its compromise
/// surfaces as ability re-derivation and egg hatching; older sources rewrite the
/// identity itself (PID, IVs, ball, met data).
/// </summary>
public sealed class TransferPreviewTests
{
    private static string CorpusPath(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", file);
    }

    private static string Fixture(string relative) => CorpusPath(Path.Combine("Legality", "Legal", relative));

    private static byte[] SunBytes() => File.ReadAllBytes(CorpusPath(Path.Combine("TestData", "SM Project 802.main")));

    private static (SaveEngineSession Scratch, int Box, int Slot) SunScratch()
    {
        var scratch = new SaveEngineSession(SunBytes());
        var landing = scratch.Snapshot.Slots.First(s => s.Species is null);
        return (scratch, landing.Box, landing.Slot);
    }

    private static TransferPreview? PreviewInSun(byte[] entityBytes)
    {
        var (scratch, box, slot) = SunScratch();
        using (scratch)
            return new TransferPreviewService(new LegalityService()).Preview(scratch, box, slot, entityBytes);
    }

    [Fact]
    public void SameGenerationTransferHasAnEmptyDiff()
    {
        var bytes = File.ReadAllBytes(Fixture(Path.Combine("Generation 7 Wild", "744 - Rockruff - B9D885FF60E2.pk7")));

        var preview = PreviewInSun(bytes);

        Assert.NotNull(preview);
        Assert.Empty(preview.Changes);
        Assert.Equal(TransferLegality.Legal, preview.Legality);
    }

    [Fact]
    public void Gen5ToGen7ReportsAbilityRederivationButKeepsTheIdentity()
    {
        var bytes = File.ReadAllBytes(Fixture(Path.Combine("Generation 5", "0550-01 - Basculin - C46358B4FA76 wrongAbility.pk5")));

        var preview = PreviewInSun(bytes);

        Assert.NotNull(preview);
        // Gen5→Gen7 keeps the PID and move list verbatim; the one compromise on this
        // route is the ability, which the target game re-derives from its own tables.
        Assert.Contains(preview.Changes, c => c.StartsWith("Ability:", StringComparison.Ordinal));
        Assert.DoesNotContain(preview.Changes, c => c.StartsWith("PID", StringComparison.Ordinal));
        Assert.DoesNotContain(preview.Changes, c => c.StartsWith("Moves:", StringComparison.Ordinal));
    }

    [Fact]
    public void Gen5EggArrivesHatched()
    {
        var bytes = File.ReadAllBytes(Fixture(Path.Combine("Generation 5", "636 - Egg - F2C5552258BA Breeder Larvesta.pk5")));

        var preview = PreviewInSun(bytes);

        Assert.NotNull(preview);
        Assert.Contains(preview.Changes, c => c.StartsWith("Egg hatches", StringComparison.Ordinal));
    }

    [Fact]
    public void Gen1ToGen7ReportsIdentityMetAndPresentationRewrites()
    {
        var bytes = File.ReadAllBytes(Fixture(Path.Combine("Generation 1 Only", "135 - JOLTEON - 7E45.pk1")));

        var preview = PreviewInSun(bytes);

        Assert.NotNull(preview);
        Assert.Contains(preview.Changes, c => c.StartsWith("PID created:", StringComparison.Ordinal));
        Assert.Contains(preview.Changes, c => c.StartsWith("IVs rewritten:", StringComparison.Ordinal));
        Assert.Contains(preview.Changes, c => c.StartsWith("Met location:", StringComparison.Ordinal));
        Assert.Contains(preview.Changes, c => c.StartsWith("Met level:", StringComparison.Ordinal));
        Assert.Contains(preview.Changes, c => c.StartsWith("Nickname:", StringComparison.Ordinal));
        Assert.Contains(preview.Changes, c => c.StartsWith("Origin game:", StringComparison.Ordinal));
    }

    [Fact]
    public void IllegalEntitySurfacesAFailedVerdict()
    {
        var bytes = File.ReadAllBytes(Fixture(Path.Combine("Generation 7 Wild", "744 - Rockruff - B9D885FF60E2.pk7")));
        var entity = EntityFormat.GetFromBytes(bytes, EntityContext.Gen7)!;
        entity.Move1 = 459; // Roar of Time: no Rockruff can ever know it
        entity.Move1_PP = 5;
        var crafted = new byte[entity.SIZE_PARTY];
        entity.WriteDecryptedDataParty(crafted);

        var preview = PreviewInSun(crafted);

        Assert.NotNull(preview);
        Assert.Equal(TransferLegality.Illegal, preview.Legality);
        Assert.NotEmpty(preview.LegalityLines);
    }

    [Fact]
    public void UnconvertibleTransferReportsNoRoute()
    {
        var bytes = File.ReadAllBytes(Fixture(Path.Combine("Generation 7 Wild", "744 - Rockruff - B9D885FF60E2.pk7")));
        using var blank = new SaveEngine().OpenBlankSession(5);
        var landing = blank.Snapshot.Slots.First(s => s.Species is null);

        // The engine has no backwards route out of Gen 7; the preview must report the
        // same refusal a real transfer would, not an empty diff.
        var preview = new TransferPreviewService(new LegalityService()).Preview(blank, landing.Box, landing.Slot, bytes);

        Assert.Null(preview);
    }

    [Fact]
    public void PreviewLeavesTheSourceSaveBytesUntouched()
    {
        var fileBytes = SunBytes();
        using var live = new SaveEngineSession(fileBytes);
        var before = live.Serialize();

        var bytes = File.ReadAllBytes(Fixture(Path.Combine("Generation 1 Only", "135 - JOLTEON - 7E45.pk1")));
        var preview = PreviewInSun(bytes);
        Assert.NotNull(preview);

        var after = live.Serialize();
        Assert.True(before.Span.SequenceEqual(after.Span), "the dry-run must leave the caller's session byte-identical");
        Assert.True(after.Span.SequenceEqual(fileBytes), "the dry-run must leave the source file's bytes untouched");
    }
}
