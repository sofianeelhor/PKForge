using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One Gen 3 clock value: days plus a time of day (PKHeX <c>RTC3</c>).</summary>
public readonly record struct Gen3Clock(int Day, int Hour, int Minute, int Second)
{
    public override string ToString() => $"day {Day}, {Hour:00}:{Minute:00}:{Second:00}";
}

/// <summary>What the Ruby/Sapphire/Emerald save knows about time.</summary>
/// <param name="Initial">The offset the game adds to the cartridge clock (pokeemerald SaveBlock2.localTimeOffset).</param>
/// <param name="Elapsed">The in-game time berries and daily events were last updated (pokeemerald SaveBlock2.lastBerryTreeUpdate).</param>
/// <param name="ResetArmed">True while the game will offer its own password-protected clock reset at the title screen.</param>
public sealed record Gen3ClockState(Gen3Clock Initial, Gen3Clock Elapsed, bool ResetArmed)
{
    /// <summary>The elapsed counter has already been moved past PKHeX's berry-fix threshold.</summary>
    public bool BerryFixApplied => Elapsed.Day >= ClockRepairService.BerryFixDay;

    /// <summary>Both counters are zero - what a fresh clock (or a PKHeX "Reset") looks like.</summary>
    public bool IsZero => Initial == default && Elapsed == default;
}

/// <summary>
/// Ruby/Sapphire/Emerald clock repair, mirroring PKHeX's WinForms <c>SAV_RTC3</c>:
/// the two <c>RTC3</c> values live in the small block at 0x98 (<c>ClockInitial</c>) and 0xA0
/// (<c>ClockElapsed</c>) (PKHeX SaveBlock3SmallRS/SaveBlock3SmallE; pokeemerald include/global.h
/// SaveBlock2 localTimeOffset and lastBerryTreeUpdate at the same offsets).
///
/// The game computes "now" as cartridge clock minus the initial offset, and grows berries and
/// runs daily events by the gap between "now" and the elapsed value. A dead battery or a
/// moved save leaves "now" behind the elapsed value, and time appears frozen.
///
/// Only save data is touched. The emulator's own clock (mGBA's 16-byte RTC footer, carried
/// verbatim by <see cref="SramPadding"/>) is outside the save and is never changed here.
/// </summary>
public static class ClockRepairService
{
    /// <summary>SAV_RTC3.B_BerryFix_Click: <c>Math.Max((2 * 366) + 2, NUD_EDay.Value)</c>.</summary>
    public const int BerryFixDay = (2 * 366) + 2;

    /// <summary>SAV_RTC3's day inputs are ushort-ranged (Designer NUD_IDay/NUD_EDay Maximum 65535).</summary>
    public const int MaxDay = ushort.MaxValue;

    // Same slot and magic the session's RepairRTC writes (pret VAR_ENABLE_RESET_RTC 0x402C, RESET_RTC_ENABLED).
    private const int ResetRtcWork = 0x2C;
    private const ushort ResetRtcEnabled = 2336;

    /// <summary>Ruby, Sapphire and Emerald: FireRed/LeafGreen have no cartridge clock (PKHeX only
    /// opens SAV_RTC3 for <c>ISaveBlock3SmallHoenn</c>).</summary>
    public static bool IsSupported(ISaveEngineSession session) => TryGetSave(session) is not null;

    public static Gen3ClockState GetState(ISaveEngineSession session)
    {
        var (save, small) = Require(session);
        return new Gen3ClockState(Read(small.ClockInitial), Read(small.ClockElapsed), save.GetWork(ResetRtcWork) == ResetRtcEnabled);
    }

    /// <summary>SAV_RTC3.B_Reset_Click: both clocks back to zero, so the game re-reads the cartridge
    /// clock from a clean start. Berries and daily events resume from the next in-game minute.</summary>
    public static Gen3ClockState ResetClocks(ISaveEngineSession session)
    {
        var (_, small) = Require(session);
        small.ClockInitial = Write(small.ClockInitial, default);
        small.ClockElapsed = Write(small.ClockElapsed, default);
        return GetState(session);
    }

    /// <summary>SAV_RTC3.B_BerryFix_Click: moves the elapsed day past the Ruby/Sapphire berry
    /// bug threshold (never backwards).</summary>
    public static Gen3ClockState ApplyBerryFix(ISaveEngineSession session)
    {
        var (_, small) = Require(session);
        var elapsed = Read(small.ClockElapsed);
        small.ClockElapsed = Write(small.ClockElapsed, elapsed with { Day = Math.Max(BerryFixDay, elapsed.Day) });
        return GetState(session);
    }

    /// <summary>Arms the game's own reset-the-clock dialog (the same write as
    /// <see cref="SaveEngineSession.RepairRTC"/>): at the title screen the game then lets
    /// the player set the time again.</summary>
    public static Gen3ClockState ArmInGameReset(ISaveEngineSession session)
    {
        Require(session);
        ((SaveEngineSession)session).RepairRTC();
        return GetState(session);
    }

    /// <summary>Writes both values, clamped to SAV_RTC3's input ranges (day 0-65535, 23:59:59).</summary>
    public static Gen3ClockState SetClocks(ISaveEngineSession session, Gen3Clock initial, Gen3Clock elapsed)
    {
        var (_, small) = Require(session);
        small.ClockInitial = Write(small.ClockInitial, Clamp(initial));
        small.ClockElapsed = Write(small.ClockElapsed, Clamp(elapsed));
        return GetState(session);
    }

    private static Gen3Clock Clamp(Gen3Clock clock) => new(
        Math.Clamp(clock.Day, 0, MaxDay), Math.Clamp(clock.Hour, 0, 23),
        Math.Clamp(clock.Minute, 0, 59), Math.Clamp(clock.Second, 0, 59));

    private static Gen3Clock Read(RTC3 rtc) => new(rtc.Day, rtc.Hour, rtc.Minute, rtc.Second);

    private static RTC3 Write(RTC3 rtc, Gen3Clock clock)
    {
        rtc.Day = clock.Day;
        rtc.Hour = clock.Hour;
        rtc.Minute = clock.Minute;
        rtc.Second = clock.Second;
        return rtc;
    }

    private static SAV3? TryGetSave(ISaveEngineSession session) =>
        session is SaveEngineSession { SaveFile: SAV3 { SmallBlock: ISaveBlock3SmallHoenn } save } ? save : null;

    private static (SAV3 Save, ISaveBlock3SmallHoenn Small) Require(ISaveEngineSession session)
    {
        var save = TryGetSave(session)
            ?? throw new NotSupportedException("The clock repair exists only for Ruby, Sapphire and Emerald saves.");
        return (save, (ISaveBlock3SmallHoenn)save.SmallBlock);
    }
}
