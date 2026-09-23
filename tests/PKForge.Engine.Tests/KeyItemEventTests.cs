using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Key-item Mystery Gift events on blank retail saves: the item alone must read as half-armed,
/// Enable must write the item plus exactly the distribution's flags/vars, Disable must undo
/// them without touching history.
/// </summary>
public sealed class KeyItemEventTests
{
    private static SaveEngineSession Blank(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);

    [Theory]
    [InlineData(GameVersion.R, "rs-eon", "Eon Ticket")]
    [InlineData(GameVersion.E, "e-eon", "Eon Ticket")]
    [InlineData(GameVersion.E, "e-aurora", "AuroraTicket")]
    [InlineData(GameVersion.E, "e-oldseamap", "Old Sea Map")]
    [InlineData(GameVersion.E, "e-mystic", "MysticTicket")]
    [InlineData(GameVersion.FR, "frlg-aurora", "AuroraTicket")]
    [InlineData(GameVersion.LG, "frlg-mystic", "MysticTicket")]
    [InlineData(GameVersion.Pt, "pt-member", "Member Card")]
    [InlineData(GameVersion.Pt, "pt-oak", "Oak’s Letter")]
    [InlineData(GameVersion.Pt, "pt-azure", "Azure Flute")]
    [InlineData(GameVersion.Pt, "pt-secret", "Secret Key (Pt)")]
    [InlineData(GameVersion.D, "dp-member", "Member Card")]
    [InlineData(GameVersion.P, "dp-azure", "Azure Flute")]
    public void EnableThenDisableRoundTrips(GameVersion version, string id, string itemName)
    {
        using var session = Blank(version);
        Assert.Equal(KeyItemEventState.NotEnabled, Status(session, id).State);

        Assert.True(KeyItemEventService.Enable(session, id).Success);
        var enabled = Status(session, id);
        Assert.Equal(KeyItemEventState.Enabled, enabled.State);
        Assert.True(enabled.HasItem);
        Assert.True(enabled.EventArmed);
        Assert.Equal(itemName, enabled.ItemName); // item id matches the game's own item table

        Assert.True(KeyItemEventService.Disable(session, id).Success);
        var disabled = Status(session, id);
        Assert.Equal(KeyItemEventState.NotEnabled, disabled.State);
        Assert.False(disabled.HasItem);
    }

    [Fact]
    public void EmeraldAuroraWritesTheGiftScriptFlags()
    {
        using var session = Blank(GameVersion.E);
        var save = (SAV3)session.SaveFile;
        KeyItemEventService.Enable(session, "e-aurora");

        // pokeemerald gift_aurora_ticket.inc: FLAG_ENABLE_SHIP_BIRTH_ISLAND + FLAG_RECEIVED_AURORA_TICKET.
        Assert.True(save.GetEventFlag(0x8D5));
        Assert.True(save.GetEventFlag(0x13A));
        Assert.False(save.GetEventFlag(0x1AF)); // not "shown" yet - the sailor sets that
        Assert.False(save.GetEventFlag(0x8B3)); // no other ferry opened
        Assert.Contains(session.GetBag(), pouch => pouch.Name == nameof(InventoryType.KeyItems)
            && pouch.Items.Any(item => item.Id == 371 && item.Count == 1));
    }

    [Fact]
    public void ItemOnlyReadsAsPartial()
    {
        using var session = Blank(GameVersion.E);
        session.SetItemCount(nameof(InventoryType.KeyItems), 370, 1);
        var status = Status(session, "e-mystic");
        Assert.Equal(KeyItemEventState.Partial, status.State);
        Assert.True(status.HasItem);
        Assert.False(status.EventArmed);

        KeyItemEventService.Enable(session, "e-mystic");
        Assert.Equal(KeyItemEventState.Enabled, Status(session, "e-mystic").State);
        Assert.Single(session.GetBag().SelectMany(p => p.Items), item => item.Id == 370);
    }

    [Fact]
    public void ShownAndCaughtFlagsReportProgressAndSurviveDisable()
    {
        using var session = Blank(GameVersion.E);
        var save = (SAV3)session.SaveFile;
        KeyItemEventService.Enable(session, "e-oldseamap");
        save.SetEventFlag(0x1B0, true); // FLAG_SHOWN_OLD_SEA_MAP
        Assert.Equal(KeyItemEventState.Used, Status(session, "e-oldseamap").State);
        save.SetEventFlag(0x1CA, true); // FLAG_CAUGHT_MEW
        Assert.Equal(KeyItemEventState.Completed, Status(session, "e-oldseamap").State);

        KeyItemEventService.Disable(session, "e-oldseamap");
        Assert.True(save.GetEventFlag(0x1B0));
        Assert.True(save.GetEventFlag(0x1CA));
        Assert.False(save.GetEventFlag(0x8D6));
    }

    [Fact]
    public void GameClearPrerequisiteIsReportedNotWritten()
    {
        using var session = Blank(GameVersion.E);
        var save = (SAV3)session.SaveFile;
        Assert.NotNull(Status(session, "e-eon").Prerequisite);
        KeyItemEventService.Enable(session, "e-eon");
        Assert.False(save.GetEventFlag(0x864));
        save.SetEventFlag(0x864, true);
        Assert.Null(Status(session, "e-eon").Prerequisite);
    }

    [Fact]
    public void PlatinumMagicVarsMatchTheDisassembly()
    {
        using var session = Blank(GameVersion.Pt);
        var save = (SAV4)session.SaveFile;
        foreach (var id in new[] { "pt-member", "pt-oak", "pt-azure", "pt-secret" })
            KeyItemEventService.Enable(session, id);

        // pokeplatinum system_vars.c sDistributionEventMagicNumbers.
        Assert.Equal(0x1209, save.GetWork(67));
        Assert.Equal(0x1112, save.GetWork(68));
        Assert.Equal(0x1123, save.GetWork(69));
        Assert.Equal(0x1103, save.GetWork(70));
        Assert.Equal(1, save.GetWork(87)); // InitShayminEvent: VAR_SHAYMIN_EVENT_STATE 0 -> 1
    }

    [Fact]
    public void ShayminStateIsNeverRewound()
    {
        using var session = Blank(GameVersion.Pt);
        var save = (SAV4)session.SaveFile;
        save.SetWork(87, 2);
        KeyItemEventService.Enable(session, "pt-oak");
        Assert.Equal(2, save.GetWork(87));
        KeyItemEventService.Disable(session, "pt-oak");
        Assert.Equal(2, save.GetWork(87));
        Assert.Equal(0, save.GetWork(68));
    }

    [Theory]
    [InlineData(GameVersion.HG)]
    [InlineData(GameVersion.B)]
    [InlineData(GameVersion.SL)]
    public void GamesWithoutVerifiedDataListNothing(GameVersion version)
    {
        using var session = Blank(version);
        Assert.False(KeyItemEventService.IsSupported(session));
        Assert.Empty(KeyItemEventService.GetEvents(session));
        Assert.False(KeyItemEventService.Enable(session, "e-eon").Success);
    }

    [Fact]
    public void EventsFromAnotherGameAreRefused()
    {
        using var session = Blank(GameVersion.FR);
        Assert.False(KeyItemEventService.Enable(session, "e-aurora").Success);
        Assert.Equal(2, KeyItemEventService.GetEvents(session).Count);
    }

    private static KeyItemEventStatus Status(SaveEngineSession session, string id) =>
        KeyItemEventService.GetEvents(session).Single(e => e.Id == id);
}
