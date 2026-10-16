using Xunit;

namespace PKForge.Sync.Tests;

public sealed class SyncStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void StatePersistsAcrossRestarts()
    {
        var store = new SyncStateStore(_root);
        store.Update(s =>
        {
            s.ServerUrl = "http://nas:8080/";
            s.Cursor = 41;
            s.Adopt("mon:x", 41, "abc");
        });

        var reopened = new SyncStateStore(_root);

        Assert.Equal(("http://nas:8080/", 41L), reopened.Read(s => (s.ServerUrl, s.Cursor)));
        Assert.Equal(new KnownItem(41, "abc"), reopened.Read(s => s.Known["mon:x"]));
    }

    [Fact]
    public void UnsavedChangesWaitForFlush()
    {
        var store = new SyncStateStore(_root);
        store.Update(s => s.Cursor = 7, persist: false);

        Assert.Equal(0, new SyncStateStore(_root).Read(s => s.Cursor));
        store.Flush();
        Assert.Equal(7, new SyncStateStore(_root).Read(s => s.Cursor));
    }

    [Fact]
    public void RecordedChangesMergeIntoUnsentGroupsOnly()
    {
        var store = new SyncStateStore(_root);
        store.Update(s => s.RecordChange(["mon:a", "mon:b"], background: false));
        store.Update(s => s.RecordChange(["mon:b", "mon:c"], background: true));
        Assert.Equal(["mon:a", "mon:b", "mon:c"], store.Read(s => s.Pending.Single().Keys.Order().ToList()));
        Assert.False(store.Read(s => s.Pending.Single().Background));

        store.Update(s => s.Pending.Single().Sent = true);
        store.Update(s => s.RecordChange(["mon:c", "mon:d"], background: true));

        Assert.Equal(2, store.Read(s => s.Pending.Count));
        Assert.True(store.Read(s => s.Pending[1].Background));
    }

    [Fact]
    public void AdoptingAnItemClearsWhatWasWaitingOnIt()
    {
        var store = new SyncStateStore(_root);
        store.Update(s =>
        {
            s.AddInFlight("mon:a", "f1");
            s.Refused["mon:a"] = "f1";
            s.Adopt("mon:a", 9, "f2");
        });

        Assert.False(store.Read(s => s.IsInFlight("mon:a", "f1")));
        Assert.False(store.Read(s => s.Refused.ContainsKey("mon:a")));
    }

    [Fact]
    public void TheActivityLogKeepsTheNewestEntries()
    {
        var store = new SyncStateStore(_root);
        store.Update(s =>
        {
            for (var i = 0; i < SyncStateData.ActivityLimit + 5; i++) s.AddActivity(DateTimeOffset.UnixEpoch.AddMinutes(i), $"#{i}");
        });

        Assert.Equal(SyncStateData.ActivityLimit, store.Read(s => s.Activity.Count));
        Assert.Equal("#5", store.Read(s => s.Activity[0].Message));
    }

    [Fact]
    public void AnUnreadableStateFileAsksForAFullResync()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "sync-state.json"), "{broken");

        var store = new SyncStateStore(_root);

        Assert.True(store.Read(s => s.NeedsResync));
        Assert.False(store.Read(s => s.Joined));
    }
}
