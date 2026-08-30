using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class MysteryGiftInboxTests
{
    [Fact]
    public void StoredWonderCardIsExposedWithoutChangingIt()
    {
        var save = new SAV7USUM();
        var card = new WC7
        {
            CardID = 1234,
            CardTitle = "Test Pikachu",
            IsEntity = true,
            Species = 25,
            Level = 30,
            GiftUsed = false,
        };
        ((IMysteryGiftStorageProvider)save).MysteryGiftStorage.SetMysteryGift(0, card);
        var before = save.Write().ToArray();

        using var session = new SaveEngineSession(save, null);
        var inbox = session.GetMysteryGiftInbox();

        var stored = Assert.Single(inbox.Cards);
        Assert.True(inbox.Supported);
        Assert.Equal(0, stored.Slot);
        Assert.Equal(1234, stored.CardId);
        Assert.Equal("Test Pikachu", stored.Title);
        Assert.Equal(25, stored.Species);
        Assert.Equal(30, stored.Level);
        Assert.False(stored.GiftUsed);
        Assert.False(stored.IsReceivedRecord);
        Assert.Equal(before, session.Serialize().ToArray());
    }

    [Fact]
    public void UnsupportedSaveHasNoInbox()
    {
        using var session = new SaveEngine().OpenBlankSession(9);
        var inbox = session.GetMysteryGiftInbox();
        Assert.False(inbox.Supported);
        Assert.Empty(inbox.Cards);
    }
}
