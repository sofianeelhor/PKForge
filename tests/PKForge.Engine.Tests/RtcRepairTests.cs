using PKHeX.Core;
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Real-time clock repair is a battery-era problem: Gen 2's cartridge clock drifts
/// and Gen 3's save battery dies. Both generations carry an in-save "ask the player
/// for the time again" switch, and the repair must arm exactly that switch - the
/// games then run their own clock-reset flow at next boot.
/// </summary>
public sealed class RtcRepairTests
{
    [Fact]
    public void Gen2RepairFlipsOnlyTheClockResetBit()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(2);
        Assert.True(session.SupportsRTCRepair);
        var save = Assert.IsType<SAV2>(session.SaveFile);
        var before = save.Data.ToArray();

        session.RepairRTC();

        // International GS/C RTC flags byte (PKHeX SAV2Offsets); bit 7 asks the
        // game to re-ask for the clock at next boot. Nothing else may change.
        var changed = Enumerable.Range(0, before.Length).Where(i => before[i] != save.Data[i]).ToList();
        var offset = Assert.Single(changed);
        Assert.Equal(0x0C60, offset);
        Assert.Equal(0x80, save.Data[0x0C60] & 0x80);
    }

    [Fact]
    public void Gen2RepairSurvivesSerializationWithValidChecksums()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(2);

        session.RepairRTC();
        var written = session.Serialize();

        // The RTC flag byte sits inside Gen 2's checksummed region, so a valid
        // written save proves the write path recomputed them around the repair.
        // (Blank Gen 2 saves are not recognizer-visible, so reparse typed.)
        var reparsed = new SAV2(written.ToArray(), LanguageID.English, GameVersion.C);
        Assert.True(reparsed.ChecksumsValid);
        Assert.Equal(0x80, reparsed.Data[0x0C60] & 0x80);
    }

    [Fact]
    public void Gen3EmeraldRepairArmsThePasswordProtectedReset()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(3);
        var save = Assert.IsType<SAV3E>(session.SaveFile);
        Assert.True(session.SupportsRTCRepair);
        Assert.Equal(0, save.GetWork(ResetRTCWork));

        session.RepairRTC();

        Assert.Equal(ResetRTCEnabled, save.GetWork(ResetRTCWork));
    }

    [Fact]
    public void Gen3RubyRepairArmsThePasswordProtectedReset()
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(GameVersion.R, "PKForge", LanguageID.English), null);
        var save = Assert.IsType<SAV3RS>(session.SaveFile);
        Assert.True(session.SupportsRTCRepair);

        session.RepairRTC();

        Assert.Equal(ResetRTCEnabled, save.GetWork(ResetRTCWork));
    }

    [Fact]
    public void FireRedHasNoRealTimeClockToRepair()
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(GameVersion.FR, "PKForge", LanguageID.English), null);
        Assert.False(session.SupportsRTCRepair);
        Assert.Throws<NotSupportedException>(session.RepairRTC);
    }

    [Fact]
    public void ModernFormatsHaveNoRealTimeClockToRepair()
    {
        using var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(7);
        Assert.False(session.SupportsRTCRepair);
        Assert.Throws<NotSupportedException>(session.RepairRTC);
    }

    // pret VAR_ENABLE_RESET_RTC (0x402C) and its enabled magic; PKHeX indexes the
    // Gen 3 work array raw, so the slot is the variable's low byte.
    private const int ResetRTCWork = 0x2C;
    private const ushort ResetRTCEnabled = 2336;
}
