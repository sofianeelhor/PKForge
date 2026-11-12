using PKForge.Domain;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Recharged Yellow repacks SaveBlock1: party count at 0x3B, party at 0x44,
/// money at 0x29C, six bag pockets with their own capacities. The ground-truth
/// save is the user's v1.9 file (kept out of the repository).
/// </summary>
public sealed class RechargedYellowTests
{
    private static string? LocalArtifact(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", file);
        return path is not null && File.Exists(path) ? path : null;
    }

    private static byte[]? Sample() => LocalArtifact(Path.Combine("romhacks", "recharged-yellow.sav")) is { } path
        ? File.ReadAllBytes(path)
        : null;

    [Fact]
    public void RealSaveIsRecognisedAndItsPartyIsReadable()
    {
        var bytes = Sample();
        if (bytes is null) return;

        Assert.True(SaveParser.IsPokemonRechargedYellow(bytes));

        using var session = new SaveEngine().OpenSession(bytes);
        Assert.IsType<RechargedYellow.RechargedYellowEngineSession>(session);
        Assert.Equal(3, session.Generation);

        var party = session.Snapshot.Slots.Where(s => s.Box == -1).ToList();
        Assert.Equal(new int?[] { 25, 1, 4, 7 }, party.Select(s => s.Species).ToArray());

        var first = session.ReadEntity(-1, 0);
        Assert.False(first.IsEmpty);
        Assert.Equal(25, first.Species);
        Assert.Equal("PIKACHU", first.Nickname);
        Assert.Equal(31, first.Level);
        Assert.Equal(74, first.Stats?[0] ?? 0);

        Assert.True(RechargedYellow.RechargedYellowFormat.TryGetLayout(SramPadding.Trim(bytes), out _, out _, out var profile));
        Assert.StartsWith("Recharged Yellow 1.9", profile.Name, StringComparison.Ordinal);

        var description = new SaveEngine().TryDescribe(bytes);
        Assert.NotNull(description);
        Assert.Equal("Recharged Yellow", description.GameName);
        var guess = SaveIdentityRules.Guess(description.GameName, description.Generation, "save.sav", null, "Save");
        Assert.Equal("Pokémon Recharged Yellow", guess.Label);
        Assert.Equal(SaveFormat.RechargedYellow, guess.Format);
        Assert.Equal(SaveLayoutFamily.RechargedYellow, guess.Family);
    }

    [Fact]
    public void BoxMonsAndNamesSurviveTheRepack()
    {
        var bytes = Sample();
        if (bytes is null) return;

        byte[] edited;
        using (var session = new SaveEngine().OpenSession(bytes))
        {
            Assert.Equal("Box1", session.GetBoxName(0));
            var pidgey = session.ReadEntity(0, 0);
            Assert.Equal(16, pidgey.Species);
            session.ApplyEdit(0, 0, new EntityEdit(Nickname: "BIRD"));
            edited = session.Serialize().ToArray();
        }

        using var reopened = new SaveEngine().OpenSession(edited);
        Assert.Equal("BIRD", reopened.ReadEntity(0, 0).Nickname);
        Assert.Equal(16, reopened.ReadEntity(0, 0).Species);
        Assert.Equal("Box1", reopened.GetBoxName(0));
        Assert.Equal("Box14", reopened.GetBoxName(13));

        // The edited mon lives in the hack's PC block at +4 and keeps a valid checksum.
        var payload = SramPadding.Trim(edited);
        Assert.True(RechargedYellow.RechargedYellowFormat.TryGetLayout(payload, out _, out var sections, out var profile));
        var pc = RechargedYellow.RechargedYellowFormat.ReadBlock(payload, sections, 5, profile.PcChunks);
        Assert.True(RechargedYellow.RechargedYellowFormat.IsValidStoredMon(pc.AsSpan(4, 80)));
    }

