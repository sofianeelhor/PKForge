using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class InjectedGiftHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pkforge-tests", Guid.NewGuid().ToString("N"));

    private string PathFor(string name = "injected-gifts.json") => Path.Combine(_root, name);

    private static EventGift Gift(int cardId = 1, string title = "Test Card") =>
        new(0, title, $"Card #: {cardId:0000} - {title}", 25, 50, false, cardId, 7, 0, 2018);

    private static readonly EventGiftSaveProfile Moon = new(7, 2, "Ultra Moon");
    private static readonly EventGiftSaveProfile Sword = new(8, 2, "Sword");

    [Fact]
    public void RecordedKeysSurviveAReload()
    {
        var key = InjectedGiftHistory.KeyFor(Gift(), Moon);
        new InjectedGiftHistory(PathFor()).Record(key);

        var reloaded = new InjectedGiftHistory(PathFor());

        Assert.Equal(1, reloaded.Count);
        Assert.True(reloaded.IsInjected(key));
    }

    [Fact]
    public void RecordingTheSameCardTwiceCountsOnce()
    {
        var history = new InjectedGiftHistory(PathFor());
        var key = InjectedGiftHistory.KeyFor(Gift(), Moon);

        history.Record(key);
        history.Record(key);

        Assert.Equal(1, history.Count);
    }

    [Fact]
    public void UnknownKeysAreNotInjected()
    {
        var history = new InjectedGiftHistory(PathFor());

        Assert.Equal(0, history.Count);
        Assert.False(history.IsInjected(InjectedGiftHistory.KeyFor(Gift(), Moon)));
    }

    [Fact]
    public void ClearForgetsEverythingAndPersists()
    {
        var path = PathFor();
        var history = new InjectedGiftHistory(path);
        history.Record(InjectedGiftHistory.KeyFor(Gift(), Moon));
        history.Record(InjectedGiftHistory.KeyFor(Gift(cardId: 2, title: "Other Card"), Moon));

        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Equal(0, new InjectedGiftHistory(path).Count);
    }

    [Fact]
    public void KeysSeparateGamesCardNumbersAndTitles()
    {
        // Same distribution injected into two games: two markers.
        Assert.NotEqual(InjectedGiftHistory.KeyFor(Gift(), Moon), InjectedGiftHistory.KeyFor(Gift(), Sword));
        // Same game, different card: two markers.
        Assert.NotEqual(InjectedGiftHistory.KeyFor(Gift(), Moon), InjectedGiftHistory.KeyFor(Gift(cardId: 2), Moon));
        Assert.NotEqual(InjectedGiftHistory.KeyFor(Gift(), Moon), InjectedGiftHistory.KeyFor(Gift(title: "Rename"), Moon));
        // The same card re-received in the same game: one marker.
        Assert.Equal(InjectedGiftHistory.KeyFor(Gift(), Moon), InjectedGiftHistory.KeyFor(Gift(), Moon));
    }

    [Fact]
    public void TheReceiveIndexIsNotPartOfTheKey()
    {
        var first = Gift() with { Id = 12 };
        var afterArchiveGrowth = Gift() with { Id = 913 };

        Assert.Equal(InjectedGiftHistory.KeyFor(first, Moon), InjectedGiftHistory.KeyFor(afterArchiveGrowth, Moon));
    }

    [Fact]
    public void ACorruptLedgerStartsEmptyInsteadOfThrowing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(PathFor(), "{ not json");

        var history = new InjectedGiftHistory(PathFor());

        Assert.Equal(0, history.Count);
        history.Record(InjectedGiftHistory.KeyFor(Gift(), Moon)); // still usable afterwards
        Assert.Equal(1, new InjectedGiftHistory(PathFor()).Count);
    }

    [Fact]
    public void APathlessHistoryWorksInMemoryOnly()
    {
        var history = new InjectedGiftHistory(null);
        var key = InjectedGiftHistory.KeyFor(Gift(), Moon);

        history.Record(key);

        Assert.True(history.IsInjected(key));
        Assert.Equal(1, history.Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
