using System.Buffers.Binary;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// "World &amp; events" editors: every write is proven through a full serialize and reopen,
/// and every legality claim (roamer rerolls, Dream World fill) is checked with PKHeX's own
/// verifiers rather than restated.
/// </summary>
public sealed class WorldEventsTests
{
    // ── Gen 3 clock ──

    [Theory]
    [InlineData(GameVersion.E)]
    [InlineData(GameVersion.R)]
    public void ClockResetAndBerryFixSurviveReopen(GameVersion version)
    {
        using var session = OpenGen3(version);
        Assert.True(ClockRepairService.IsSupported(session));
        ClockRepairService.SetClocks(session, new Gen3Clock(400, 23, 99, 5), new Gen3Clock(12, 1, 2, 3));
        var clamped = ClockRepairService.GetState(session);
        Assert.Equal(new Gen3Clock(400, 23, 59, 5), clamped.Initial);

        ClockRepairService.ApplyBerryFix(session);
        using (var reopened = Reopen(session))
        {
            var state = ClockRepairService.GetState(reopened);
            Assert.Equal(734, state.Elapsed.Day); // SAV_RTC3: (2 * 366) + 2
            Assert.Equal(new Gen3Clock(734, 1, 2, 3), state.Elapsed);
            Assert.True(state.BerryFixApplied);
        }

        ClockRepairService.ResetClocks(session);
        ClockRepairService.ArmInGameReset(session);
        using var again = Reopen(session);
        var reset = ClockRepairService.GetState(again);
        Assert.True(reset.IsZero);
        Assert.True(reset.ResetArmed);
        // Offsets are the ones pokeemerald SaveBlock2 and PKHeX SaveBlock3Small* agree on.
        var small = ((SAV3)((SaveEngineSession)again).SaveFile).SmallBlock.Data;
        Assert.All(small.Slice(0x98, 16).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void BerryFixNeverMovesTheClockBackwards()
    {
        using var session = OpenGen3(GameVersion.E);
        ClockRepairService.SetClocks(session, default, new Gen3Clock(5000, 0, 0, 0));
        Assert.Equal(5000, ClockRepairService.ApplyBerryFix(session).Elapsed.Day);
    }

    [Fact]
    public void FireRedHasNoClock()
    {
        using var session = OpenGen3(GameVersion.FR);
        Assert.False(ClockRepairService.IsSupported(session));
        Assert.Throws<NotSupportedException>(() => ClockRepairService.GetState(session));
    }

    // ── Box names and wallpapers ──

    [Theory]
    [InlineData(GameVersion.E, 16, "box_wp05e")]
    [InlineData(GameVersion.FR, 16, "box_wp14frlg")]
    public void Gen3BoxLayoutSurvivesReopen(GameVersion version, int wallpapers, string expectedAsset)
    {
        using var session = OpenGen3(version);
        Assert.True(BoxLayoutService.SupportsNames(session));
        Assert.True(BoxLayoutService.SupportsWallpapers(session));
        Assert.Equal(wallpapers, BoxLayoutService.GetWallpapers(session).Count);

        BoxLayoutService.Rename(session, 2, "  LEGENDS OF HOENN  ");
        var index = expectedAsset.Contains("frlg") ? 13 : 4;
        BoxLayoutService.SetWallpaper(session, 2, index);

        using var reopened = Reopen(session);
        var box = BoxLayoutService.GetBox(reopened, 2);
        Assert.Equal("LEGENDS ", box.Name); // cut to SAV_BoxLayout's 8 characters, then kept verbatim
        Assert.Equal(index, box.Wallpaper);
        Assert.Equal(expectedAsset, box.WallpaperAsset);
    }

    [Fact]
    public void Gen5BoxLayoutSurvivesReopenAndMatchesWallpaperUtil()
    {
        using var session = OpenBlank(GameVersion.B2);
        BoxLayoutService.Rename(session, 0, "Dream Team");
        BoxLayoutService.SetWallpaper(session, 0, 20);
        using var reopened = Reopen(session);
        var box = BoxLayoutService.GetBox(reopened, 0);
        Assert.Equal("Dream Te", box.Name);
        Assert.Equal(20, box.Wallpaper);
        Assert.Equal("box_wp21b2w2", box.WallpaperAsset);
        Assert.Equal("box_wp03bw", BoxLayoutService.GetWallpaperResourceName(GameVersion.W2, 2));
        Assert.Throws<ArgumentException>(() => BoxLayoutService.Rename(session, 0, "   "));
        Assert.Throws<ArgumentOutOfRangeException>(() => BoxLayoutService.SetWallpaper(session, 0, 24));
    }

    [Fact]
    public void Gen6NamesAllowFourteenCharacters()
    {
        using var session = OpenBlank(GameVersion.X);
        Assert.Equal(14, BoxLayoutService.GetNameMaxLength(session));
        BoxLayoutService.Rename(session, 30, "Kalos Champions!");
        using var reopened = Reopen(session);
        Assert.Equal("Kalos Champion", BoxLayoutService.GetBox(reopened, 30).Name);
    }

    [Fact]
    public void Gen1HasNoBoxLayout()
    {
        using var session = OpenBlank(GameVersion.RD);
        Assert.False(BoxLayoutService.IsSupported(session));
    }

    // ── HGSS Apricorns and Pokéwalker ──

    [Fact]
    public void ApricornsSurviveReopenInPkhexOrder()
    {
        using var session = OpenHgss();
        var apricorns = ApricornService.GetApricorns(session);
        Assert.Equal(["Red Apricorn", "Yellow Apricorn", "Blue Apricorn", "Green Apricorn", "Pink Apricorn", "White Apricorn", "Black Apricorn"],
            apricorns.Select(a => a.Name));
        ApricornService.SetAll(session, 250);
        ApricornService.SetCount(session, 2, 17);
        using var reopened = Reopen(session);
        var after = ApricornService.GetApricorns(reopened);
        Assert.Equal([99, 99, 17, 99, 99, 99, 99], after.Select(a => a.Count));
        Assert.Equal(17, ((SAV4HGSS)((SaveEngineSession)reopened).SaveFile).GetApricornCount(2));
    }

    [Fact]
    public void PokewalkerCountersAndCoursesSurviveReopen()
    {
        using var session = OpenHgss();
        var state = PokewalkerService.GetState(session);
        Assert.Equal(27, state.Courses.Count);
        Assert.All(state.Courses, c => Assert.Equal(6, c.Encounters.Count));
        Assert.Equal("Refreshing Field", state.Courses[0].Name);

        PokewalkerService.SetCounters(session, 123_456, 99_999_999);
        PokewalkerService.UnlockAll(session);
        PokewalkerService.SetCourse(session, 3, false);

        using var reopened = Reopen(session);
        var after = PokewalkerService.GetState(reopened);
        Assert.Equal((123_456u, PokewalkerService.MaxCounter), (after.Steps, after.Watts));
        // English saves: every course but Rally (23), Sightseeing (24), Amity Meadow (26) - PKHeX's INT mask 0x027FFFFF.
        var expected = Enumerable.Range(0, 27).Where(i => i is not (3 or 23 or 24 or 26));
        Assert.Equal(expected, after.Courses.Where(c => c.Unlocked).Select(c => c.Index));
        Assert.False(after.Courses[23].AvailableForLanguage);
    }

    // ── Roamers ──

    [Fact]
    public void Method1MatchesPkhexMethodFinder()
    {
        var (pid, iv32) = RoamerService.Method1(0x1234_5678);
        var pk = new PK3 { PID = pid, IV32 = iv32 };
        var result = MethodFinder.Analyze(pk);
        Assert.Equal(PIDType.Method_1, result.Type);
        Assert.Equal(0x1234_5678u, result.OriginSeed);
    }

    [Theory]
    [InlineData(GameVersion.E, false)]
    [InlineData(GameVersion.R, true)]
    [InlineData(GameVersion.FR, true)]
    public void Gen3RoamerRerollIsMethod1AndShinyOnRequest(GameVersion version, bool glitched)
    {
        using var session = OpenGen3(version, tid: 12345, sid: 54321);
        var save = (SAV3)((SaveEngineSession)session).SaveFile;
        var roamer = new Roamer3(save.LargeBlock);
        Assert.False(RoamerService.GetRoamers(session)[0].CanReroll); // not released yet
        roamer.Species = (ushort)(version == GameVersion.FR ? Species.Raikou : Species.Latios);
        roamer.CurrentLevel = 40;
        roamer.IsActive = true;

        var info = RoamerService.Reroll(session, 0, shiny: true);
        Assert.True(info.IsShiny);
        using var reopened = Reopen(session);
        var stored = new Roamer3(((SAV3)((SaveEngineSession)reopened).SaveFile).LargeBlock);
        Assert.Equal(info.Pid, stored.PID);
        Assert.True(Roamer3.IsShiny(stored.PID, save));

        // The caught Pokémon: full IVs on Emerald, one byte on R/S/FR/LG - PKHeX must see Method 1 / Method 1 Roamer.
        var pk = new PK3 { PID = stored.PID, IV32 = glitched ? stored.IV32 & 0xFF : stored.IV32 };
        var type = MethodFinder.Analyze(pk).Type;
        Assert.Equal(glitched ? PIDType.Method_1_Roamer : PIDType.Method_1, type);
        // Base HP 80 Latios / 90 Raikou at their levels, zero EVs.
        var baseHp = save.Personal[roamer.Species].HP;
        Assert.Equal((2 * baseHp + stored.IV_HP) * stored.CurrentLevel / 100 + stored.CurrentLevel + 10, stored.HP_Current);
    }

    [Fact]
    public void HgssRoamerRerollIsMethod1()
    {
        using var session = OpenHgss();
        var save = (SAV4HGSS)((SaveEngineSession)session).SaveFile;
        save.RoamerRaikou.Species = (ushort)Species.Raikou;
        save.RoamerRaikou.Level = 40;
        save.RoamerRaikou.IsActive = true;
        var info = RoamerService.Reroll(session, 0, shiny: false);
        using var reopened = Reopen(session);
        var raikou = ((SAV4HGSS)((SaveEngineSession)reopened).SaveFile).RoamerRaikou;
        Assert.Equal(info.Pid, raikou.PID);
        var pk = new PK4 { PID = raikou.PID, IV32 = raikou.IV32 };
        Assert.Equal(PIDType.Method_1, MethodFinder.Analyze(pk).Type);
        Assert.Equal(4, RoamerService.GetRoamers(reopened).Count);
    }

    [Fact]
    public void BlackWhiteRoamerRerollWritesIndependentValues()
    {
        using var session = OpenBlank(GameVersion.B);
        var save = (SAV5BW)((SaveEngineSession)session).SaveFile;
        save.Encount.Roamer1.Species = (ushort)Species.Thundurus;
        save.Encount.Roamer1.Level = 40;
        var info = RoamerService.Reroll(session, 0, shiny: true);
        Assert.True(info.IsShiny);
        using var reopened = Reopen(session);
        var roamer = ((SAV5BW)((SaveEngineSession)reopened).SaveFile).Encount.Roamer1;
        Assert.Equal(info.Pid, roamer.PID);
        Assert.True(ShinyUtil.GetIsShiny3(save.ID32, roamer.PID));
        Assert.Equal("Thundurus", RoamerService.GetRoamers(reopened)[0].Label);
    }

    [Fact]
    public void XyRoamerStateSurvivesReopenAndRefusesReroll()
    {
        using var session = OpenBlank(GameVersion.X);
        var info = RoamerService.GetRoamers(session)[0];
        Assert.False(info.CanReroll);
        Assert.Throws<InvalidOperationException>(() => RoamerService.Reroll(session, 0, false));
        RoamerService.SetGen6State(session, (int)Roamer6State.Roaming, 3);
        using var reopened = Reopen(session);
        var after = RoamerService.GetRoamers(reopened)[0];
        Assert.Equal("Roaming", after.State);
        Assert.Equal(3u, after.TimesEncountered);
    }

    // ── Event resets ──

    [Fact]
    public void Gen1LegendaryResetSurvivesReopen()
    {
        using var session = OpenBlank(GameVersion.RD);
        var sav1 = (SAV1)((SaveEngineSession)session).SaveFile;
        // Mewtwo caught: its script flag and hide flag set, as the game leaves them.
        var spawner = new G1OverworldSpawner(sav1);
        spawner.GetFlagPairs().Single(p => p.Name == "FlagMewtwo").SetState(true);
        spawner.Save();
        Assert.True(EventResetService.GetEvents(session).Single(e => e.Title == "Mewtwo").Done);

        var outcome = EventResetService.Reset(session, "FlagMewtwo");
        Assert.True(outcome.Success, outcome.Message);
        using var reopened = Reopen(session);
        var events = EventResetService.GetEvents(reopened);
        Assert.False(events.Single(e => e.Title == "Mewtwo").Done);
        Assert.Contains(events, e => e.Title == "Voltorb 1");
        Assert.Equal("Cerulean Cave", events.Single(e => e.Title == "Mewtwo").Where);
    }

    [Fact]
    public void CrystalGsBallResetClearsKurtAndShrineState()
    {
        using var session = new SaveEngineSession(new SAV2(Crystal(), LanguageID.English, GameVersion.C), null);
        var sav2 = (SAV2)session.SaveFile;
        foreach (var flag in new[] { 190, 191, 192, 832 }) sav2.SetEventFlag(flag, true);
        sav2.SetWork(30, 2);
        Assert.True(EventResetService.GetEvents(session).Single().Done);

        Assert.True(EventResetService.Reset(session, EventResetService.GsBallId).Success);
        var reparsed = new SAV2(session.Serialize().ToArray(), LanguageID.English, GameVersion.C);
        Assert.All(new[] { 190, 191, 192, 832 }, flag => Assert.False(reparsed.GetEventFlag(flag)));
        Assert.Equal(0, reparsed.GetWork(30));
        Assert.True(reparsed.IsEnabledGSBallMobileEvent);
    }

    // ── Gen 5 Entralink ──

    [Theory]
    [InlineData(GameVersion.W)]
    [InlineData(GameVersion.B2)]
    public void EntreeForestFillIsDreamWorldLegalAndSurvivesReopen(GameVersion version)
    {
        using var session = OpenBlank(version);
        EntralinkService.SetLevels(session, 1234, 42);
        var filled = EntralinkService.FillForestLegally(session);
        Assert.Equal(8, filled.UnlockedAreas);
        Assert.True(filled.NinthAreaUnlocked);

        using var reopened = Reopen(session);
        var state = EntralinkService.GetState(reopened);
        Assert.Equal((999, 42), (state.WhiteForestLevel, state.BlackCityLevel));
        Assert.Equal(filled.VisitorCount, state.VisitorCount);
        Assert.True(state.VisitorCount > 0);

        // Every slot must be one of PKHeX's Dream World templates (species, form, move).
        var save = (SAV5)((SaveEngineSession)reopened).SaveFile;
        var templates = (save is SAV5BW ? Encounters5BW.DreamWorld_BW : Encounters5B2W2.DreamWorld_B2W2)
            .Concat(Encounters5DR.DreamWorld_Common).ToArray();
        var forest = save.EntreeForest;
        forest.StartAccess();
        foreach (var slot in forest.Slots.Where(s => s.Species != 0))
        {
            Assert.Contains(templates, t => t.Species == slot.Species && t.Form == slot.Form
                && (slot.Move == 0 || t.Moves.Contains(slot.Move)));
        }
        forest.EndAccess();

        EntralinkService.ClearForest(reopened);
        Assert.Equal(0, EntralinkService.GetState(reopened).VisitorCount);
    }

    [Fact]
    public void B2W2PassPowersAndMissionsSurviveReopen()
    {
        using var session = OpenBlank(GameVersion.W2);
        EntralinkService.SetPassPower(session, 1, (int)PassPower5.CaptureMAX);
        EntralinkService.UnlockAllMissions(session);
        Assert.Throws<ArgumentOutOfRangeException>(() => EntralinkService.SetPassPower(session, 0, 46));

        using var reopened = Reopen(session);
        var state = EntralinkService.GetState(reopened);
        Assert.Equal("Capture Power MAX", state.PassPowers[1]);
        Assert.Contains(state.Missions, m => m.Unlocked && m.Index == 5);
        Assert.Equal("Encounter Power ↑↑", EntralinkService.PassPowerName((byte)PassPower5.EncounterPlus2));
    }

    [Fact]
    public void BlackWhiteHasNoPassPowers()
    {
        using var session = OpenBlank(GameVersion.B);
        var state = EntralinkService.GetState(session);
        Assert.False(state.IsB2W2);
        Assert.Throws<NotSupportedException>(() => EntralinkService.SetPassPower(session, 0, 0));
    }

    // ── Helpers ──

    private static SaveEngineSession OpenBlank(GameVersion version) =>
        new(BlankSaveFile.Get(version, "PKForge", LanguageID.English), null);

    private static SaveEngineSession Reopen(SaveEngineSession session)
    {
        var bytes = session.Serialize().ToArray();
        // Blank and zero-filled images are not all recognizer-visible (HoneyTreeTests, RtcRepairTests):
        // reparse those typed, through the same constructor a cartridge dump takes.
        SaveFile? save = session.SaveFile switch
        {
            SAV4HGSS => new SAV4HGSS(bytes),
            SAV6XY => new SAV6XY(bytes),
            SAV1 sav1 => new SAV1(bytes, LanguageID.English, sav1.Version),
            _ => SaveUtil.TryGetSaveFile(bytes, out var parsed) ? parsed : null,
        };
        Assert.NotNull(save);
        return new SaveEngineSession(save, null);
    }

    /// <summary>A full-size, zero-filled 512 KB HGSS image (the HoneyTreeTests workaround:
    /// the in-memory blank cannot write its checksums).</summary>
    private static SaveEngineSession OpenHgss()
    {
        var save = new SAV4HGSS(new byte[0x80000]) { Language = (int)LanguageID.English };
        return new SaveEngineSession(save, null);
    }

    /// <summary>A recognizer-visible Gen 3 image, as EmulatorEnvelopeTests builds it: one full slot
    /// of 14 sectors, re-checksummed by PKHeX itself.</summary>
    private static SaveEngineSession OpenGen3(GameVersion version, ushort tid = 1, ushort sid = 2)
    {
        var raw = new byte[0x20000];
        for (var id = 0; id < 14; id++)
        {
            var sector = raw.AsSpan(id * 0x1000);
            BinaryPrimitives.WriteUInt16LittleEndian(sector[0xFF4..], (ushort)id);
            BinaryPrimitives.WriteUInt32LittleEndian(sector[0xFF8..], 0x08012025);
            BinaryPrimitives.WriteUInt32LittleEndian(sector[0xFFC..], 1);
        }
        raw[6] = raw[7] = 0xFF; // SAV3.Japanese reads 0 here; an international 7-character OT name area does not
        if (version == GameVersion.E)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0xAC), 0x1234_5678);
            raw[0x900] = 1;
        }
        if (version == GameVersion.FR)
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0xAC), 1); // FRLG game code marker (SAV3 GetVersion)
        SAV3 save = version switch
        {
            GameVersion.E => new SAV3E(raw),
            GameVersion.FR => new SAV3FRLG(raw),
            _ => new SAV3RS(raw),
        };
        save.TID16 = tid;
        save.SID16 = sid;
        var written = save.Write().ToArray();
        Assert.True(SaveUtil.TryGetSaveFile(written, out var parsed), "synthetic Gen 3 image must be recognised");
        return new SaveEngineSession(parsed!, null);
    }

    /// <summary>EmulatorEnvelopeTests' Crystal SRAM: both empty Pokémon lists PKHeX keys on.</summary>
    private static byte[] Crystal()
    {
        var sram = new byte[0x8000];
        foreach (var list in new[] { 0x2865, 0x2D10 }) { sram[list] = 0; sram[list + 1] = 0xFF; }
        return new SAV2(sram, LanguageID.English, GameVersion.C).Write().ToArray();
    }
}