    [Fact]
    public void RecognisedSaveIsNotASuspectedHackAndAPartyEditPassesWriteSafety()
    {
        var bytes = Sample();
        if (bytes is null) return;

        var engine = new SaveEngine();
        Assert.Null(engine.AssessLayoutRisk(bytes));

        using var session = engine.OpenSession(bytes);
        session.ApplyEdit(-1, 0, new EntityEdit(Level: 40));
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "BIRD"));
        var candidate = session.Serialize();
        Assert.Null(engine.CheckWriteSafety(bytes, candidate, null));
    }

    [Fact]
    public void FullSixMonPartyIsStillRecognised()
    {
        var bytes = Sample();
        if (bytes is null) return;

        var payload = SramPadding.Trim(bytes);
        Assert.True(RechargedYellow.RechargedYellowFormat.TryGetLayout(payload, out _, out var sections, out var profile));
        var sb1 = RechargedYellow.RechargedYellowFormat.ReadBlock(payload, sections, 1, profile.Sb1Chunks);
        var record = sb1.AsSpan(RechargedYellow.RechargedYellowFormat.PartyOffset, 100).ToArray();
        record.CopyTo(sb1.AsSpan(RechargedYellow.RechargedYellowFormat.PartyOffset + (4 * 100), 100));
        record.CopyTo(sb1.AsSpan(RechargedYellow.RechargedYellowFormat.PartyOffset + (5 * 100), 100));
        sb1[RechargedYellow.RechargedYellowFormat.PartyCountOffset] = 6;
        // Hack slot 5 starts at retail 0x238; make that retail count look real so
        // the old detector would have rejected the save.
        sb1[RechargedYellow.RechargedYellowFormat.VanillaPartyCountOffset] = 3;
        RechargedYellow.RechargedYellowFormat.WriteBlock(payload, sections, 1, profile.Sb1Chunks, sb1);
        RechargedYellow.RechargedYellowFormat.WriteChecksums(payload, sections, profile.Windows);

        Assert.True(SaveParser.IsPokemonRechargedYellow(payload));
        using var session = new SaveEngine().OpenSession(payload);
        Assert.Equal(6, session.Snapshot.Slots.Count(x => x.Box == -1));
        Assert.Equal(25, session.ReadEntity(-1, 0).Species);
        Assert.Equal(25, session.ReadEntity(-1, 5).Species);
    }

    [Fact]
    public void TornNewestSlotFallsBackToTheValidatedOlderSlot()
    {
        var bytes = Sample();
        if (bytes is null) return;

        var torn = (byte[])bytes.Clone();
        torn[0xE000] ^= 0xFF; // break one checksum in the newest half
        Assert.True(SaveParser.IsPokemonRechargedYellow(torn));

        using var session = new SaveEngine().OpenSession(torn);
        Assert.Equal(25, session.ReadEntity(-1, 0).Species);
        Assert.Equal(5, session.ReadEntity(-1, 0).Level);

        // The torn half is not the validated slot, so it must stay byte-identical.
        var serialized = session.Serialize().ToArray();
        Assert.True(serialized.AsSpan(0xE000, 0x1000).SequenceEqual(torn.AsSpan(0xE000, 0x1000)));
    }

    [Fact]
    public void OtherRomHackSamplesAreNotRechargedYellow()
    {
        foreach (var file in new[]
                 {
                     "quetzal-9.1.sav", "quetzal-bills.sav", "soulgold.sav", "heart-and-soul.sav",
                     "lazarus.sav", "emerald-seaglass.sav", "emerald-enhanced.sav", "perfect-emerald.sav",
                 })
        {
            var path = LocalArtifact(Path.Combine("romhacks", file));
            if (path is null) continue;
            Assert.False(SaveParser.IsPokemonRechargedYellow(File.ReadAllBytes(path)), file);
        }
    }

    [Fact]
    public void UnchangedSaveRoundTripsByteForByte()
    {
        var bytes = Sample();
        if (bytes is null) return;

        using var session = new SaveEngine().OpenSession(bytes);
        Assert.True(session.Serialize().Span.SequenceEqual(bytes));
        Assert.True(new SaveEngine().Validate(bytes));
    }

    [Fact]
    public void PartyEditIsWrittenToTheHackOffsets()
    {
        var bytes = Sample();
        if (bytes is null) return;

        byte[] edited;
        using (var session = new SaveEngine().OpenSession(bytes))
        {
            session.ApplyEdit(-1, 0, new EntityEdit(Level: 50));
            edited = session.Serialize().ToArray();
        }

        Assert.False(edited.AsSpan().SequenceEqual(bytes));
        // Only the newest slot changed; the game's backup slot and the four extra
        // sectors stay byte-identical.
        Assert.True(edited.AsSpan(0, 0xE000).SequenceEqual(bytes.AsSpan(0, 0xE000)));
        Assert.True(edited.AsSpan(0x1C000).SequenceEqual(bytes.AsSpan(0x1C000)));
        using var reopened = new SaveEngine().OpenSession(edited);
        Assert.Equal(50, reopened.ReadEntity(-1, 0).Level);
        Assert.Equal(25, reopened.ReadEntity(-1, 0).Species);

        // The hack owns the party; the retail offsets PKHeX used before stay empty.
        var payload = SramPadding.Trim(edited);
        Assert.True(RechargedYellow.RechargedYellowFormat.TryGetLayout(payload, out _, out var sections, out var profile));
        var sb1 = RechargedYellow.RechargedYellowFormat.ReadBlock(payload, sections, 1, profile.Sb1Chunks);
        Assert.Equal(4, sb1[RechargedYellow.RechargedYellowFormat.PartyCountOffset]);
        // The retail byte is the save's own coincidental 5 (empty party slot 4);
        // PKForge never treats it as the party.
        Assert.Equal(5, sb1[RechargedYellow.RechargedYellowFormat.VanillaPartyCountOffset]);
        Assert.True(RechargedYellow.RechargedYellowFormat.IsValidStoredMon(
            sb1.AsSpan(RechargedYellow.RechargedYellowFormat.PartyOffset, 80)));
    }

    [Fact]
    public void MoneyAndBagUseTheHackLayout()
    {
        var bytes = Sample();
        if (bytes is null) return;

        using (var session = new SaveEngine().OpenSession(bytes))
        {
            Assert.Equal(3000u, session.GetTrainer().Money);
            var medicine = session.GetBag().Single(p => p.Name == "Medicine");
            Assert.Empty(medicine.Items);
            var sampleBalls = session.GetBag().Single(p => p.Name == "Balls");
            Assert.Contains(sampleBalls.Items, item => item is { Id: 4, Count: 8 });
            var key = session.GetBag().Single(p => p.Name == "KeyItems");
            Assert.Contains(key.Items, item => item is { Id: 361, Count: 1 });

            session.SetTrainer(session.GetTrainer() with { Money = 1_234_567 });
            session.SetItemCount("Balls", 1, 12);
        }

        // Serialize from a second session that made the edits, then reopen.
        using var edit = new SaveEngine().OpenSession(bytes);
        edit.SetTrainer(edit.GetTrainer() with { Money = 1_234_567 });
        edit.SetItemCount("Balls", 1, 12);
        var changed = edit.Serialize().ToArray();

        using var reopened = new SaveEngine().OpenSession(changed);
        Assert.Equal(1_234_567u, reopened.GetTrainer().Money);
        var balls = reopened.GetBag().Single(p => p.Name == "Balls");
        Assert.Contains(balls.Items, item => item is { Id: 1, Count: 12 });
    }
}
