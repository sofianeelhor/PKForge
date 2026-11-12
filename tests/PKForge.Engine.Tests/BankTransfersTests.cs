using PKForge.Domain;
using PKForge.Infrastructure;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// A transfer of several Pokémon is all or nothing: a tester's event deposits to the Bank
/// ended half done. The destination takes the whole batch in one write first, the source lets
/// go only after that, and a failure at any step leaves both sides exactly as they were.
/// </summary>
public sealed class BankTransfersTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-banktransfers-" + Guid.NewGuid().ToString("N"));
    private readonly SaveEngine _engine = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A Sword save holding <paramref name="species"/> in box 1, in order.</summary>
    private static SaveEngineSession SaveWith(params ushort[] species)
    {
        var save = BlankSaveFile.Get(GameVersion.SW, "Sof", LanguageID.English);
        for (var i = 0; i < species.Length; i++)
        {
            var mon = save.BlankPKM;
            mon.Species = species[i];
            mon.CurrentLevel = 30;
            mon.Version = GameVersion.SW;
            mon.OriginalTrainerName = "Sof";
            mon.Language = (int)LanguageID.English;
            mon.PID = 0x12345678u + (uint)i;
            mon.Move1 = 33; // Tackle
            mon.MetLocation = 30;
            mon.MetLevel = 30;
            mon.Nickname = SpeciesName.GetSpeciesNameGeneration(species[i], mon.Language, mon.Format);
            mon.RefreshChecksum();
            save.SetBoxSlotAtIndex(mon, 0, i, EntityImportSettings.None);
        }
        return new SaveEngineSession(save, null);
    }

    private IReadOnlyList<BankDeposit> Deposits(ISaveEngineSession session, int count) =>
        [.. Enumerable.Range(0, count).Select(slot => session.ExportSlot(0, slot))
            .Select(export => new BankDeposit(export.Data, _engine.TryDescribeEntity(export.Data, "Sword", export.Format)!))];

    [Fact]
    public async Task AFailedSaveWriteTakesTheDepositsBackOutOfTheBank()
    {
        using var session = SaveWith(25, 133, 1);
        var bank = new FileBankService(_root);
        var before = session.Serialize().ToArray();

        // The move's release fails after the Bank took all three: the Bank gives them back.
        var outcome = await BankTransfers.DepositAsync(bank, Deposits(session, 3), () =>
        {
            session.ReleaseSlot(0, 0);
            return Task.FromResult<string?>("the save could not be written");
        });

        Assert.False(outcome.Success);
        Assert.False(outcome.Duplicated);
        Assert.StartsWith("Nothing was sent", outcome.Message);
        Assert.Empty(bank.GetAll());
        Assert.Empty(new FileBankService(_root).GetAll());
        Assert.Empty(Directory.GetFiles(_root, "*.bin"));
        Assert.Equal(before.Length, session.Serialize().Length);
    }

    [Fact]
    public async Task ABankThatCannotTakeTheBatchNeverTouchesTheSave()
    {
        using var session = SaveWith(25, 133);
        var bank = new FileBankService(_root);
        Directory.CreateDirectory(Path.Combine(_root, "index.json.tmp")); // the index can no longer be written
        var released = false;

        var outcome = await BankTransfers.DepositAsync(bank, Deposits(session, 2), () =>
        {
            released = true;
            return Task.FromResult<string?>(null);
        });

        Assert.False(outcome.Success);
        Assert.False(released);
        Assert.Empty(bank.GetAll());
    }

    [Fact]
    public async Task ADepositLandsEverythingThenReleases()
    {
        using var session = SaveWith(25, 133, 1);
        var bank = new FileBankService(_root);
        var deposits = Deposits(session, 3);

        var outcome = await BankTransfers.DepositAsync(bank, deposits, () =>
        {
            Assert.Equal(3, bank.GetAll().Count); // the Bank holds every copy before the save lets go
            for (var slot = 0; slot < 3; slot++) session.ReleaseSlot(0, slot);
            return Task.FromResult<string?>(null);
        });

        Assert.True(outcome.Success, outcome.Message);
        Assert.Equal([25, 133, 1], bank.GetAll().Select(e => e.Info.Species));
        Assert.Equal(deposits[1].Data, bank.GetData(bank.GetAll()[1].Id));
    }

    [Fact]
    public async Task AWithdrawalRefusedMidBatchLeavesTheBankWhole()
    {
        using var source = SaveWith(25, 133, 1);
        var bank = new FileBankService(_root);
        var entries = bank.AddMany(Deposits(source, 3));
        using var target = SaveWith();
        var rooms = target.Snapshot.Slots.Where(s => s.Box >= 0).Take(3).Select(s => new SlotRef(s.Box, s.Slot)).ToArray();
        // The second Pokémon's bytes are not a Pokémon: the first already landed in the session.
        var items = entries.Select((e, i) => new TransferItem(i == 1 ? new byte[7] : bank.GetData(e.Id), e.Info.Format, e.Info.Nickname)).ToArray();

        var outcome = await BankTransfers.WithdrawAsync(bank, [.. entries.Select(e => e.Id)], "Sword",
            () => Task.FromResult(BankTransfers.ImportAll(target, items, rooms, out var refusal) is var failed and >= 0
                ? $"{items[failed].Nickname} cannot go to Sword. {refusal}"
                : null),
            () => throw new InvalidOperationException("nothing to undo"));

        Assert.False(outcome.Success);
        Assert.Contains(items[1].Nickname, outcome.Message);
        Assert.Equal(entries, bank.GetAll());
        using var fresh = SaveWith();
        Assert.Equal(1, BankTransfers.ImportAll(fresh, items, rooms, out _)); // index of the refused one
    }

    [Fact]
    public void ASecondBatchIsRefusedTheRoomsTheFirstOneTook()
    {
        // Two sends in a row to the connected game were handed the same "empty" slots, read
        // from the picture taken when the save was opened: the second replaced the first,
        // which had already left the Bank for good.
        using var source = SaveWith(25, 133, 1);
        var bank = new FileBankService(_root);
        var entries = bank.AddMany(Deposits(source, 3));
        var items = entries.Select(e => new TransferItem(bank.GetData(e.Id), e.Info.Format, e.Info.Nickname)).ToArray();
        using var target = SaveWith();
        var stale = SlotPlanning.FreeSlotsFrom(target.Snapshot.Slots, null).Take(2).ToArray();

        Assert.Equal(-1, BankTransfers.ImportAll(target, items[..2], stale, out _));
        Assert.Equal(0, BankTransfers.ImportAll(target, items[2..], stale, out var refusal));

        Assert.Contains("no longer empty", refusal);
        Assert.Equal(25, target.ReadEntity(0, 0).Species);
        Assert.Equal(133, target.ReadEntity(0, 1).Species);
    }

    [Fact]
    public void RoomsReadFromTheLiveSessionFollowEachArrival()
    {
        using var source = SaveWith(25, 133, 1);
        var bank = new FileBankService(_root);
        var entries = bank.AddMany(Deposits(source, 3));
        var items = entries.Select(e => new TransferItem(bank.GetData(e.Id), e.Info.Format, e.Info.Nickname)).ToArray();
        using var target = SaveWith();

        var first = SlotPlanning.FreeSlotsFrom(target.Snapshot.Slots, null).Take(2).ToArray();
        Assert.Equal(-1, BankTransfers.ImportAll(target, items[..2], first, out _));
        var second = SlotPlanning.FreeSlotsFrom(target.Snapshot.Slots, null).Take(1).ToArray();
        Assert.Equal(-1, BankTransfers.ImportAll(target, items[2..], second, out _));

        Assert.Equal(new SlotRef(0, 2), second[0]);
        Assert.Equal([25, 133, 1], Enumerable.Range(0, 3).Select(slot => target.ReadEntity(0, slot).Species));
    }

    [Fact]
    public async Task ABankThatCannotLetGoGetsItsPokemonBackFromTheSave()
    {
        using var source = SaveWith(25, 133);
        var bank = new FileBankService(_root);
        var entries = bank.AddMany(Deposits(source, 2));
        using var target = SaveWith();
        var rooms = target.Snapshot.Slots.Where(s => s.Box >= 0).Take(2).Select(s => new SlotRef(s.Box, s.Slot)).ToArray();
        var items = entries.Select(e => new TransferItem(bank.GetData(e.Id), e.Info.Format, e.Info.Nickname)).ToArray();

        var outcome = await BankTransfers.WithdrawAsync(bank, [.. entries.Select(e => e.Id)], "Sword",
            () =>
            {
                Assert.Equal(-1, BankTransfers.ImportAll(target, items, rooms, out _));
                // The save write went through; then the Bank's index becomes unwritable.
                Directory.CreateDirectory(Path.Combine(_root, "index.json.tmp"));
                return Task.FromResult<string?>(null);
            },
            () =>
            {
                foreach (var room in rooms) target.ReleaseSlot(room.Box, room.Slot);
                return Task.FromResult<string?>(null);
            });

        Assert.False(outcome.Success);
        Assert.False(outcome.Duplicated);
        Assert.Equal(entries, bank.GetAll());
        Assert.All(rooms, room => Assert.True(target.ReadEntity(room.Box, room.Slot).IsEmpty));
    }

    [Fact]
    public async Task AnUndoThatFailsTooSaysBothSidesHoldTheBatch()
    {
        var steps = new List<string>();
        var outcome = await BankTransfers.MoveAsync(4, "Emerald",
            () => { steps.Add("write"); return Task.FromResult<string?>(null); },
            () => { steps.Add("release"); throw new IOException("disk full"); },
            () => { steps.Add("undo"); return Task.FromResult<string?>("read-only"); });

        Assert.Equal(["write", "release", "undo"], steps);
        Assert.False(outcome.Success);
        Assert.True(outcome.Duplicated);
        Assert.Contains("4 Pokémon are now in both places", outcome.Message);
    }
}
