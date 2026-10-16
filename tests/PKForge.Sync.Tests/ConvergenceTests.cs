using PKForge.Domain;
using PKForge.Sync.Protocol;
using PKForge.Sync.Tests.Infrastructure;
using Xunit;

namespace PKForge.Sync.Tests;

/// <summary>
/// Three devices make random offline edits and sync in random order; conflicts are resolved the way a
/// careful person would (keep both, keep the edited copy). Afterwards every device holds the same Bank,
/// and every Pokémon's bytes that no device released or replaced are still there (spec §12).
/// </summary>
public sealed class ConvergenceTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task RandomOfflineEditsConvergeWithoutLosingAPokemon(int seed)
    {
        var random = new Random(seed);
        using var server = new SyncServer();
        var devices = new List<TestDevice> { new(server, await server.SignUpAsync()) };
        await devices[0].JoinAsync();
        for (var i = 1; i < 3; i++)
        {
            devices.Add(new TestDevice(server, await server.SignInAsync(device: $"Device {i}")));
            await devices[i].JoinAsync();
        }

        var created = new HashSet<string>();
        var doomed = new HashSet<string>();
        var nextSeed = 1000 * seed;
        try
        {
            for (var round = 0; round < 6; round++)
            {
                foreach (var device in devices)
                {
                    for (var op = 0; op < 4; op++) Edit(device, random, created, doomed, ref nextSeed);
                    device.Time.Advance(TimeSpan.FromSeconds(random.Next(1, 60)));
                }
                foreach (var device in devices.OrderBy(_ => random.Next())) await SyncAndResolveAsync(device);
            }

            for (var pass = 0; pass < 4; pass++)
            {
                foreach (var device in devices) await SyncAndResolveAsync(device);
            }

            TestDevice.SameContents([.. devices]);
            var held = devices[0].Bank.GetAll().Select(e => BlobHash.Compute(devices[0].Bank.GetData(e.Id))).ToHashSet();
            Assert.Empty(created.Except(doomed).Except(held));
            Assert.All(devices, d => Assert.Empty(d.Store.Read(s => s.Conflicts)));
        }
        finally
        {
            foreach (var device in devices) device.Dispose();
        }
    }

    private static void Edit(TestDevice device, Random random, HashSet<string> created, HashSet<string> doomed, ref int nextSeed)
    {
        var entries = device.Bank.GetAll();
        switch (entries.Count == 0 ? 0 : random.Next(11))
        {
            case 0:
            case 1:
                var bytes = TestData.Bytes(nextSeed++);
                created.Add(BlobHash.Compute(bytes));
                device.Bank.Add(bytes, TestData.Info(random.Next(1, 1000), $"N{nextSeed}"));
                break;
            case 2:
                var mover = entries[random.Next(entries.Count)];
                device.Bank.Move(mover.Id, random.Next(device.Bank.BoxCount), random.Next(IBankService.SlotsPerBox));
                break;
            case 3:
                var released = entries[random.Next(entries.Count)];
                doomed.Add(BlobHash.Compute(device.Bank.GetData(released.Id)));
                device.Bank.Remove(released.Id);
                break;
            case 4:
                var edited = entries[random.Next(entries.Count)];
                var newBytes = TestData.Bytes(nextSeed++);
                doomed.Add(BlobHash.Compute(device.Bank.GetData(edited.Id)));
                created.Add(BlobHash.Compute(newBytes));
                device.Bank.Replace(edited.Id, newBytes, edited.Info with { Nickname = $"E{nextSeed}" });
                break;
            case 5:
                device.Bank.Decor.Names.SetMany([(random.Next(device.Bank.BoxCount), $"Box{random.Next(100)}")]);
                break;
            case 6:
                device.Bank.AddBox();
                break;
            case 7:
                var count = device.Bank.BoxCount;
                var remap = random.Next(3) switch
                {
                    0 => BankBoxRemap.Swap(count, random.Next(count), random.Next(count)),
                    1 => BankBoxRemap.Move(count, random.Next(count), random.Next(count)),
                    _ => BankBoxRemap.Insert(count, random.Next(count + 1)),
                };
                device.Bank.RemapBoxes(remap);
                device.Bank.Decor.Remap(remap);
                break;
            case 8:
                // An organizer sort: a few Pokémon trade places.
                var sorted = entries.OrderBy(_ => random.Next()).Take(Math.Min(4, entries.Count)).ToList();
                var places = sorted.Select(e => (e.Box, e.Slot)).OrderBy(_ => random.Next()).ToList();
                device.Bank.Place(sorted.Select((e, i) => (e.Id, places[i].Box, places[i].Slot)).ToList());
                break;
            case 9:
                var deposits = new List<BankDeposit>();
                for (var i = 0; i < 2; i++)
                {
                    var data = TestData.Bytes(nextSeed++);
                    created.Add(BlobHash.Compute(data));
                    deposits.Add(new BankDeposit(data, TestData.Info(random.Next(1, 1000), $"N{nextSeed}")));
                }
                device.Bank.AddMany(deposits);
                break;
            default:
                // A background fact rewrite, like the held-item backfill.
                var backfilled = entries[random.Next(entries.Count)];
                device.Bank.UpdateInfo([(backfilled.Id, backfilled.Info with { HeldItem = random.Next(1, 50) })]);
                break;
        }
    }

    private static async Task SyncAndResolveAsync(TestDevice device)
    {
        await device.SyncAsync();
        foreach (var conflict in device.Store.Read(s => s.Conflicts.ToList()))
        {
            var choice = conflict.Kind switch
            {
                ConflictKind.BothEdited => ConflictChoice.KeepBoth,
                ConflictKind.EditedHereReleasedThere => ConflictChoice.KeepThisDevice,
                ConflictKind.ReleasedHereEditedThere => ConflictChoice.KeepOtherDevice,
                _ => ConflictChoice.KeepThisDevice,
            };
            await device.Engine.ResolveAsync(conflict.Id, choice);
        }
        if (device.Store.Read(s => s.Conflicts.Count) == 0) await device.SyncAsync();
    }
}
