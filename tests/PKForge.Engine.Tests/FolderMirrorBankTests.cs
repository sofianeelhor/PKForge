using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>The PKForge folder with real PKHeX entities: same-size formats (PK8/PB8) keep
/// their exact type through Bank → folder files → another Bank, bytes identical.</summary>
public sealed class FolderMirrorBankTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-mirror-real-" + Guid.NewGuid().ToString("N"));
    private readonly SaveEngine _engine = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class Folder : IFolderTreeAccess
    {
        public readonly Dictionary<string, byte[]> Files = new(MirrorPlan.PathComparer);

        public ValueTask<IReadOnlyList<FolderEntry>> ListTreeAsync(string treeId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<FolderEntry>>([.. Files.Select(f => new FolderEntry(f.Key, f.Value.Length, DateTimeOffset.UnixEpoch))]);

        public ValueTask<byte[]> ReadAsync(string treeId, string path, CancellationToken cancellationToken = default) => ValueTask.FromResult(Files[path]);

        public ValueTask WriteAsync(string treeId, string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
        {
            Files[path] = bytes.ToArray();
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> DeleteAsync(string treeId, string path, CancellationToken cancellationToken = default) => ValueTask.FromResult(Files.Remove(path));
    }

    private (FileBankService Bank, IMirrorSource[] Sources) Device(string name)
    {
        var root = Path.Combine(_root, name);
        var bank = new FileBankService(Path.Combine(root, "bank"));
        var source = new BankMirrorSource(bank, new BankBoxNames(Path.Combine(root, "bank")),
            (bytes, file, format) => _engine.TryDescribeEntity(bytes, file, format),
            info => GameInfo.GetStrings("en").specieslist[info.Species], new RecentlyRemovedStore(Path.Combine(root, "removed")));
        return (bank, [source]);
    }

    private static byte[] Stored(PKM pk)
    {
        pk.Species = 25;
        pk.CurrentLevel = 30;
        pk.PID = 0x12345678;
        pk.EncryptionConstant = 0x9ABCDEF0;
        pk.Move1 = 33;
        pk.RefreshChecksum();
        return pk.Data[..pk.SIZE_STORED].ToArray();
    }

    [Fact]
    public async Task Real_entities_round_trip_through_the_folder_with_their_exact_format()
    {
        var (a, aSources) = Device("a");
        foreach (var pk in new PKM[] { new PK8(), new PB8(), new PK7(), new PK3() })
        {
            var bytes = Stored(pk);
            a.AddMany([new BankDeposit(bytes, _engine.TryDescribeEntity(bytes, "test", pk.GetType().Name)!)]);
        }
        var folder = new Folder();
        await FolderMirrorSync.SyncAsync(folder, "t", aSources, null, DateTimeOffset.UtcNow);
        Assert.Contains("Bank/01 - Box 1/002 - Pikachu.pb8", folder.Files.Keys);

        var (b, bSources) = Device("b");
        await FolderMirrorSync.SyncAsync(folder, "t", bSources, null, DateTimeOffset.UtcNow);

        static List<(int, int, string?, string)> Of(IBankService bank) =>
            [.. bank.GetAll().OrderBy(e => e.Slot).Select(e => (e.Box, e.Slot, e.Info.Format, Convert.ToHexString(bank.GetData(e.Id))))];
        Assert.Equal(Of(a), Of(b));
        Assert.Equal(["PK8", "PB8", "PK7", "PK3"], b.GetAll().OrderBy(e => e.Slot).Select(e => e.Info.Format));
    }
}
