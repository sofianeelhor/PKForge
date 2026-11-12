using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Mystery Gift slot writing on blank retail saves, per generation: a database card is written,
/// the save is serialized and reloaded, and the card reads back from its slot; deleting packs the
/// slots; received flags round-trip; cards for another generation are refused.
/// </summary>
public sealed class MysteryGiftSlotTests
{
    public static TheoryData<GameVersion> Games => new()
    {
        GameVersion.D, GameVersion.Pt, GameVersion.HG,
        GameVersion.B, GameVersion.B2,
        GameVersion.X, GameVersion.OR,
        GameVersion.SN, GameVersion.US,
    };

    /// <summary>Gen 4 opens a full-size zero-filled 512 KB image (PKHeX's in-memory Gen 4 blank
    /// has no file image to write back, see HoneyTreeTests); later games use PKHeX's blank.</summary>
    private static SaveEngineSession Blank(GameVersion version)
    {
        var session = new SaveEngineSession(BlankOf(version), null);
        if (session.SaveFile is SAV5 b2w2)
        {
            // A real B/W album is zeros encrypted under its seed; PKHeX's blank leaves raw zeros
            // (seed 0), which decrypt to noise. Start from the album a new game has.
            b2w2.Mystery.ClearReceivedFlags();
            for (var i = 0; i < b2w2.Mystery.GiftCountMax; i++)
                b2w2.Mystery.SetMysteryGift(i, new PGF());
            b2w2.Mystery.EndAccess();
        }
        return session;
    }

    private static SaveFile BlankOf(GameVersion version) => version switch
    {
        GameVersion.D or GameVersion.P => new SAV4DP(new byte[0x80000]) { Version = version },
        GameVersion.Pt => new SAV4Pt(new byte[0x80000]),
        GameVersion.HG or GameVersion.SS => new SAV4HGSS(new byte[0x80000]),
        _ => BlankSaveFile.Get(version, "PKForge", LanguageID.English),
    };

    private static SaveEngineSession Reload(SaveEngineSession session)
    {
        // What the app writes, reopened as the same game (blank Gen 4 saves carry no block
        // footers for format detection, so the type is named rather than sniffed).
        var bytes = session.Serialize().ToArray();
        var type = session.SaveFile.GetType();
        var reloaded = (SaveFile)(type.GetConstructor([typeof(Memory<byte>)]) is { } memory
            ? memory.Invoke([new Memory<byte>(bytes)])
            : Activator.CreateInstance(type, bytes)!);
        if (reloaded is SAV4DP dp) dp.Version = session.SaveFile.Version;
        return new SaveEngineSession(reloaded, null);
    }

    private static IEnumerable<DataMysteryGift> Database(byte generation) => generation switch
    {
        4 => EncounterEvent.MGDB_G4,
        5 => EncounterEvent.MGDB_G5,
        6 => EncounterEvent.MGDB_G6,
        _ => EncounterEvent.MGDB_G7,
    };

