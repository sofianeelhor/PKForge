using System.Text;
using System.Text.Json;
using PKForge.Infrastructure;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The engine review's sync and store findings (5, 6, 8, 9, 11, 18), each reproduced as it failed:
/// two devices sharing the PKForge folder never lose an item or a Pokémon History copy, an edit
/// made in the folder during a sync is never lost, and no data read from a file can name a path.
/// </summary>
public sealed class MirrorDataSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-safety-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    private static readonly string MasterBall = ItemBankKeys.Item(1);
    private static readonly string RareCandy = ItemBankKeys.Item(50);
    private static readonly string Potion = ItemBankKeys.Item(17);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static BankEntryInfo? Describe(byte[] bytes, string source, string? format) =>
        bytes.Length >= 2 && bytes[1] == 0xAA ? new BankEntryInfo(bytes[0], 0, false, $"Mon{bytes[0]}", 5, 7, source, format ?? "PK7") : null;

    private static byte[] Mon(byte species, byte salt = 0) => [species, 0xAA, salt, 7];

    private sealed record Device(string Root, FileBankService Bank, RecentlyRemovedStore Removed, ItemBankStore Items,
        FilePokemonHistoryStore History, IReadOnlyList<IMirrorSource> Sources)
    {
        public MirrorState? State;
        public string Album => Path.Combine(Root, "album");
    }

    private Device NewDevice(string name)
    {
        var root = Path.Combine(_root, name);
        var bank = new FileBankService(Path.Combine(root, "bank"));
        var removed = new RecentlyRemovedStore(Path.Combine(root, "removed"));
        var items = new ItemBankStore(Path.Combine(root, "item-bank"));
        var history = new FilePokemonHistoryStore(Path.Combine(root, FilePokemonHistoryStore.Folder));
        IMirrorSource[] sources =
        [
            new BankMirrorSource(bank, new BankBoxNames(Path.Combine(root, "bank")), Describe, info => $"Mon{info.Species}", removed, () => Now),
            new DirectoryMirrorSource("Album/", Path.Combine(root, "album"), removed, () => Now),
            new HistoryMirrorSource(history, removed, () => Now),
            new ItemBankMirrorSource(items, Path.Combine(root, "mirror-item-bank.json"), removed, () => Now),
        ];
        return new Device(root, bank, removed, items, history, sources);
    }

    private static async Task<MirrorReport> Sync(Device device, MemoryFolder folder)
    {
        var (report, state) = await FolderMirrorSync.SyncAsync(folder, "tree", device.Sources, device.State, Now);
        device.State = state;
        return report;
    }

    // ── Finding 5: the Item Bank as one opaque file ─────────────────────────

    /// <summary>infraprobe C: A banked 10 Rare Candies, B 5 Potions, both offline. B's sync made A's bank
    /// a conflict copy nothing read, and A's next sync replaced its bank with B's: the candies were gone.</summary>
    [Fact]
    public async Task Items_banked_offline_on_two_devices_all_survive_and_a_withdrawal_stays_withdrawn()
    {
        var folder = new MemoryFolder();
        var a = NewDevice("a");
        var b = NewDevice("b");
        a.Items.Save(ItemBank.Empty.Deposit(MasterBall, "Master Ball", 1, "Emerald", 3));
        await Sync(a, folder);
        await Sync(b, folder);
        Assert.Equal(1, b.Items.Read().CountOf(MasterBall));

        a.Items.Update(bank => bank.Deposit(RareCandy, "Rare Candy", 10, "Emerald", 3));
        b.Items.Update(bank => bank.Deposit(Potion, "Potion", 5, "Crystal", 2));
        await Sync(a, folder);
        var merged = await Sync(b, folder);
        await Sync(a, folder);

        Assert.Empty(merged.Conflicts);
        Assert.DoesNotContain(folder.Files.Keys, path => path.Contains("conflict", StringComparison.Ordinal));
        foreach (var device in new[] { a, b })
        {
            var bank = device.Items.Read();
            Assert.Equal((1, 10, 5), (bank.CountOf(MasterBall), bank.CountOf(RareCandy), bank.CountOf(Potion)));
        }

        // Both change again: A takes 4 candies out into a game, B banks 2 more Master Balls.
        a.Items.Update(bank => bank.Withdraw(RareCandy, 4));
        b.Items.Update(bank => bank.Deposit(MasterBall, "Master Ball", 2, "Emerald", 3));
        await Sync(a, folder);
        await Sync(b, folder);
        await Sync(a, folder);
        foreach (var device in new[] { a, b })
        {
            var bank = device.Items.Read();
            Assert.Equal((3, 6, 5), (bank.CountOf(MasterBall), bank.CountOf(RareCandy), bank.CountOf(Potion)));
        }
    }

    /// <summary>A pull wrote the whole folder copy over the bank, outside the store's lock: a deposit
    /// made while the sync ran was overwritten.</summary>
    [Fact]
    public async Task A_deposit_made_while_the_bank_is_pulled_is_kept()
    {
        var folder = new MemoryFolder();
        var a = NewDevice("a");
        var b = NewDevice("b");
        a.Items.Save(ItemBank.Empty.Deposit(MasterBall, "Master Ball", 1, "Emerald", 3));
        await Sync(a, folder);
        await Sync(b, folder);
        b.Items.Update(bank => bank.Deposit(Potion, "Potion", 5, "Crystal", 2));
        await Sync(b, folder);

        // A's sync sends a photo first; while it does, A's player banks 10 Rare Candies.
        Directory.CreateDirectory(a.Album);
        File.WriteAllBytes(Path.Combine(a.Album, "1.jpg"), [1, 2, 3]);
        folder.AfterWrite = path =>
        {
            if (path == "Album/1.jpg") a.Items.Update(bank => bank.Deposit(RareCandy, "Rare Candy", 10, "Emerald", 3));
        };
        await Sync(a, folder);
        folder.AfterWrite = null;
        await Sync(a, folder);
        await Sync(b, folder);

        foreach (var device in new[] { a, b })
        {
            var bank = device.Items.Read();
            Assert.Equal((1, 10, 5), (bank.CountOf(MasterBall), bank.CountOf(RareCandy), bank.CountOf(Potion)));
        }
    }

    // ── Finding 11: a valid-JSON Item Bank without its lists ────────────────

    [Fact]
    public async Task An_item_bank_file_without_its_lists_never_reaches_the_bank_as_nulls()
    {
        var store = new ItemBankStore(Path.Combine(_root, "item-bank"));
        Directory.CreateDirectory(store.Root);

        // Was an ArgumentNullException no caller catches: the Item Bank page crashed on every open.
        File.WriteAllText(store.FilePath, """{"Version":1}""");
        Assert.Throws<InvalidDataException>(store.Read);
        Assert.Throws<InvalidDataException>(() => store.Update(bank => bank));
        Assert.Equal("""{"Version":1}""", File.ReadAllText(store.FilePath)); // never overwritten

        // An entry without its origins (an older or foreign writer) reads as one with none.
        File.WriteAllText(store.FilePath, """{"Version":1,"Entries":[{"Key":"item:17","Name":"Potion","Count":3},null,{"Key":null,"Count":2}]}""");
        var read = store.Read();
        Assert.Equal(3, read.CountOf(Potion));
        Assert.Empty(read.Entries.Single().Origins);
        Assert.Equal(1, read.Withdraw(Potion, 2).CountOf(Potion));
        Assert.Single(read.Merge(ItemBank.Empty.Deposit(Potion, "Potion", 1, "Crystal", 2), out _).Entries.Single().Origins);

        Assert.False(ItemBankStore.IsItemBank(Encoding.UTF8.GetBytes("""{"Version":1}""")));
        Assert.False(ItemBankStore.IsItemBank(Encoding.UTF8.GetBytes("[1,2]")));

        // The mirror leaves such a file in the folder instead of taking it in.
        var folder = new MemoryFolder();
        var a = NewDevice("a");
        a.Items.Save(ItemBank.Empty.Deposit(MasterBall, "Master Ball", 1, "Emerald", 3));
        await Sync(a, folder);
        folder.Put(ItemBankMirrorSource.FolderPath, Encoding.UTF8.GetBytes("""{"Version":1}"""));
        var report = await Sync(a, folder);
        Assert.Contains(report.LeftAlone, line => line.StartsWith(ItemBankMirrorSource.FolderPath, StringComparison.Ordinal));
        Assert.Equal(1, a.Items.Read().CountOf(MasterBall));
    }

    // ── Finding 6: post-sync stamps paired with pre-sync hashes ─────────────

    /// <summary>infraprobe A: a folder edit made while the sync wrote another file got the new stamp with
    /// the old hash, so it was never read again, and later overwritten.</summary>
    [Fact]
    public async Task A_folder_edit_made_during_a_sync_is_brought_in_by_the_next_one()
    {
        var folder = new MemoryFolder();
        var a = NewDevice("a");
        Directory.CreateDirectory(a.Album);
        File.WriteAllBytes(Path.Combine(a.Album, "1.jpg"), [1]);
        await Sync(a, folder);

        File.WriteAllBytes(Path.Combine(a.Album, "2.jpg"), [2]);
        folder.AfterWrite = path => { if (path == "Album/2.jpg") folder.Put("Album/1.jpg", [9, 9]); };
        await Sync(a, folder);
        folder.AfterWrite = null;

        var report = await Sync(a, folder);
        Assert.Equal(1, report.Pulled);
        Assert.Equal([9, 9], File.ReadAllBytes(Path.Combine(a.Album, "1.jpg")));
    }

    /// <summary>For Bank/ it was worse: releasing the Pokémon then deleted the folder's edited file
    /// outright. Now a file is read again right before it is deleted, and an edit brings it back.</summary>
    [Fact]
    public async Task A_release_never_deletes_a_bank_file_edited_in_the_folder()
    {
        var folder = new MemoryFolder();
        var a = NewDevice("a");
        var mon = a.Bank.AddMany([new BankDeposit(Mon(25), Describe(Mon(25), "x", "PK7")!, 0, 0)]).Single();
        await Sync(a, folder);
        const string file = "Bank/01 - Box 1/001 - Mon25.pk7";

        // Released here; in the folder, the file is edited while this sync sends a photo first.
        a.Bank.Remove(mon.Id);
        Directory.CreateDirectory(a.Album);
        File.WriteAllBytes(Path.Combine(a.Album, "1.jpg"), [1]);
        folder.AfterWrite = path => { if (path == "Album/1.jpg") folder.Put(file, Mon(25, 9)); };
        var report = await Sync(a, folder);

        Assert.Equal(0, report.DeletedRemote);
        Assert.Equal(Mon(25, 9), folder.Files[file].Bytes);
        var back = Assert.Single(a.Bank.GetAll());
        Assert.Equal(Mon(25, 9), a.Bank.GetData(back.Id));
    }

    // ── Finding 18: a pulled Bank edit ──────────────────────────────────────

    [Fact]
    public async Task A_pulled_bank_edit_keeps_the_old_pokemon_and_respects_box_locks()
    {
        var folder = new MemoryFolder();
        var a = NewDevice("a");
        var mon = a.Bank.AddMany([new BankDeposit(Mon(25), Describe(Mon(25), "x", "PK7")!, 0, 0)]).Single();
        await Sync(a, folder);
        const string file = "Bank/01 - Box 1/001 - Mon25.pk7";

        folder.Put(file, Mon(25, 1));
        await Sync(a, folder);
        Assert.Equal(Mon(25, 1), a.Bank.GetData(mon.Id));
        var held = Assert.Single(a.Removed.List()); // the Pokémon it replaced, for 30 days
        Assert.Equal(Mon(25), a.Removed.Read(held));

        a.Bank.SetBoxLocked(0, true);
        folder.Put(file, Mon(25, 2));
        var report = await Sync(a, folder);
        Assert.Single(report.LeftAlone);
        Assert.Equal(Mon(25, 1), a.Bank.GetData(mon.Id)); // a locked box is not edited from the folder
        Assert.Equal(Mon(25, 2), folder.Files[file].Bytes); // and the edit waits there
    }

    [Fact]
    public void A_replace_whose_index_write_fails_keeps_the_old_bytes()
    {
        var directory = Path.Combine(_root, "bank");
        var bank = new FileBankService(directory);
        var mon = bank.Add([1, 2, 3], new BankEntryInfo(25, 0, false, "A", 5, 8, "t", "PK8"));
        var blocker = Path.Combine(directory, "index.json.tmp");
        Directory.CreateDirectory(blocker);
        Assert.ThrowsAny<Exception>(() => bank.Replace(mon.Id, [9, 9, 9], mon.Info with { Nickname = "B" }));
        Directory.Delete(blocker);
        Assert.Equal([1, 2, 3], bank.GetData(mon.Id)); // bytes and facts still agree
        Assert.Equal("A", bank.GetAll().Single().Info.Nickname);
    }

    // ── Finding 8: ids and paths read from data ─────────────────────────────

    /// <summary>infraprobe E: a history record with the id "../bank/index" overwrote the Bank index,
    /// which then read as an empty Bank.</summary>
    [Fact]
    public void A_crafted_history_id_never_becomes_a_path()
    {
        var bankDirectory = Path.Combine(_root, "bank");
        var bank = new FileBankService(bankDirectory);
        bank.Add([1, 2, 3], new BankEntryInfo(25, 0, false, "Pikachu", 5, 7, "test", "PK7"));
        var crafted = new PokemonHistoryRecord("../bank/index", 25, ["soulX"], [], [], []);

        Assert.Null(FilePokemonHistoryStore.Parse(FilePokemonHistoryStore.Serialize(crafted)));
        var store = new FilePokemonHistoryStore(Path.Combine(_root, FilePokemonHistoryStore.Folder));
        Assert.Throws<ArgumentException>(() => store.Merge(crafted));
        Assert.Single(new FileBankService(bankDirectory).GetAll());
    }

    [Fact]
    public void A_bank_index_overwritten_by_other_json_is_not_an_empty_bank()
    {
        var directory = Path.Combine(_root, "bank");
        var bank = new FileBankService(directory);
        bank.Add([1], new BankEntryInfo(25, 0, false, "A", 5, 7, "t", "PK7"));
        bank.Add([2], new BankEntryInfo(26, 0, false, "B", 5, 7, "t", "PK7")); // the backup index holds A
        File.WriteAllText(Path.Combine(directory, "index.json"), """{"id":"0123456789abcdef0123","species":25}""");
        Assert.Single(new FileBankService(directory).GetAll()); // read from the backup, not as empty

        File.Delete(Path.Combine(directory, "index.json.bak"));
        var unreadable = new FileBankService(directory);
        Assert.Throws<InvalidOperationException>(() => unreadable.Add([3], new BankEntryInfo(27, 0, false, "C", 5, 7, "t", "PK7")));
    }

    /// <summary>A restore point's sidecar (brought in by a data archive) named its own files: pruning
    /// one with the id "../bank/index" deleted the Bank index.</summary>
    [Fact]
    public async Task A_crafted_restore_point_id_is_never_pruned_as_a_path()
    {
        var bank = new FileBankService(Path.Combine(_root, "bank"));
        bank.Add([1], new BankEntryInfo(25, 0, false, "A", 5, 7, "t", "PK7"));
        var backups = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backups);
        File.WriteAllText(Path.Combine(backups, "evil.json"), JsonSerializer.Serialize(
            new BackupInfo("../bank/index", Now.AddYears(-5), "00", "x", "PK7", 7, 1, DocumentId: "doc")));

        var service = new FileBackupService(backups, maxVersions: 1);
        await service.CreateAsync(new SaveSnapshot("PKM Test", 7, new byte[] { 1 }, [], "save"), documentId: "doc");
        await service.CreateAsync(new SaveSnapshot("PKM Test", 7, new byte[] { 2 }, [], "save"), documentId: "doc");

        Assert.True(File.Exists(Path.Combine(_root, "bank", "index.json")));
        Assert.DoesNotContain(await service.ListAsync(), info => info.BackupId.Contains('/'));
    }

    [Fact]
    public void A_recently_removed_note_naming_another_file_is_ignored()
    {
        var store = new RecentlyRemovedStore(Path.Combine(_root, "removed"));
        store.Put([1], "Album/a.jpg", null, null, Now);
        File.WriteAllText(Path.Combine(store.Directory, "evil.json"), JsonSerializer.Serialize(
            new RecentlyRemovedStore.Item("../bank/index", Now.AddYears(-1), "x", null, null)));
        Assert.Single(store.List());
        Assert.Equal(0, store.Prune(Now)); // the crafted note is never followed to "../bank/index"
    }

    // ── Finding 9: a History conflict copy hid the real record ──────────────

    private static PokemonHistoryRecord Trip(PokemonHistoryRecord? record, string format, string game, DateTimeOffset at, byte seed) =>
        (record ?? PokemonHistoryRecord.Start(["soul:a"], 286)).WithTrip(["soul:a"], 286,
            HistoryCopy.Of(format, game, at, [seed, 1]), HistoryCopy.Of("PK3", "Emerald", at, [seed, 2]), restored: false);

    /// <summary>infraprobe B: "id (conflict …).json" sorts before "id.json", so it was found first, and the
    /// next trip saved it over the real record, which lost its PK8 departure copy.</summary>
    [Fact]
    public void A_history_conflict_copy_never_hides_the_real_record()
    {
        var directory = Path.Combine(_root, FilePokemonHistoryStore.Folder);
        var store = new FilePokemonHistoryStore(directory);
        var mine = Trip(null, "PK8", "Sword", Now, 1);
        store.Save(mine);
        var theirs = Trip(null, "PK5", "White", Now.AddMinutes(1), 2);
        File.WriteAllText(Path.Combine(directory, $"{mine.Id} (conflict 2026-11-12).json"), FilePokemonHistoryStore.Serialize(theirs));

        var found = store.Find(["soul:a"])!;
        Assert.NotNull(found.DepartureFor("PK8"));
        Assert.NotNull(found.DepartureFor("PK5"));

        store.Update(["soul:a"], record => record!.WithTrip(["soul:a"], 286,
            HistoryCopy.Of("PK3", "Emerald", Now.AddDays(1), [3, 1]), HistoryCopy.Of("PK4", "Platinum", Now.AddDays(1), [3, 2]), false));
        var saved = FilePokemonHistoryStore.Read(Path.Combine(directory, mine.Id + ".json"))!;
        Assert.Equal([1, 1], saved.DepartureFor("PK8")!.Bytes()); // the copy the feature exists to restore
        Assert.NotNull(saved.DepartureFor("PK5"));
        Assert.Equal(3, saved.Trips.Count);
    }

    [Fact]
    public async Task A_history_record_changed_on_two_devices_is_merged_in_place()
    {
        var folder = new MemoryFolder();
        var a = NewDevice("a");
        var b = NewDevice("b");
        var start = Trip(null, "PK8", "Sword", Now, 1);
        a.History.Save(start);
        await Sync(a, folder);
        await Sync(b, folder);

        a.History.Save(Trip(a.History.Find(["soul:a"]), "PK5", "White", Now.AddHours(1), 2));
        b.History.Save(Trip(b.History.Find(["soul:a"]), "PK6", "X", Now.AddHours(2), 3));
        await Sync(a, folder);
        var report = await Sync(b, folder);
        await Sync(a, folder);

        Assert.Empty(report.Conflicts);
        Assert.DoesNotContain(folder.Files.Keys, path => path.Contains("conflict", StringComparison.Ordinal));
        foreach (var device in new[] { a, b })
        {
            var record = Assert.Single(device.History.All());
            Assert.Equal(3, record.Trips.Count);
            Assert.NotNull(record.DepartureFor("PK8"));
            Assert.NotNull(record.DepartureFor("PK5"));
            Assert.NotNull(record.DepartureFor("PK6"));
        }
    }

    // ── Every JSON store: partial, foreign or null-holding files ────────────

    [Fact]
    public void Json_stores_read_null_holding_files_without_crashing()
    {
        var presets = Path.Combine(_root, "item-presets.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(presets, """[null,{"Id":"x","Name":"Mine","Generation":3,"Items":null}]""");
        var itemPresets = new ItemPresetStore(presets);
        Assert.Empty(Assert.Single(itemPresets.Read()).Items);
        Assert.Equal(1, itemPresets.Merge([null!, new ItemPreset("y", "Other", 3, null!)]));

        var monPresets = Path.Combine(_root, "mon-presets.json");
        File.WriteAllText(monPresets, """[null,{"Id":"m","Name":"Mine","Lines":null}]""");
        Assert.Empty(Assert.Single(new MonPresetStore(monPresets).Read()).Lines);

        var state = Path.Combine(_root, "mirror-state.json");
        File.WriteAllText(state, """{"Album/a.jpg":null,"Album/b.jpg":{"Hash":"AB","RemoteSize":1,"RemoteModifiedMs":2}}""");
        Assert.Equal(["Album/b.jpg"], MirrorState.Load(state)!.Records.Keys);

        var identities = Path.Combine(_root, "save-identities.json");
        File.WriteAllText(identities, "[null]");
        Assert.Null(new JsonSaveIdentityStore(identities).Get("x"));
    }

    [Fact]
    public void Atomic_writes_leave_no_temporary_beside_the_file()
    {
        var store = new ItemBankStore(Path.Combine(_root, "item-bank"));
        store.Update(bank => bank.Deposit(Potion, "Potion", 1, "Crystal", 2));
        store.UpdateOnce("walk:1", bank => bank.Deposit(Potion, "Potion", 1, "Crystal", 2));
        Assert.False(store.UpdateOnce("walk:1", bank => bank.Deposit(Potion, "Potion", 1, "Crystal", 2)));
        Assert.Equal(2, store.Read().CountOf(Potion));
        Assert.Equal([ItemBankStore.FileName], Directory.GetFiles(store.Root).Select(Path.GetFileName));
    }
}
