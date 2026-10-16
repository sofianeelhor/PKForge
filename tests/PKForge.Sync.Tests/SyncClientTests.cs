using System.Text.Json;
using PKForge.Sync.Protocol;
using PKForge.Sync.Tests.Infrastructure;
using Xunit;

namespace PKForge.Sync.Tests;

public sealed class SyncClientTests
{
    [Fact]
    public async Task ServerInfoNeedsNoAccount()
    {
        using var server = new SyncServer();
        var info = await server.Client().GetServerInfoAsync();
        Assert.Equal(SyncProtocol.Version, info.MaxProtocol);
    }

    [Fact]
    public async Task ServerErrorsBecomeSyncApiExceptions()
    {
        using var server = new SyncServer();
        await server.SignUpAsync();

        var error = await Assert.ThrowsAsync<SyncApiException>(() =>
            server.Client().LoginAsync(new LoginRequest("ash@example.com", "wrong-password", "Deck")));

        Assert.Equal(401, error.Status);
        Assert.Equal(ErrorCodes.InvalidCredentials, error.Code);
    }

    [Fact]
    public async Task BlobsPushAndChangesRoundTrip()
    {
        using var server = new SyncServer();
        var client = server.Client(await server.SignUpAsync());
        var bytes = TestData.Bytes(1);
        var hash = BlobHash.Compute(bytes);

        Assert.Equal([hash], await client.MissingBlobsAsync([hash]));
        await client.UploadBlobsAsync([new BlobData(hash, bytes)]);
        var entry = new PKForge.Domain.BankEntry(Guid.NewGuid(), 0, 0, TestData.Info(25, "Sparky"), TestData.T0);
        var push = await client.PushAsync(new PushRequest([new ChangeGroup(Guid.NewGuid(),
            [new ItemWrite(ItemKinds.Mon, entry.Id.ToString(), 0, SyncValues.Mon(entry, hash, TestData.T0))])]));
        var changes = await client.GetChangesAsync(0, null, 100);
        var downloaded = await client.DownloadBlobsAsync([hash]);

        Assert.Equal(GroupStatus.Accepted, push.Results[0].Status);
        Assert.Equal(entry, SyncValues.ToEntry(entry.Id, SyncValues.ReadMon(Assert.Single(changes.Items).Value!.Value)));
        Assert.Equal(bytes, Assert.Single(downloaded).Data);
        Assert.Empty(await client.MissingBlobsAsync([hash]));
    }

    [Fact]
    public async Task DevicesAndPairingCodes()
    {
        using var server = new SyncServer();
        var phone = server.Client(await server.SignUpAsync());

        var code = await phone.CreatePairingCodeAsync();
        var deck = await server.Client().RedeemPairingAsync(new RedeemPairingRequest(code.Code, "Deck"));
        var devices = await phone.GetDevicesAsync();
        await phone.RemoveDeviceAsync(deck.DeviceId);

        Assert.Equal(["Phone", "Deck"], devices.Select(d => d.Name).Order().Reverse());
        var error = await Assert.ThrowsAsync<SyncApiException>(() => server.Client(deck).GetDevicesAsync());
        Assert.Equal(401, error.Status);
    }
}
