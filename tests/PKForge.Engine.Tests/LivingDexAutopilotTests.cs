using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The Living Dex Autopilot end to end on synthetic blank saves: plan, stage (the dry run),
/// commit through the real SafeSaveWriter and FileBackupService, then prove the moves, the
/// trade evolution, the restore points and the all-or-nothing rollback.
/// </summary>
public sealed class LivingDexAutopilotTests : IDisposable
{
    private const ushort Bulbasaur = 1, Squirtle = 7, Pikachu = 25, Kadabra = 64, Alakazam = 65, Machop = 66;
    private const string DestId = "content://saves/black2.sav", SourceId = "content://saves/white2.sav";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-livingdex-" + Guid.NewGuid().ToString("N"));
    private readonly SaveEngine _engine = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class OwnershipSettings : IGenerationOwnershipSettings
    {
        public bool UseCurrentTrainerForGeneration => true;
    }

    /// <summary>A blank Gen 5 (Black 2) save holding legal, generated Pokémon at the given slots (box -1 = party).</summary>
    private byte[] Save(string trainer, params (int Box, int Slot, ushort Species)[] mons)
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(GameVersion.B2, trainer, LanguageID.English), null);
        session.SetTrainer(new TrainerInfo(trainer, 12345, 54321, 1000, 0));
        var legalizer = new LegalizerService(new OwnershipSettings());
        foreach (var (box, slot, species) in mons)
        {
            var outcome = legalizer.Generate(session, box, slot, new GenerationRequest(species, 30, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null));
            Assert.True(outcome.Success, outcome.Message);
        }
        return session.Serialize().ToArray();
    }

    /// <summary>A Gen 4 Squirtle exported as a loose .pk4, as the Bank stores it (a forward conversion into Gen 5).</summary>
    private byte[] BankSquirtle()
    {
        using var session = (SaveEngineSession)_engine.OpenBlankSession(4);
        var outcome = new LegalizerService(new OwnershipSettings()).Generate(session, 0, 0,
            new GenerationRequest(Squirtle, 20, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null));
        Assert.True(outcome.Success, outcome.Message);
        return session.ExportSlot(0, 0).Data;
    }

    private sealed class MemoryAccess : ISaveFileAccess
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public string? FailWritesTo { get; set; }
        public List<string> Writes { get; } = [];

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(string documentId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ReadOnlyMemory<byte>>(Files[documentId].ToArray());

        public ValueTask WriteAtomicallyAsync(string documentId, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            if (documentId == FailWritesTo) throw new IOException("The storage grant was revoked.");
            Writes.Add(documentId);
            Files[documentId] = bytes.ToArray();
            return ValueTask.CompletedTask;
        }
    }

    private sealed record World(MemoryAccess Access, FileBackupService Backups, FileBankService Bank, LivingDexExecutor Executor,
        Dictionary<string, DetectedSave> Saves, byte[] DestOriginal, byte[] SourceOriginal, Guid SquirtleId);

    private World Build()
    {
        var access = new MemoryAccess();
        // Destination: a Bulbasaur already there. Source: three Kadabra, one Pikachu (its only
        // copy), and two Machop in the party (party is off limits by default).
        var dest = Save("Lyra", (0, 0, Bulbasaur));
        var source = Save("Dawn", (0, 0, Kadabra), (0, 1, Kadabra), (0, 2, Kadabra), (0, 3, Pikachu), (-1, 0, Machop), (-1, 1, Machop));
        access.Files[DestId] = dest;
        access.Files[SourceId] = source;
        var backups = new FileBackupService(Path.Combine(_root, "backups"));
        var bank = new FileBankService(Path.Combine(_root, "bank"));
        var squirtle = BankSquirtle();
        var entry = bank.Add(squirtle, _engine.TryDescribeEntity(squirtle, "test")!);
        var writer = new SafeSaveWriter(_engine, backups, access);
        var executor = new LivingDexExecutor(_engine, writer, access, bank, new EvolutionService(), new LegalityService());
        var saves = new Dictionary<string, DetectedSave>(StringComparer.Ordinal)
        {
            [DestId] = new(DestId, "black2.sav", "Pokémon Black 2", EmulatorKind.MelonDS, false, null, 5),
            [SourceId] = new(SourceId, "white2.sav", "Pokémon White 2", EmulatorKind.MelonDS, false, null, 5),
        };
        return new World(access, backups, bank, executor, saves, dest, source, entry.Id);
    }

    private LivingDexPlan Plan(World world, LivingDexOptions? options = null)
    {
        var sources = new List<LivingDexSource>();
        foreach (var (id, label) in new[] { (DestId, "Black 2"), (SourceId, "White 2") })
        {
            var bytes = world.Access.Files[id];
            using var session = _engine.OpenSession(bytes, world.Saves[id].EngineHint, world.Saves[id].Format);
            sources.Add(LivingDexSources.FromSession(id, label, session, bytes));
        }
        sources.Add(LivingDexSources.FromBank(world.Bank));
        return LivingDexPlanner.Plan(LivingDexCatalogBuilder.Build(), sources, options ?? new LivingDexOptions(DestId));
    }

    private SaveEngineSession Open(World world, string id) => (SaveEngineSession)_engine.OpenSession(world.Access.Files[id]);

    private static int[] BoxSpecies(SaveEngineSession session) =>
        [.. session.Snapshot.Slots.Where(s => s.Box >= 0 && s.Species is > 0).Select(s => s.Species!.Value).Order()];

    [Fact]
    public void TheCatalogKnowsTradeEvolutionsAndCollectibleForms()
    {
        var catalog = LivingDexCatalogBuilder.Build();
        Assert.Equal(1025, catalog.Species.Count);
        Assert.Contains(new LivingDexTradeEvolution(Kadabra, 0, Alakazam, 0), catalog.TradeEvolutions);
        Assert.Contains(1, catalog.FormsOf(37)); // Alolan Vulpix
        Assert.DoesNotContain(1, catalog.FormsOf(3)); // Mega Venusaur is battle-only
        Assert.Equal(28, catalog.FormsOf(201).Count); // Unown A-Z ! ?
    }

    [Fact]
    public async Task ApplyMovesEvolvesAndLeavesRestorePointsOfTheOriginals()
    {
        var world = Build();
        var plan = Plan(world);

        Assert.Equal(1, plan.CoveredBefore);
        Assert.Equal(LivingDexStepKind.Move, plan.Steps.Single(s => s.Fills && s.Species == Kadabra).Kind);
        Assert.Equal(LivingDexStepKind.EvolveAndMove, plan.Steps.Single(s => s.Fills && s.Species == Alakazam).Kind);
        Assert.Equal(LivingDexPlanner.BankId, plan.Steps.Single(s => s.Fills && s.Species == Squirtle).SourceId);
        Assert.Contains(plan.Steps, s => s.Kind == LivingDexStepKind.Guide && s.Species == Pikachu); // only copy stays
        Assert.Contains(plan.Steps, s => s.Kind == LivingDexStepKind.Guide && s.Species == Machop);  // party stays

        // The dry run stages everything and writes nothing.
        using (var dry = await world.Executor.StageAsync(plan, world.Saves))
        {
            Assert.Equal(3, dry.Staged);
            Assert.Equal(0, dry.Failed);
        }
        Assert.Empty(world.Access.Writes);

        var progress = new List<LivingDexProgress>();
        var result = await world.Executor.ApplyAsync(plan, world.Saves, new SyncProgress(progress.Add));

        Assert.True(result.Committed, result.Summary);
        Assert.Equal(4, result.CoveredAfter);
        Assert.Contains("1 → 4", result.Summary);
        Assert.Equal([DestId, SourceId], world.Access.Writes); // each save exactly once, destination first
        Assert.Contains(progress, p => p.Phase == LivingDexPhase.Writing);

        using (var dest = Open(world, DestId))
            Assert.Equal([Bulbasaur, Squirtle, Kadabra, Alakazam], BoxSpecies(dest));
        using (var source = Open(world, SourceId))
        {
            Assert.Equal([Pikachu, Kadabra], BoxSpecies(source));
            Assert.Equal(2, source.Snapshot.Slots.Count(s => s.Box == -1 && s.Species == Machop));
        }
        Assert.DoesNotContain(world.Bank.GetAll(), e => e.Id == world.SquirtleId);

        // One restore point per written save, each holding exactly the bytes before the run.
        Assert.Equal(2, result.BackupIds.Count);
        Assert.Equal(world.DestOriginal, (await world.Backups.ReadAsync(result.BackupIds[DestId])).ToArray());
        Assert.Equal(world.SourceOriginal, (await world.Backups.ReadAsync(result.BackupIds[SourceId])).ToArray());
        Assert.Equal(2, (await world.Backups.ListAsync()).Count);
    }

    [Fact]
    public async Task AFailedWriteRollsEverySaveAndTheBankBack()
    {
        var world = Build();
        var plan = Plan(world);
        world.Access.FailWritesTo = SourceId; // the destination is written, then the source fails

        var result = await world.Executor.ApplyAsync(plan, world.Saves);

        Assert.False(result.Committed);
        Assert.True(result.RolledBack);
        Assert.Equal(world.DestOriginal, world.Access.Files[DestId]);
        Assert.Equal(world.SourceOriginal, world.Access.Files[SourceId]);
        Assert.Contains(world.Bank.GetAll(), e => e.Id == world.SquirtleId); // the Bank never let go
        Assert.All(result.Outcomes, o => Assert.NotEqual(LivingDexStepStatus.Done, o.Status));
    }

    [Fact]
    public async Task AFailedBankReleaseKeepsTheBankWholeAndRollsTheSavesBack()
    {
        var world = Build();
        var plan = Plan(world);
        // The saves are written, then the Bank's index write (releasing Squirtle) fails.
        Directory.CreateDirectory(Path.Combine(_root, "bank", "index.json.tmp"));

        var result = await world.Executor.ApplyAsync(plan, world.Saves);

        Assert.False(result.Committed);
        Assert.True(result.RolledBack);
        Assert.Contains("put back", result.Summary);
        Assert.DoesNotContain("Rollback problem", result.Summary);
        Assert.Equal(world.DestOriginal, world.Access.Files[DestId]);
        Assert.Equal(world.SourceOriginal, world.Access.Files[SourceId]);
        Assert.Contains(world.Bank.GetAll(), e => e.Id == world.SquirtleId);
        Assert.NotEmpty(world.Bank.GetData(world.SquirtleId)); // its bytes were not deleted either
        Directory.Delete(Path.Combine(_root, "bank", "index.json.tmp"));
        Assert.Contains(new FileBankService(Path.Combine(_root, "bank")).GetAll(), e => e.Id == world.SquirtleId);
    }

    [Fact]
    public async Task ACancelBeforeWritingChangesNothing()
    {
        var world = Build();
        var plan = Plan(world);
        using var staging = await world.Executor.StageAsync(plan, world.Saves);
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var result = await world.Executor.CommitAsync(staging, cancellationToken: cancel.Token);

        Assert.False(result.Committed);
        Assert.False(result.RolledBack);
        Assert.Empty(world.Access.Writes);
        Assert.StartsWith("Cancelled", result.Summary);
    }

    [Fact]
    public async Task ASaveThatChangedSinceThePlanIsRefused()
    {
        var world = Build();
        var plan = Plan(world);
        // The emulator saved in the meantime.
        world.Access.Files[SourceId] = Save("Dawn", (0, 0, Kadabra));

        var error = await Assert.ThrowsAsync<LivingDexAbortException>(() => world.Executor.StageAsync(plan, world.Saves));
        Assert.Contains("emulator", error.Message);
        Assert.Empty(world.Access.Writes);
    }

    [Fact]
    public async Task TheBankAsDestinationGetsDexOrderedBoxesAndTheSourceLetsGoAfter()
    {
        var world = Build();
        var plan = Plan(world, new LivingDexOptions(LivingDexPlanner.BankId));
        // Box 0 holds the Squirtle; the region takes the empty boxes right after it.
        var startBox = plan.BankStartBox;
        Assert.Equal(1, startBox);

        var result = await world.Executor.ApplyAsync(plan, world.Saves);

        Assert.True(result.Committed, result.Summary);
        Assert.Equal([SourceId], world.Access.Writes);
        var entries = world.Bank.GetAll();
        // Squirtle #7 → slot 6 of the region (arranged), Kadabra #64 and Alakazam #65 moved in.
        // Black 2's Bulbasaur is its only copy: it stays home, its Bank slot waits empty.
        Assert.DoesNotContain(entries, e => e.Info.Species == Bulbasaur);
        Assert.Contains(entries, e => e.Id == world.SquirtleId && e.Box == startBox && e.Slot == 6);
        Assert.Contains(entries, e => e.Info.Species == Kadabra && e.Box == startBox + 63 / 30 && e.Slot == 63 % 30);
        Assert.Contains(entries, e => e.Info.Species == Alakazam && e.Box == startBox + 64 / 30 && e.Slot == 64 % 30);
        using var source = Open(world, SourceId);
        Assert.Equal([Pikachu, Kadabra], BoxSpecies(source));
        using var dest = Open(world, DestId);
        Assert.Equal([Bulbasaur], BoxSpecies(dest));
    }

    private sealed class SyncProgress(Action<LivingDexProgress> report) : IProgress<LivingDexProgress>
    {
        public void Report(LivingDexProgress value) => report(value);
    }
}
