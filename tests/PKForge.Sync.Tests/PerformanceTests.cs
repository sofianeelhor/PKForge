using System.Diagnostics;
using System.Net.Http;
using PKForge.Domain;
using PKForge.Sync.Tests.Infrastructure;
using Xunit;

namespace PKForge.Sync.Tests;

/// <summary>Sync work grows with the number of changes, not with changes × Bank size.</summary>
public sealed class PerformanceTests
{
    [Fact]
    public async Task TheSameBoxInsertedOnTwoDevicesSettlesQuickly()
    {
        using var server = new SyncServer();
        using var phone = new TestDevice(server, await server.SignUpAsync());
        phone.Bank.AddMany(Enumerable.Range(1, 2000).Select(i => new BankDeposit(TestData.Bytes(i), TestData.Info(25, $"N{i}"))).ToList());
        await phone.JoinAsync();
        using var deck = new TestDevice(server, await server.SignInAsync());
        await deck.JoinAsync();

        phone.Time.Advance(TimeSpan.FromMinutes(2));   // the phone moved them later: the deck takes every move
        foreach (var device in new[] { phone, deck })
        {
            var remap = BankBoxRemap.Insert(device.Bank.BoxCount, 0);
            device.Bank.RemapBoxes(remap);
            device.Bank.Decor.Remap(remap);
        }
        await phone.SyncAsync();
        var clock = Stopwatch.StartNew();
        var report = await deck.SyncAsync();
        clock.Stop();

        Assert.Equal(0, report.NewConflicts);
        TestDevice.SameContents(phone, deck);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"Took {clock.Elapsed}.");
    }

    private sealed class PushCounter : DelegatingHandler
    {
        public int Pushes;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/v1/sync/push", StringComparison.Ordinal)) Interlocked.Increment(ref Pushes);
            return base.SendAsync(request, ct);
        }
    }

    [Fact]
    public async Task ABankJoiningWithoutRecordedChangesUploadsInModestRequests()
    {
        using var server = new SyncServer();
        var counter = new PushCounter();
        using var phone = new TestDevice(server, await server.SignUpAsync(), handler: counter);
        phone.Bank.AddMany(Enumerable.Range(1, 2500).Select(i => new BankDeposit(TestData.Bytes(i), TestData.Info(25, $"N{i}"))).ToList());
        phone.Store.Update(s => s.Pending.Clear());     // as after signing in: nothing recorded, everything new to the server

        var report = await phone.JoinAsync();
        using var deck = new TestDevice(server, await server.SignInAsync());
        await deck.JoinAsync();

        Assert.False(report.PendingChanges);
        Assert.True(counter.Pushes >= 2, $"{counter.Pushes} push request(s).");
        Assert.Equal(2500, deck.Bank.GetAll().Count);
    }
}
