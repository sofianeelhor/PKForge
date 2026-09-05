using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class DuplicateSlotTests
{
    [Theory]
    [InlineData(GameVersion.AS, false)]
    [InlineData(GameVersion.AS, true)]
    [InlineData(GameVersion.X, false)]
    [InlineData(GameVersion.X, true)]
    public void NativeGen6DuplicatesWithoutAmbiguousFormatDetection(GameVersion version, bool party)
    {
        var save = BlankSaveFile.Get(version, "Trainer", LanguageID.English);
        // Blank saves omit the recognition marker present in retail XY/ORAS.
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(save.Data[^0x1F0..], 0x42454546);
        var mon = new PK6
        {
            Species = 258, Version = version, CurrentLevel = 5, MetLevel = 5,
            MetLocation = 14, Ball = 4, Language = 2, OriginalTrainerName = "Trainer",
            TID16 = 12345, SID16 = 54321, PID = 0x12345678, EncryptionConstant = 0xABCDEF01,
            Nickname = "Mudkip", Move1 = 33, Move2 = 45, OriginalTrainerFriendship = 70,
        };
        mon.RefreshChecksum();
        if (party) save.SetPartySlotAtIndex(mon, 0, EntityImportSettings.None);
        else save.SetBoxSlotAtIndex(mon, 0, 0, EntityImportSettings.None);
        var box = party ? -1 : 0;
        using var session = new SaveEngineSession(save, "Sinking Sapphire");
        var before = session.ExportSlot(box, 0).Data;
        // Reproduce the previous bug: native PK6 looks like an ambiguous PK6/PK7,
        // and the no-context loose-file parser chooses PK7 (no transfer route back).
        Assert.IsType<PK7>(EntityFormat.GetFromBytes(before.ToArray()));
        Assert.Null(EntityConverter.ConvertToType(EntityFormat.GetFromBytes(before.ToArray())!, typeof(PK6), out _));

        Assert.True(session.DuplicateSlot(box, 0, box, 1));
        Assert.Equal(before, session.ExportSlot(box, 0).Data);
        Assert.Equal(before, session.ExportSlot(box, 1).Data);
        var written = session.Serialize();
        using var reopened = new SaveEngine().OpenSession(written);
        Assert.Equal(before, reopened.ExportSlot(box, 0).Data);
        Assert.Equal(before, reopened.ExportSlot(box, 1).Data);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void DuplicateDoesNotOverwriteAndDoesNotAliasSource(int generation)
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(generation);
        var mon = session.SaveFile.BlankPKM;
        mon.Species = 25;
        mon.CurrentLevel = 10;
        mon.RefreshChecksum();
        session.SaveFile.SetBoxSlotAtIndex(mon, 0, 0, EntityImportSettings.None);
        var before = session.ExportSlot(0, 0).Data;
        Assert.False(session.DuplicateSlot(0, 2, 0, 1));
        Assert.False(session.DuplicateSlot(0, 0, 0, 0));
        Assert.True(session.DuplicateSlot(0, 0, 0, 1));
        Assert.Equal(before, session.ExportSlot(0, 1).Data);
        Assert.False(session.DuplicateSlot(0, 0, 0, 1));
        session.ApplyEdit(0, 1, new EntityEdit(Nickname: "COPY"));
        Assert.Equal(before, session.ExportSlot(0, 0).Data);
    }

    [Fact]
    public void FullPartyRejectsDuplicateWithoutChangingSave()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(6);
        var mon = new PK6 { Species = 25, CurrentLevel = 10 };
        mon.RefreshChecksum();
        session.SaveFile.SetPartySlotAtIndex(mon, 0, EntityImportSettings.None);
        for (var i = 1; i < 6; i++) Assert.True(session.DuplicateSlot(-1, 0, -1, i));
        var before = session.Serialize().ToArray();
        Assert.False(session.DuplicateSlot(-1, 0, -1, 0));
        Assert.Equal(before, session.Serialize().ToArray());
    }
}
