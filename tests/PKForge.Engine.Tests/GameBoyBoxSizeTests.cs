using PKForge.Domain;
using PKForge.Infrastructure;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// "When I select multiple Pokémon to send them to the Bank the app crashes, I can't use box
/// management and it won't let me send the auto dex" (Pokémon Yellow, 3.1.1). A Game Boy box
/// holds 20, not 30, and keeps its Pokémon as a list that closes up behind whoever leaves.
/// These run the paths that broke on 20-slot Gen 1 and Gen 2 boxes.
/// </summary>
public sealed class GameBoyBoxSizeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-gb-" + Guid.NewGuid().ToString("N"));
    private readonly SaveEngine _engine = new();
    private const string SaveId = "content://saves/yellow.sav";
    private const ushort Pikachu = 25, Pidgey = 16, Rattata = 19, Caterpie = 10;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static PKM Mon(SaveFile save, ushort species, int level)
    {
        var mon = save.BlankPKM;
        mon.Species = species;
        mon.CurrentLevel = (byte)level;
        mon.OriginalTrainerName = save.OT;
        mon.TID16 = save.TID16;
        mon.Nickname = SpeciesName.GetSpeciesNameGeneration(species, (int)LanguageID.English, save.Generation);
        mon.Move1 = 33; // Tackle
        mon.RefreshChecksum();
        return mon;
    }

    /// <summary>A save with a party of <paramref name="party"/> and the given box Pokémon packed from slot 0.</summary>
    private static byte[] File(GameVersion version, ushort[] party, params (int Box, ushort Species)[] mons)
    {
        var save = BlankSaveFile.Get(version, "ASH", LanguageID.English);
        for (var i = 0; i < party.Length; i++)
            save.SetPartySlotAtIndex(Mon(save, party[i], 10 + i), i, EntityImportSettings.None);
        var next = new Dictionary<int, int>();
        foreach (var (box, species) in mons)
        {
            var slot = next.GetValueOrDefault(box);
            next[box] = slot + 1;
            save.SetBoxSlotAtIndex(Mon(save, species, 5 + slot), box, slot, EntityImportSettings.None);
        }
        return save.Write().ToArray();
    }

    /// <summary>Box 1 full (species 1-20), box 2 with 1, 4, 7, 7.</summary>
    private static byte[] FullBoxFile(GameVersion version) =>
        File(version, [Pikachu], [.. Enumerable.Range(1, 20).Select(s => (0, (ushort)s)), (1, 1), (1, 4), (1, 7), (1, 7)]);

    private static int[] Box(ISaveEngineSession session, int box) =>
        [.. session.Snapshot.Slots.Where(s => s.Box == box).OrderBy(s => s.Slot).Select(s => s.Species ?? 0)];

    private static int[] Party(ISaveEngineSession session) =>
        [.. Enumerable.Range(0, 6).Select(i => session.ReadEntity(-1, i)).Where(m => !m.IsEmpty).Select(m => m.Species)];

    private sealed class MemoryAccess : ISaveFileAccess
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(Files[documentId].ToArray());
        public ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Files[documentId] = bytes.ToArray();
            return ValueTask.CompletedTask;
        }
    }

    [Theory]
    [InlineData(GameVersion.RD)]
    [InlineData(GameVersion.YW)]
    [InlineData(GameVersion.C)]
    public void AGameBoyBoxHoldsTwentyAndEveryOneOfThemReads(GameVersion version)
    {
        using var session = _engine.OpenSession(FullBoxFile(version));

        // What the box manager's mini map and the copy tools read: every slot of every box.
        var perBox = session.Snapshot.Slots.Where(s => s.Box >= 0).GroupBy(s => s.Box).Select(g => g.Count()).Distinct();
        Assert.Equal([20], perBox);
        foreach (var slot in session.Snapshot.Slots)
            _ = session.ReadEntity(slot.Box, slot.Slot);
        Assert.Equal(Enumerable.Range(1, 20), Box(session, 0));
        // The 21st slot does not exist: the 3.1.1 paint path asked for it and took the app down.
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ReadEntity(0, 20));
    }

    [Theory]
    [InlineData(GameVersion.YW)]
    [InlineData(GameVersion.C)]
    public void MarkedPokemonSentToTheBankLeaveTheBoxClosedUpAndNothingElse(GameVersion version)
    {
        var original = FullBoxFile(version);
        using var session = _engine.OpenSession(original);
        var bank = new FileBankService(Path.Combine(_root, "bank"));
        // Multi-select: three from the full box (first, middle, last) and two from the next one.
        (int Box, int Slot)[] marked = [(0, 0), (0, 9), (0, 19), (1, 1), (1, 2)];

        var deposits = marked.Select(m => session.ExportSlot(m.Box, m.Slot))
            .Select(export => new BankDeposit(export.Data, _engine.TryDescribeEntity(export.Data, "Yellow", export.Format)!)).ToList();
        bank.AddMany(deposits);
        SlotReleases.ReleaseAll(session, marked);
        Assert.True(session.PackListBoxes());
        var written = session.Serialize();

        Assert.Equal([1, 10, 20, 4, 7], bank.GetAll().OrderBy(e => e.Box).ThenBy(e => e.Slot).Select(e => e.Info.Species));
        Assert.Null(_engine.CheckWriteSafety(original, written, null));
        using var reopened = _engine.OpenSession(written);
        Assert.Equal([.. Enumerable.Range(2, 8), .. Enumerable.Range(11, 9), 0, 0, 0], Box(reopened, 0));
        Assert.Equal([1, 7], Box(reopened, 1).Where(s => s != 0));
        Assert.Equal([Pikachu], Party(reopened));
    }

    [Theory]
    [InlineData(GameVersion.YW)]
    [InlineData(GameVersion.B2)]
    public void SeveralPartyPokemonReleasedTogetherAreTheOnesMarked(GameVersion version)
    {
        // The party closes up after each departure: released first-to-last, the second
        // index already pointed at the Pokémon after the one marked.
        using var session = _engine.OpenSession(File(version, [Pikachu, Pidgey, Rattata, Caterpie], (0, 1)));

        SlotReleases.ReleaseAll(session, [(-1, 1), (-1, 2)]);

        Assert.Equal([Pikachu, Caterpie], Party(session));
    }

    [Theory]
    [InlineData(GameVersion.YW)]
    [InlineData(GameVersion.C)]
    public async Task TheLivingDexAutopilotSendsAGameBoySaveToTheBank(GameVersion version)
    {
        var bytes = FullBoxFile(version);
        var access = new MemoryAccess();
        access.Files[SaveId] = bytes;
        var bank = new FileBankService(Path.Combine(_root, "bank"));
        var writer = new SafeSaveWriter(_engine, new FileBackupService(Path.Combine(_root, "backups")), access);
        var executor = new LivingDexExecutor(_engine, writer, access, bank, new EvolutionService(), new LegalityService());
        var saves = new Dictionary<string, DetectedSave>(StringComparer.Ordinal)
        {
            [SaveId] = new(SaveId, "yellow.sav", "Pokémon Yellow", EmulatorKind.RetroArch, false, null, version == GameVersion.C ? 2 : 1),
        };
        LivingDexPlan plan;
        using (var session = _engine.OpenSession(bytes))
            plan = LivingDexPlanner.Plan(LivingDexCatalogBuilder.Build(),
                [LivingDexSources.FromSession(SaveId, "Yellow", session, bytes), LivingDexSources.FromBank(bank)],
                new LivingDexOptions(LivingDexPlanner.BankId, AllowLastCopies: true));
        Assert.Equal(20, plan.Moves); // species 1-20; the second Squirtle has no slot of its own

        var result = await executor.ApplyAsync(plan, saves);

        Assert.True(result.Committed, result.Summary);
        Assert.Equal(20, bank.GetAll().Count);
        using var after = _engine.OpenSession(access.Files[SaveId]);
        // Box 1 went whole (species 1-20); box 2's 1, 4, 7, 7 are second copies and stay.
        Assert.All(Box(after, 0), species => Assert.Equal(0, species));
        Assert.Equal([1, 4, 7, 7], Box(after, 1).Where(s => s != 0));
    }

    [Fact]
    public void AnUntouchedCartridgeSaveIsNotMistakenForUnsavedEdits()
    {
        // A real cartridge leaves stale entries after a list's terminator; writing tidies them,
        // so the raw file never equals what an untouched session writes. The autopilot called
        // the open Yellow "open with unsaved changes" for that and left it out.
        var file = File(GameVersion.YW, [Pikachu], (0, 1), (0, 4));
        file[0x2F2C + 3] = 0x99; // party list: count 1, Pikachu, terminator, then a leftover
        byte sum = 0;
        for (var i = 0x2598; i < 0x3523; i++) sum += file[i];
        file[0x3523] = (byte)~sum;

        using var open = _engine.OpenSession(file);
        using var fresh = _engine.OpenSession(file);
        var live = open.Serialize();

        Assert.False(live.Span.SequenceEqual(file));
        Assert.True(live.Span.SequenceEqual(fresh.Serialize().Span));
        Assert.Null(_engine.AssessLayoutRisk(file));
    }

    [Theory]
    [InlineData(GameVersion.RD)]
    [InlineData(GameVersion.C)]
    public void BoxToolsKeepEveryPokemonOfTwentySlotBoxes(GameVersion version)
    {
        var original = FullBoxFile(version);
        using var session = _engine.OpenSession(original);

        var preview = session.PreviewSortOrder(SortCriteria.DexNumber, [0, 1]);
        Assert.Equal(24, preview!.Count);
        Assert.Equal(24, session.SortBoxes(SortCriteria.DexNumber, [0, 1], reverse: true));
        session.BatchApply(["Level=50"], [0, 1]);
        session.SwapBoxes(0, 2);
        session.DeleteBox(1);
        session.PackListBoxes();
        var written = session.Serialize();

        Assert.Null(_engine.CheckWriteSafety(original, written, null));
        using var reopened = _engine.OpenSession(written);
        var boxed = reopened.Snapshot.Slots.Where(s => s.Box >= 0 && s.Species is > 0).ToList();
        Assert.Equal(24, boxed.Count);
        Assert.All(boxed, s => Assert.InRange(s.Slot, 0, 19));
        Assert.All(boxed, s => Assert.Equal(50, reopened.ReadEntity(s.Box, s.Slot).Level));
    }

    [Theory]
    [InlineData(0xFF)]
    [InlineData(0x00)]
    public void ANeverSavedCartridgeIsRefusedWithItsReason(byte fill)
    {
        var blank = Enumerable.Repeat(fill, 0x8000).ToArray();

        Assert.Null(_engine.TryDescribe(blank));
        Assert.Contains("never saved in game", _engine.DescribeRejection(blank));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(44)]
    [InlineData(48)]
    [InlineData(0x2000)]
    public void AGen1SaveWithAnEmulatorFooterOrPaddingStillReads(int extra)
    {
        byte[] file = [.. File(GameVersion.RD, [Pikachu], (0, 1)), .. new byte[extra]];

        Assert.Equal("Red / Blue", _engine.TryDescribe(file)?.GameName);
    }
}
