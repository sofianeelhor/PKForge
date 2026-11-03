using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

public sealed class RanchBankRecordTests
{
    [Theory]
    [InlineData(RanchOwnershipType.Trainer)]
    [InlineData(RanchOwnershipType.Hayley)]
    [InlineData(RanchOwnershipType.Hayley_Traded)]
    public void BankEditorRetainsTheNativeRecordAndOwnershipTail(RanchOwnershipType owner)
    {
        // RK4's first 136 bytes are the PK4 view. The remaining 28 bytes carry
        // Ranch ownership/handler data (pinned PKHeX.Core RK4.ConvertToPK4).
        var native = new RK4 { Species = 25, PID = 12345, ID32 = 23456,
            Language = 2, Version = GameVersion.D, OwnershipType = owner,
            HandlingTrainerTID = 99, HandlingTrainerSID = 88, HandlingTrainerName = "HANDLER" };
        native.Data[0x89] = 0xAB; // include an uninterpreted byte in preservation checks
        native.RefreshChecksum();
        var original = new byte[native.SIZE_STORED];
        native.WriteDecryptedDataStored(original);
        var engine = new SaveEngine();
        using var session = engine.OpenEntitySession(original, format: "RK4");
        Assert.NotNull(session);
        var noop = session.ExportSlot(0, 0);
        Assert.Equal("RK4", noop.Format);
        Assert.Equal(original, noop.Data);

        session.ApplyEdit(0, 0, new(Nickname: "COPY"));
        var changed = session.ExportSlot(0, 0);
        Assert.Equal("RK4", changed.Format);
        Assert.Equal(164, changed.Data.Length);
        var reread = new RK4(changed.Data);
        Assert.Equal("COPY", reread.Nickname);
        Assert.True(reread.ChecksumValid);
        Assert.Equal(owner, reread.OwnershipType);
        Assert.Equal(99, reread.HandlingTrainerTID);
        Assert.Equal(88, reread.HandlingTrainerSID);
        Assert.Equal("HANDLER", reread.HandlingTrainerName);
        Assert.True(original.AsSpan(136).SequenceEqual(changed.Data.AsSpan(136)));

        using var reopened = engine.OpenEntitySession(changed.Data, format: changed.Format);
        Assert.NotNull(reopened);
        Assert.Equal(changed.Data, reopened.ExportSlot(0, 0).Data);
    }
}
