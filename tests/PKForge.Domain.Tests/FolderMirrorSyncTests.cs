using Xunit;
using System.Text;
using PKForge.Domain;
using PKForge.Infrastructure;

namespace PKForge.Domain.Tests;

/// <summary>An in-memory granted folder: path → bytes and a modification stamp.</summary>
internal sealed class MemoryFolder : IFolderTreeAccess
{
    public readonly Dictionary<string, (byte[] Bytes, DateTimeOffset At)> Files = new(MirrorPlan.PathComparer);
    private long _tick = 1_700_000_000_000;
    public int Reads;

    /// <summary>Runs after each write: another device (Syncthing, the player) changing the folder mid-sync.</summary>
    public Action<string>? AfterWrite;

    public void Put(string path, byte[] bytes) => Files[path] = (bytes, DateTimeOffset.FromUnixTimeMilliseconds(_tick += 1000));

    public ValueTask<IReadOnlyList<FolderEntry>> ListTreeAsync(string treeId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<FolderEntry>>([.. Files.Select(f => new FolderEntry(f.Key, f.Value.Bytes.Length, f.Value.At))]);

    public ValueTask<byte[]> ReadAsync(string treeId, string path, CancellationToken cancellationToken = default)
    {
        Reads++;
        return ValueTask.FromResult(Files[path].Bytes);
    }

    public ValueTask WriteAsync(string treeId, string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        Put(path, bytes.ToArray());
        AfterWrite?.Invoke(path);
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> DeleteAsync(string treeId, string path, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Files.Remove(path));
}

public sealed class FolderMirrorSyncTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-mirror-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    // A fake reader: byte 0 is the species, byte 1 must be 0xAA for the file to be a Pokémon.
    private static BankEntryInfo? Describe(byte[] bytes, string source, string? format) =>
        bytes.Length >= 2 && bytes[1] == 0xAA ? new BankEntryInfo(bytes[0], 0, false, $"Mon{bytes[0]}", 5, 7, source, format ?? "PK7") : null;

    private static byte[] Mon(byte species, byte salt = 0) => [species, 0xAA, salt, 7];

    private sealed record Device(FileBankService Bank, BankBoxNames Names, RecentlyRemovedStore Removed, string Root, IReadOnlyList<IMirrorSource> Sources)
    {
        public MirrorState? State;
    }

    private Device NewDevice(string name)
    {
        var root = Path.Combine(_root, name);
        var bank = new FileBankService(Path.Combine(root, "bank"));
        var names = new BankBoxNames(Path.Combine(root, "bank"));
        var removed = new RecentlyRemovedStore(Path.Combine(root, "removed"));
        IMirrorSource[] sources =
        [
            new BankMirrorSource(bank, names, Describe, info => $"Mon{info.Species}", removed, () => Now),
            new DirectoryMirrorSource("Album/", Path.Combine(root, "album"), removed, () => Now),
            new FileMirrorSource("Items/item-presets.json", Path.Combine(root, "item-presets.json"), removed,
                bytes => bytes.Length > 0 && bytes[0] == (byte)'[', () => Now),
        ];
        return new Device(bank, names, removed, root, sources);
    }

    private static async Task<MirrorReport> Sync(Device device, MemoryFolder folder)
    {
        var (report, state) = await FolderMirrorSync.SyncAsync(folder, "tree", device.Sources, device.State, Now);
        device.State = state;
        return report;
    }

    private static List<(int Box, int Slot, string Hash, string? Format)> Contents(IBankService bank) =>
        [.. bank.GetAll().OrderBy(e => e.Box).ThenBy(e => e.Slot).Select(e => (e.Box, e.Slot, MirrorPlan.Hash(bank.GetData(e.Id)), e.Info.Format))];

    [Fact]
    public async Task Bank_entries_become_readable_box_folders_and_files()
    {
        var device = NewDevice("a");
        device.Bank.AddMany([new BankDeposit(Mon(25), Describe(Mon(25), "x", "PK7")!, 0, 11), new BankDeposit(Mon(1), Describe(Mon(1), "x", "PB8")!, 2, 0)]);
        device.Names.SetMany([(2, "Favourites")]);
        var folder = new MemoryFolder();

        var report = await Sync(device, folder);

        Assert.Equal(Mon(25), folder.Files["Bank/01 - Box 1/012 - Mon25.pk7"].Bytes);
        Assert.Equal(Mon(1), folder.Files["Bank/03 - Favourites/001 - Mon1.pb8"].Bytes);
        Assert.True(folder.Files.ContainsKey(BankMirrorSource.MappingPath));
        Assert.True(folder.Files.ContainsKey(FolderMirrorSync.ReadmePath));
        Assert.Equal(2, report.Pushed);
        Assert.False(report.Failed);
    }

    [Fact]
    public async Task Box_groups_travel_in_the_mapping_while_box_folders_stay_flat()
    {
        var device = NewDevice("a");
        device.Bank.AddMany([new BankDeposit(Mon(25), Describe(Mon(25), "x", "PK7")!, 2, 0)]);
        device.Bank.SetBoxGroups(BankBoxGroups.Create([], "Shiny", 2, 0));
        var folder = new MemoryFolder();

        await Sync(device, folder);

        Assert.True(folder.Files.ContainsKey("Bank/03 - Box 3/001 - Mon25.pk7")); // not nested under the group
        using var mapping = System.Text.Json.JsonDocument.Parse(folder.Files[BankMirrorSource.MappingPath].Bytes);
        var group = Assert.Single(mapping.RootElement.GetProperty("groups").EnumerateArray());
        Assert.Equal("Shiny", group.GetProperty("name").GetString());
        Assert.Equal([1, 3], group.GetProperty("boxes").EnumerateArray().Select(b => b.GetInt32()));
    }

    [Fact]
    public async Task Entries_to_files_to_entries_is_identical_bytes_and_slots()
    {
        var a = NewDevice("a");
        a.Bank.AddMany([
            new BankDeposit(Mon(25), Describe(Mon(25), "x", "PK7")!, 0, 11),
            new BankDeposit(Mon(4, 1), Describe(Mon(4), "x", "PK3")!, 0, 0),
            new BankDeposit(Mon(4, 2), Describe(Mon(4), "x", "PK3")!, 4, 29),
        ]);
        a.Names.SetMany([(4, "Shinies")]);
        var folder = new MemoryFolder();
        await Sync(a, folder);

        var b = NewDevice("b");
        var report = await Sync(b, folder);

        Assert.Equal(Contents(a.Bank), Contents(b.Bank));
        Assert.Equal("Shinies", b.Names.Get(4));
        Assert.Equal(3, report.Pulled);
        Assert.Equal(0, report.DeletedRemote);

        // A further sync on either side changes nothing.
        var again = await Sync(b, folder);
        Assert.Equal((0, 0, 0), (again.Pushed, again.Pulled, again.DeletedRemote));
        var back = await Sync(a, folder);
        Assert.Equal((0, 0, 0), (back.Pushed, back.Pulled, back.DeletedRemote));
    }

    [Fact]
    public async Task A_dropped_pk_file_is_deposited_in_its_box_and_takes_its_bank_name()
    {
        var device = NewDevice("a");
        var folder = new MemoryFolder();
        await Sync(device, folder);

        folder.Put("Bank/02 - Box 2/my charmander.pk7", Mon(4));
        var report = await Sync(device, folder);

        var entry = Assert.Single(device.Bank.GetAll());
        Assert.Equal((1, 0), (entry.Box, entry.Slot));
        Assert.Equal("PK7", entry.Info.Format);
        Assert.Equal(Mon(4), device.Bank.GetData(entry.Id));
        Assert.False(folder.Files.ContainsKey("Bank/02 - Box 2/my charmander.pk7"));
        Assert.Equal(Mon(4), folder.Files["Bank/02 - Box 2/001 - Mon4.pk7"].Bytes);
        Assert.Equal(1, report.Pulled);
    }

    [Fact]
    public async Task A_dropped_file_with_a_slot_number_lands_in_that_slot()
    {
        var device = NewDevice("a");
        var folder = new MemoryFolder();
        await Sync(device, folder);
        folder.Put("Bank/03 - Box 3/017 - whatever.pk7", Mon(7));
        await Sync(device, folder);
        var entry = Assert.Single(device.Bank.GetAll());
        Assert.Equal((2, 16), (entry.Box, entry.Slot));
    }

    [Fact]
    public async Task An_unreadable_file_is_left_alone_and_reported()
    {
        var device = NewDevice("a");
        var folder = new MemoryFolder();
        folder.Put("Bank/01 - Box 1/notes.pk7", Encoding.UTF8.GetBytes("hello"));
        var report = await Sync(device, folder);
        Assert.Empty(device.Bank.GetAll());
        Assert.Contains(report.LeftAlone, line => line.StartsWith("Bank/01 - Box 1/notes.pk7"));
        Assert.True(folder.Files.ContainsKey("Bank/01 - Box 1/notes.pk7"));
        var again = await Sync(device, folder);
        Assert.Single(again.LeftAlone);
        Assert.True(folder.Files.ContainsKey("Bank/01 - Box 1/notes.pk7"));
    }

    [Fact]
    public async Task A_file_moved_in_the_folder_moves_the_pokemon_keeping_its_id()
    {
        var device = NewDevice("a");
        var added = device.Bank.AddMany([new BankDeposit(Mon(25), Describe(Mon(25), "x", "PK7")!, 0, 0)]).Single();
        var folder = new MemoryFolder();
        await Sync(device, folder);

        var bytes = folder.Files["Bank/01 - Box 1/001 - Mon25.pk7"].Bytes;
        folder.Files.Remove("Bank/01 - Box 1/001 - Mon25.pk7");
        folder.Put("Bank/02 - Box 2/005 - Mon25.pk7", bytes);
        var report = await Sync(device, folder);

        var entry = Assert.Single(device.Bank.GetAll());
        Assert.Equal(added.Id, entry.Id);
        Assert.Equal((1, 4), (entry.Box, entry.Slot));
        Assert.Empty(device.Removed.List());
        Assert.Equal(0, report.MovedToRecentlyRemoved);
        Assert.Equal(["Bank/02 - Box 2/005 - Mon25.pk7"], folder.Files.Keys.Where(k => k.EndsWith(".pk7")));
    }

    [Fact]
    public async Task A_file_edited_in_the_folder_updates_the_entry_in_place()
    {
        var device = NewDevice("a");
        var added = device.Bank.AddMany([new BankDeposit(Mon(25), Describe(Mon(25), "x", "PK7")!, 0, 0)]).Single();
        var folder = new MemoryFolder();
        await Sync(device, folder);
        folder.Put("Bank/01 - Box 1/001 - Mon25.pk7", Mon(25, 9));
        await Sync(device, folder);
        var entry = Assert.Single(device.Bank.GetAll());
        Assert.Equal(added.Id, entry.Id);
        Assert.Equal(Mon(25, 9), device.Bank.GetData(entry.Id));
    }

    [Fact]
    public async Task A_file_deleted_in_the_folder_goes_to_recently_removed_and_can_be_restored()
    {
        var device = NewDevice("a");
        device.Bank.AddMany([new BankDeposit(Mon(25), Describe(Mon(25), "x", "PK7")!, 1, 4)]);
        var folder = new MemoryFolder();
        await Sync(device, folder);
        folder.Files.Remove("Bank/02 - Box 2/005 - Mon25.pk7");
        var report = await Sync(device, folder);

        Assert.Empty(device.Bank.GetAll());
        Assert.Equal(1, report.MovedToRecentlyRemoved);
        var held = Assert.Single(device.Removed.List());
        Assert.Equal(Mon(25), device.Removed.Read(held));

        device.Removed.Restore(held, device.Bank);
        var back = Assert.Single(device.Bank.GetAll());
        Assert.Equal(Mon(25), device.Bank.GetData(back.Id));
        Assert.Equal((1, 4), (back.Box, back.Slot)); // the slot it left
        Assert.Empty(device.Removed.List());
        await Sync(device, folder);
        Assert.True(folder.Files.ContainsKey("Bank/02 - Box 2/005 - Mon25.pk7"));
    }

    [Fact]
    public async Task Recently_removed_keeps_thirty_days()
    {
        var store = new RecentlyRemovedStore(Path.Combine(_root, "held"));
        store.Put([1], "a", null, null, Now.AddDays(-31));
        store.Put([2], "b", null, null, Now.AddDays(-2));
        Assert.Equal(1, store.Prune(Now));
        Assert.Equal("b", Assert.Single(store.List()).FolderPath);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_release_here_deletes_the_file_there()
    {
        var device = NewDevice("a");
        var added = device.Bank.AddMany([new BankDeposit(Mon(25), Describe(Mon(25), "x", "PK7")!, 0, 0)]).Single();
        var folder = new MemoryFolder();
        await Sync(device, folder);
        device.Bank.Remove(added.Id);
        var report = await Sync(device, folder);
        Assert.Equal(1, report.DeletedRemote);
        Assert.DoesNotContain(folder.Files.Keys, k => k.EndsWith(".pk7"));
    }

    [Fact]
    public async Task First_sync_against_a_full_folder_never_deletes_either_side()
    {
        var device = NewDevice("a");
        device.Bank.AddMany([new BankDeposit(Mon(25), Describe(Mon(25), "x", "PK7")!, 0, 0)]);
        var folder = new MemoryFolder();
        folder.Put("Bank/01 - Box 1/002 - Mon1.pk7", Mon(1));
        folder.Put("Album/old.jpg", [9, 9]);
        await Sync(device, folder);
        Assert.Equal(2, device.Bank.GetAll().Count);
        Assert.True(File.Exists(Path.Combine(device.Root, "album", "old.jpg")));
        Assert.True(folder.Files.ContainsKey("Bank/01 - Box 1/001 - Mon25.pk7"));
        Assert.True(folder.Files.ContainsKey("Album/old.jpg"));
    }

    [Fact]
    public async Task A_conflict_keeps_both_copies()
    {
        var device = NewDevice("a");
        var presets = Path.Combine(device.Root, "item-presets.json");
        Directory.CreateDirectory(device.Root);
        File.WriteAllText(presets, "[1]");
        var folder = new MemoryFolder();
        await Sync(device, folder);

        File.WriteAllText(presets, "[2]");
        folder.Put("Items/item-presets.json", Encoding.UTF8.GetBytes("[3]"));
        var report = await Sync(device, folder);

        Assert.Equal(["Items/item-presets.json"], report.Conflicts);
        Assert.Equal("[2]", Encoding.UTF8.GetString(folder.Files["Items/item-presets.json"].Bytes));
        Assert.Equal("[3]", Encoding.UTF8.GetString(folder.Files["Items/item-presets (conflict 2026-11-10).json"].Bytes));
        Assert.Equal("[2]", File.ReadAllText(presets));
    }

    [Fact]
    public async Task A_bank_conflict_deposits_the_folders_copy_as_another_pokemon()
    {
        var device = NewDevice("a");
        var added = device.Bank.AddMany([new BankDeposit(Mon(25), Describe(Mon(25), "x", "PK7")!, 0, 0)]).Single();
        var folder = new MemoryFolder();
        await Sync(device, folder);
        device.Bank.Replace(added.Id, Mon(25, 1), Describe(Mon(25, 1), "x", "PK7")!);
        folder.Put("Bank/01 - Box 1/001 - Mon25.pk7", Mon(25, 2));
        var report = await Sync(device, folder);

        Assert.Single(report.Conflicts);
        var hashes = device.Bank.GetAll().Select(e => MirrorPlan.Hash(device.Bank.GetData(e.Id))).ToHashSet();
        Assert.Equal(new[] { MirrorPlan.Hash(Mon(25, 1)), MirrorPlan.Hash(Mon(25, 2)) }.Order(), hashes.Order());
        Assert.Equal(2, folder.Files.Keys.Count(k => k.EndsWith(".pk7")));
    }

    [Fact]
    public async Task Album_photos_go_both_ways_and_a_deletion_there_is_held_here()
    {
        var device = NewDevice("a");
        var album = Path.Combine(device.Root, "album");
        Directory.CreateDirectory(album);
        File.WriteAllBytes(Path.Combine(album, "1.jpg"), [1, 2, 3]);
        var folder = new MemoryFolder();
        await Sync(device, folder);
        Assert.Equal([1, 2, 3], folder.Files["Album/1.jpg"].Bytes);

        folder.Put("Album/2.jpg", [4]);
        folder.Files.Remove("Album/1.jpg");
        var report = await Sync(device, folder);
        Assert.True(File.Exists(Path.Combine(album, "2.jpg")));
        Assert.False(File.Exists(Path.Combine(album, "1.jpg")));
        Assert.Equal(1, report.MovedToRecentlyRemoved);
        Assert.Equal([1, 2, 3], device.Removed.Read(Assert.Single(device.Removed.List())));
    }

    [Fact]
    public async Task Unchanged_folder_files_are_not_read_again()
    {
        var device = NewDevice("a");
        device.Bank.AddMany([new BankDeposit(Mon(25), Describe(Mon(25), "x", "PK7")!, 0, 0)]);
        var folder = new MemoryFolder();
        folder.Put("Notes/mine.txt", [1]);
        await Sync(device, folder);
        await Sync(device, folder); // what the first sync wrote is read back once: its stamp was not trusted
        folder.Reads = 0;
        await Sync(device, folder);
        Assert.Equal(0, folder.Reads);
        Assert.True(folder.Files.ContainsKey("Notes/mine.txt")); // files no part of PKForge owns are never touched
    }

    [Fact]
    public async Task A_locked_box_refuses_a_dropped_file_and_keeps_it()
    {
        var device = NewDevice("a");
        device.Bank.AddBox();
        device.Bank.SetBoxLocked(0, true);
        var folder = new MemoryFolder();
        folder.Put("Bank/01 - Box 1/003 - x.pk7", Mon(3));
        var report = await Sync(device, folder);
        Assert.Empty(device.Bank.GetAll());
        Assert.Single(report.LeftAlone);
        Assert.True(folder.Files.ContainsKey("Bank/01 - Box 1/003 - x.pk7"));
    }

    [Theory]
    [InlineData("Bank/01 - Box 1/012 - Pikachu.pk7", 0, 11, null)]
    [InlineData("Bank/12 - Legends/030 - Mew.pk3", 11, 29, "Legends")]
    [InlineData("Bank/Stuff/a.pk7", -1, -1, null)]
    [InlineData("Bank/a.pk7", -1, -1, null)]
    [InlineData("Bank/02/045 - x.pk7", 1, -1, null)]
    public void Folder_paths_name_their_box_and_slot(string path, int box, int slot, string? name) =>
        Assert.Equal((box, slot, name), BankMirrorSource.Place(path));
}