    /// <summary>The first two database Pokémon cards this save accepts, with distinct card numbers.</summary>
    private static (byte[] Data, string Name)[] Cards(SaveEngineSession session, int count = 2)
    {
        var picked = new List<(byte[], string)>();
        var ids = new HashSet<int>();
        foreach (var gift in Database(session.SaveFile.Generation))
        {
            if (!gift.IsEntity || gift.CardID <= 0 || !ids.Add(gift.CardID)) continue;
            var file = (gift.Write().ToArray(), $"card.{gift.Extension}");
            if (MysteryGiftSlotService.Check(session, file.Item1, file.Item2) is not { Refusal: null }) continue;
            picked.Add(file);
            if (picked.Count == count) break;
        }
        Assert.Equal(count, picked.Count);
        return [.. picked];
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void WrittenCardSurvivesSaveAndReload(GameVersion version)
    {
        using var session = Blank(version);
        Assert.True(MysteryGiftSlotService.IsSupported(session));
        var (data, name) = Cards(session, 1)[0];
        var card = MysteryGiftSlotService.Parse(data, name)!;

        var check = MysteryGiftSlotService.Check(session, data, name);
        Assert.Equal(0, check.FreeSlot);
        Assert.True(MysteryGiftSlotService.Write(session, data, name).Success);

        using var reloaded = Reload(session);
        var album = MysteryGiftSlotService.Load(reloaded)!;
        var slot = album.Slots[0];
        Assert.False(slot.IsEmpty);
        Assert.False(slot.Used);
        Assert.Equal(card.Species, slot.Species);
        Assert.Contains(card.CardID, album.ReceivedFlags);
        if (card is PCD)
        {
            // Gen 4: the PGT gift waits for the delivery man; the card itself fills the album.
            Assert.Equal("PGT", slot.Format);
            var albumCard = album.Slots.Single(s => s.Format == "PCD" && !s.IsEmpty);
            Assert.Equal(card.CardID, albumCard.CardId);
            Assert.True(((SAV4)reloaded.SaveFile).Mystery.IsDeliveryManActive);
        }
        else
        {
            Assert.Equal(card.CardID, slot.CardId);
            Assert.Equal(card.Type, slot.Format);
        }
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void DeletePacksSlotsAndSurvivesReload(GameVersion version)
    {
        using var session = Blank(version);
        var cards = Cards(session);
        foreach (var (data, name) in cards)
            Assert.True(MysteryGiftSlotService.Write(session, data, name).Success);
        var second = MysteryGiftSlotService.Load(session)!.Slots[1];

        Assert.True(MysteryGiftSlotService.Delete(session, 0).Success);
        Assert.False(MysteryGiftSlotService.Delete(session, 5).Success);

        using var reloaded = Reload(session);
        var slots = MysteryGiftSlotService.Load(reloaded)!.Slots;
        Assert.Equal(second.Species, slots[0].Species); // moved up
        Assert.True(slots[1].IsEmpty);
    }

    [Theory]
    [MemberData(nameof(Games))]
    public void ReceivedFlagsRoundTrip(GameVersion version)
    {
        using var session = Blank(version);
        Assert.True(MysteryGiftSlotService.SetReceivedFlag(session, 42, true).Success);
        Assert.True(MysteryGiftSlotService.SetReceivedFlag(session, 7, true).Success);
        Assert.False(MysteryGiftSlotService.SetReceivedFlag(session, 0, true).Success);

        using var reloaded = Reload(session);
        Assert.Equal([7, 42], MysteryGiftSlotService.Load(reloaded)!.ReceivedFlags);
        Assert.True(MysteryGiftSlotService.SetReceivedFlag(reloaded, 42, false).Success);

        using var again = Reload(reloaded);
        Assert.Equal([7], MysteryGiftSlotService.Load(again)!.ReceivedFlags);
    }

    [Fact]
    public void FullSlotsOfferReplacement()
    {
        using var session = Blank(GameVersion.X);
        var (data, name) = Cards(session, 1)[0];
        var count = MysteryGiftSlotService.Load(session)!.Slots.Count;
        for (var i = 0; i < count; i++)
            Assert.True(MysteryGiftSlotService.Write(session, data, name).Success);

        var check = MysteryGiftSlotService.Check(session, data, name);
        Assert.Null(check.Refusal);
        Assert.Null(check.FreeSlot);
        Assert.Equal(count, check.Replaceable.Count);
        Assert.False(MysteryGiftSlotService.Write(session, data, name).Success);
        Assert.True(MysteryGiftSlotService.Write(session, data, name, replace: 3).Success);
    }

    [Fact]
    public void OtherGenerationCardsAreRefused()
    {
        using var gen7 = Blank(GameVersion.SN);
        var (data, name) = Cards(gen7, 1)[0];
        using var gen6 = Blank(GameVersion.X);

        var check = MysteryGiftSlotService.Check(gen6, data, name);
        Assert.NotNull(check.Refusal);
        Assert.Contains("Generation 7", check.Refusal);
        Assert.False(MysteryGiftSlotService.Write(gen6, data, name).Success);
        Assert.All(MysteryGiftSlotService.Load(gen6)!.Slots, s => Assert.True(s.IsEmpty));
        Assert.False(MysteryGiftSlotService.Check(gen6, [1, 2, 3], "junk.wc6").Refusal is null);
    }

    [Fact]
    public void GamesWithoutSlotsAreUnsupported()
    {
        using var session = Blank(GameVersion.SW);
        Assert.False(MysteryGiftSlotService.IsSupported(session));
        Assert.Null(MysteryGiftSlotService.Load(session));
    }
}
