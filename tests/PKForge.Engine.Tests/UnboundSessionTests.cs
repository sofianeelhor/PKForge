using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Engine.Unbound;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The Unbound session against the owner's real device save: party decode, box
/// layout, checksum policies, format conversion, and the PID solver, all verified
/// on ground truth rather than fixtures.
/// </summary>
public sealed class UnboundSessionTests
{
    private static string? GroundTruth()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", "unbound-v2111.srm");
        return path is not null && File.Exists(path) ? path : null;
    }

    private static UnboundEngineSession? OpenGroundTruth()
    {
        var path = GroundTruth();
        if (path is null) return null; // gitignored ground truth: dev-only, skipped on CI
        return new UnboundEngineSession(File.ReadAllBytes(path), "Unbound");
    }

    [Fact]
    public void DuplicatePartyAndPcRetainNativeFields()
    {
        using var session = OpenGroundTruth();
        if (session is null) return;
        var source = session.ExportSlot(-1, 0).Data;
        Assert.True(session.DuplicateSlot(-1, 0, -1, 1));
        Assert.Equal(source, session.ExportSlot(-1, 0).Data);
        Assert.Equal(source, session.ExportSlot(-1, 1).Data);
        Assert.True(session.DuplicateSlot(-1, 0, 0, 0));
        var boxed = session.ExportSlot(0, 0).Data;
        Assert.True(session.DuplicateSlot(0, 0, 0, 1));
        Assert.Equal(boxed, session.ExportSlot(0, 1).Data);
        Assert.False(session.DuplicateSlot(0, 0, 0, 1));
        using var reopened = new UnboundEngineSession(session.Serialize());
        Assert.Equal(source, reopened.ExportSlot(-1, 1).Data);
        Assert.Equal(boxed, reopened.ExportSlot(0, 1).Data);
    }

    [Fact]
    public void PartyDecodesFromGroundTruth()
    {
        var session = OpenGroundTruth();
        if (session is null) return;

        Assert.Equal(3, session.Generation);
        Assert.Equal(25, session.BoxCount);

        var larvitar = session.ReadEntity(-1, 0);
        Assert.False(larvitar.IsEmpty);
        Assert.Equal(246, larvitar.Species);
        Assert.Equal("Larvitar", larvitar.SpeciesName);
        Assert.Equal("Larvitar", larvitar.Nickname);
        Assert.Equal("Sof", larvitar.OriginalTrainer);
        Assert.Equal(10, larvitar.Level);
        Assert.Equal(1, larvitar.CurrentHp);
        Assert.NotNull(larvitar.Stats);
        Assert.Equal(30, larvitar.Stats![0]); // max HP from the party tail
        Assert.True(session.ReadEntity(-1, 1).IsEmpty);
    }

    [Fact]
    public void NicknameEditMirrorsToEveryPartyCopyAndFixesChecksums()
    {
        var session = OpenGroundTruth();
        if (session is null) return;
        session.ApplyEdit(-1, 0, new EntityEdit(Nickname: "ROCKY"));

        var bytes = session.Serialize().ToArray();
        var copies = 0;
        for (var sector = 0; sector < UnboundFormat.SectorCount; sector++)
        {
            var off = sector * UnboundFormat.SectorSize;
            if (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 0xFF4)) != 1) continue;
            copies++;
            var mon = new UnboundMon(bytes, off + UnboundFormat.PartyOffset, party: true);
            Assert.Equal("ROCKY", mon.Nickname);
            var stored = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 0xFF6));
            var computed = UnboundFormat.Checksum(bytes.AsSpan(off, 0x1000), 0xFF4);
            Assert.Equal(computed, stored);
        }
        Assert.True(copies >= 2); // both rotating halves carry section 1
        Assert.True(SaveParser.IsPokemonUnbound(bytes));
    }

    [Fact]
    public void PartyMonMovesIntoTheBoxStreamWithValidChecksums()
    {
        var session = OpenGroundTruth();
        if (session is null) return;
        session.MoveSlot(-1, 0, 0, 0);

        Assert.True(session.ReadEntity(-1, 0).IsEmpty); // party compacted away
        var boxed = session.ReadEntity(0, 0);
        Assert.False(boxed.IsEmpty);
        Assert.Equal(246, boxed.Species);
        Assert.Equal("Larvitar", boxed.Nickname);

        var bytes = session.Serialize().ToArray();
        Assert.True(SaveParser.IsPokemonUnbound(bytes));

        // Every live stream section must carry a checksum that matches its content.
        var reloaded = new UnboundEngineSession(bytes);
        Assert.Equal(246, reloaded.ReadEntity(0, 0).Species);
    }

    [Fact]
    public void BoxToPartyRoundTripsThroughPk3()
    {
        var session = OpenGroundTruth();
        if (session is null) return;
        session.MoveSlot(-1, 0, 5, 3);
        var export = session.ExportSlot(5, 3);
        Assert.EndsWith(".pk3", export.FileName);

        var second = OpenGroundTruth();
        if (second is null) return;
        Assert.False(second.ImportSlot(2, 7, new byte[8])); // garbage never imports
        Assert.True(second.ImportSlot(2, 7, export.Data));
        var imported = second.ReadEntity(2, 7);
        Assert.Equal(246, imported.Species);
        Assert.Equal("Larvitar", imported.Nickname);
    }

    [Fact]
    public void NatureRerollKeepsIdentityUnderCfruShinyRule()
    {
        var session = OpenGroundTruth();
        if (session is null) return;
        session.ApplyEdit(-1, 0, new EntityEdit(IsShiny: true));
        var shiny = session.ReadEntity(-1, 0);
        Assert.True(shiny.IsShiny);

        session.ApplyEdit(-1, 0, new EntityEdit(Nature: (shiny.Nature + 7) % 25));
        var rerolled = session.ReadEntity(-1, 0);
        Assert.Equal((shiny.Nature + 7) % 25, rerolled.Nature);
        Assert.True(rerolled.IsShiny);
    }

    [Fact]
    public void UnsupportedFeaturesFailWithHonestMessages()
    {
        var session = OpenGroundTruth();
        if (session is null) return;
        Assert.Throws<NotSupportedException>(() => session.SortBoxes(SortCriteria.DexNumber));
        Assert.False(session.SupportsCompassSettings);
    }

    [Fact]
    public void BagReadsGroundTruth()
    {
        var session = OpenGroundTruth();
        if (session is null) return;
        var bag = session.GetBag();

        Assert.Equal(4, bag.Count);
        var items = bag.First(p => p.Name == "Items");
        Assert.Equal(13, items.Items[0].Id);   // Potion x2
        Assert.Equal(2, items.Items[0].Count);
        Assert.Equal(86, items.Items[1].Id);   // Repel x2
        var balls = bag.First(p => p.Name == "Balls");
        Assert.Equal(4, balls.Items[0].Id);    // Poké Ball x10
        Assert.Equal(10, balls.Items[0].Count);
        Assert.Empty(bag.First(p => p.Name == "TMs").Items);
    }

    [Fact]
    public void BagEditMirrorsItemPocketAndFixesChecksums()
    {
        var session = OpenGroundTruth();
        if (session is null) return;
        var stored = session.SetItemCount("Items", 13, 50);
        Assert.Equal(50, stored);

        var bytes = session.Serialize().ToArray();
        foreach (var copy in new[] { 0x1000, 0x10000 }) // both rotating section-13 copies
        {
            var id = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(copy + 0xAD8));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(copy + 0xAD8 + 2));
            Assert.Equal(13, id);
            Assert.Equal(50, count);
            var storedChecksum = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(copy + 0xFF6));
            Assert.Equal(UnboundFormat.Checksum(bytes.AsSpan(copy, 0x450), 0x450), storedChecksum);
        }

        var reloaded = new UnboundEngineSession(bytes);
        Assert.Equal(50, reloaded.GetBag().First(p => p.Name == "Items").Items[0].Count);
    }

    [Fact]
    public void BagAppendRemoveAndBallPocketEditsRoundTrip()
    {
        var session = OpenGroundTruth();
        if (session is null) return;
        Assert.Equal(5, session.SetItemCount("Items", 25, 5));      // appended past the run
        Assert.Equal(99, session.SetItemCount("Balls", 4, 99));     // fixed-sector pocket
        Assert.Equal(0, session.SetItemCount("Items", 25, 0));      // removal compacts the run

        var reloaded = new UnboundEngineSession(session.Serialize().ToArray());
        var items = reloaded.GetBag().First(p => p.Name == "Items").Items;
        Assert.DoesNotContain(items, item => item.Id == 25);
        Assert.Equal(2, items.Count); // Potion + Repel remain
        Assert.Equal(99, reloaded.GetBag().First(p => p.Name == "Balls").Items[0].Count);
    }

    [Fact]
    public void PouchLegalItemsFollowTheFamilies()
    {
        var session = OpenGroundTruth();
        if (session is null) return;
        Assert.Contains(4, session.GetPouchLegalItems("Balls"));
        Assert.Contains(622, session.GetPouchLegalItems("Balls"));  // Fast Ball item id
        Assert.DoesNotContain(4, session.GetPouchLegalItems("Items"));
        Assert.NotEmpty(session.GetPouchLegalItems("TMs"));
    }

    [Fact]
    public void PlainSpeciesShowdownSetGeneratesToo()
    {
        var session = OpenGroundTruth();
        if (session is null) return;
        var outcome = new LegalizerService().GenerateFromShowdown(session, 0, 0, "Larvitar");
        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal(246, session.ReadEntity(0, 0).Species);
    }

    [Fact]
    public void WizardGenerationBuildsFromRomTables()
    {
        var legalizer = new LegalizerService();
        var session = OpenGroundTruth();
        if (session is null) return;

        // Modern id 246 = Larvitar; Unbound keeps it, but the resolution must go by name.
        var outcome = legalizer.Generate(session, 0, 1,
            new GenerationRequest(246, 50, Shiny: true, Nature: 8, Ability: null, Ball: 4, Moves: [44, 43]));
        Assert.True(outcome.Success, outcome.Message);

        var generated = session.ReadEntity(0, 1);
        Assert.Equal(246, generated.Species);
        Assert.Equal(50, generated.Level);
        Assert.True(generated.IsShiny);
        Assert.Equal(8, generated.Nature);
        Assert.Equal(44, generated.Move1); // Bite
    }

    [Fact]
    public void GenerationResolvesDivergentSpeciesIdsByName()
    {
        var legalizer = new LegalizerService();
        var session = OpenGroundTruth();
        if (session is null) return;

        // Sneasler: modern id 903, Unbound ROM id 1256. Only name resolution lands it.
        var outcome = legalizer.Generate(session, 1, 0, new GenerationRequest(903, 30, false, null, null, null, null));
        Assert.True(outcome.Success, outcome.Message);
        var generated = session.ReadEntity(1, 0);
        Assert.Equal(1256, generated.Species);
        Assert.Equal("Sneasler", generated.SpeciesName);
    }

    [Fact]
    public void ShowdownPasteGeneratesAndPartyAppendFillsUp()
    {
        var legalizer = new LegalizerService();
        var session = OpenGroundTruth();
        if (session is null) return;

        var outcome = legalizer.GenerateFromShowdown(session, -1, 0,
            "Larvitar\nLevel: 25\nAdamant Nature\n- Bite\n- Leer");
        Assert.True(outcome.Success, outcome.Message);

        var party = session.ReadEntity(-1, 1);
        Assert.False(party.IsEmpty);
        Assert.Equal(246, party.Species);
        Assert.Equal(25, party.Level);
        Assert.NotNull(party.Stats);

        var bytes = session.Serialize().ToArray();
        var reloaded = new UnboundEngineSession(bytes);
        Assert.Equal(246, reloaded.ReadEntity(-1, 1).Species);
        Assert.True(SaveParser.IsPokemonUnbound(bytes));
    }
}
