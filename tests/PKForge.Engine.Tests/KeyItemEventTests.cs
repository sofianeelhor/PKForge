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
    [InlineData(GameVersion.HG, "hgss-enigma", "Enigma Stone")]
    [InlineData(GameVersion.SS, "hgss-enigma", "Enigma Stone")]
    [InlineData(GameVersion.B, "bw-liberty", "Liberty Pass")]
    [InlineData(GameVersion.W, "bw-liberty", "Liberty Pass")]
    [InlineData(GameVersion.OR, "oras-eon", "Eon Ticket")]
    [InlineData(GameVersion.AS, "oras-eon", "Eon Ticket")]
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
    [InlineData(GameVersion.GD)]
    [InlineData(GameVersion.B2)]
    [InlineData(GameVersion.X)]
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

    [Fact]
    public void HeartGoldEnigmaStoneSetsTheMagicVar()
    {
        using var session = Blank(GameVersion.HG);
        var save = (SAV4)session.SaveFile;
        KeyItemEventService.Enable(session, "hgss-enigma");
        Assert.Equal(1778, save.GetWork(67)); // PKHeX const_hgss 0067 "Enigma Stone" 1778:Activated
        Assert.Contains(session.GetBag().SelectMany(p => p.Items), item => item.Id == 536 && item.Count == 1);

        save.SetEventFlag(781, true); // flags_hgss "Lati@s (Pewter City) Disappeared"
        Assert.Equal(KeyItemEventState.Completed, Status(session, "hgss-enigma").State);
        KeyItemEventService.Disable(session, "hgss-enigma");
        Assert.Equal(0, save.GetWork(67));
        Assert.True(save.GetEventFlag(781));
    }

    [Fact]
    public void LibertyPassStateIsKeyedToTheTrainerIdAndSurvivesSaving()
    {
        using var session = Blank(GameVersion.W);
        var save = (SAV5BW)session.SaveFile;
        KeyItemEventService.Enable(session, "bw-liberty");
        Assert.Equal(Misc5BW.LibertyTicketMagic ^ save.ID32, save.Misc.LibertyTicketState);

        var reloaded = (SAV5BW)SaveUtil.GetSaveFile(save.Write().ToArray())!;
        Assert.True(reloaded.Misc.IsLibertyTicketActivated);
        Assert.Contains(reloaded.Inventory.Pouches.SelectMany(p => p.Items), item => item.Index == 574 && item.Count == 1);

        save.EventWork.SetWork(145, 4); // const_bw "Victini" 4:Captured
        Assert.Equal(KeyItemEventState.Completed, Status(session, "bw-liberty").State);
        KeyItemEventService.Disable(session, "bw-liberty");
        Assert.Equal(0u, save.Misc.LibertyTicketState);
    }

    [Fact]
    public void LibertyPassItemAloneIsPartial()
    {
        using var session = Blank(GameVersion.B);
        session.SetItemCount(nameof(InventoryType.KeyItems), 574, 1);
        Assert.Equal(KeyItemEventState.Partial, Status(session, "bw-liberty").State);
    }

    [Fact]
    public void OrasEonTicketWritesTheReceivedFlagAndSurvivesSaving()
    {
        using var session = Blank(GameVersion.AS);
        var save = (SAV6AO)session.SaveFile;
        KeyItemEventService.Enable(session, "oras-eon");
        Assert.True(save.EventWork.GetEventFlag(3010)); // flags_oras "Received Eon Ticket"
        Assert.False(save.EventWork.GetEventFlag(3011));

        var reloaded = new SAV6AO(save.Write().ToArray()); // blank ORAS saves are not auto-detected, so reopen by type
        Assert.True(reloaded.EventWork.GetEventFlag(3010));
        Assert.Contains(reloaded.Inventory.Pouches.SelectMany(p => p.Items), item => item.Index == 726 && item.Count == 1);

        save.EventWork.SetEventFlag(3011, true); // "Eon Ticket Event Completed"
        Assert.Equal(KeyItemEventState.Completed, Status(session, "oras-eon").State);
    }

    [Theory]
    [InlineData(LanguageID.English, 0x3E3C, 0x3E44)]
    [InlineData(LanguageID.Japanese, 0xA000, 0xA083)]
    public void CrystalGsBallMirrorsPkhexVirtualConsoleButton(LanguageID language, int primary, int backup)
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(GameVersion.C, "PKForge", language), null);
        var save = (SAV2)session.SaveFile;
        Assert.Equal(KeyItemEventState.NotEnabled, Status(session, "c-gsball").State);

        Assert.True(KeyItemEventService.Enable(session, "c-gsball").Success);
        Assert.Equal(0x0B, save.Data[primary]); // SAV2 GS_BALL_AVAILABLE
        Assert.Equal(0x0B, save.Data[backup]);
        var status = Status(session, "c-gsball");
        Assert.Equal(KeyItemEventState.Enabled, status.State);
        Assert.False(status.NeedsItem);
        Assert.DoesNotContain(session.GetBag().SelectMany(p => p.Items), item => item.Count > 0);

        var reloaded = new SAV2(save.Write().ToArray(), language, GameVersion.C);
        Assert.True(reloaded.IsEnabledGSBallMobileEvent);

        save.SetEventFlag(832, true); // flags_c "Received GS Ball"
        Assert.Equal(KeyItemEventState.Used, Status(session, "c-gsball").State);
        KeyItemEventService.Disable(session, "c-gsball");
        Assert.False(save.IsEnabledGSBallMobileEvent);
        Assert.Equal(0, save.Data[backup]);
        Assert.True(save.GetEventFlag(832));
    }

    private static KeyItemEventStatus Status(SaveEngineSession session, string id) =>
        KeyItemEventService.GetEvents(session).Single(e => e.Id == id);
}
