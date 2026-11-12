using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The Gen 4 Pal Park block, against real Platinum and HeartGold saves (copied to a temp
/// directory, the originals are never written) and a blank Diamond/Pearl image for structure.
/// Tests that need a real save do nothing when the local test data is absent.
/// </summary>
public sealed class PalParkTests
{
    private const string Platinum = "Pokemon - Platinum Version (Europe).sav";
    private const string HeartGold = "Pokemon - Version Or HeartGold (France).sav";

    private static byte[]? RealSave(string file)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, ".local-testdata", "thor", "nds", file);
            if (!File.Exists(path)) continue;
            var copy = Path.Combine(Path.GetTempPath(), $"pkforge-palpark-{Guid.NewGuid():N}.sav");
            File.Copy(path, copy);
            try { return File.ReadAllBytes(copy); }
            finally { File.Delete(copy); }
        }
        return null;
    }

    private static SaveEngineSession Open(byte[] data) =>
        new((SaveFile)SaveUtil.GetSaveFile(data)!, null);

    /// <summary>A legal FireRed Pokémon (PKHeX's own wild encounter template).</summary>
    private static PK3 FireRedMon()
    {
        var fr = BlankSaveFile.Get(GameVersion.FR, "Sof", LanguageID.English);
        var slot = EncounterMovesetGenerator.GenerateEncounters(new PK3 { Species = (ushort)Species.Pidgey, Language = 2 }, fr,
                ReadOnlyMemory<ushort>.Empty, GameVersion.FR)
            .OfType<EncounterSlot3>().First();
        var pk = (PK3)slot.ConvertToPKM(fr);
        Assert.True(new LegalityAnalysis(pk).Valid);
        return pk;
    }

    [Theory]
    [InlineData(Platinum)]
    [InlineData(HeartGold)]
    public void RealSavesHaveSixEmptyGameShapedSlots(string file)
    {
        if (RealSave(file) is not { } data) return;
        using var session = Open(data);
        Assert.True(PalParkService.IsSupported(session));
        var state = PalParkService.GetState(session);
        Assert.Equal(6, state.Slots.Count);
        Assert.All(state.Slots, slot => Assert.False(slot.Occupied));
        var block = PalParkService.Block((SAV4)session.SaveFile);
        var empty = PalParkService.EmptySlot();
        for (var i = 0; i < 6; i++)
            Assert.Equal(empty, block.Slice(i * 0xEC, 0xEC).ToArray());
    }

    [Theory]
    [InlineData(Platinum)]
    [InlineData(HeartGold)]
    public void AConvertedFireRedMonSurvivesExportAndReopenLegal(string file)
    {
        if (RealSave(file) is not { } data) return;
        var pk3 = FireRedMon();
        byte[] written;
        using (var session = Open(data))
        {
            var preview = PalParkService.Put(session, 2, PalParkSource.FromBytes(Party(pk3), "PK3"), allowNonGba: false);
            Assert.True(preview.Allowed, preview.Refusal);
            Assert.True(preview.FromGba);
            Assert.True(preview.Legal, preview.Verdict);
            written = PalParkService.ReadSlot((SAV4)session.SaveFile, 2) is var w ? Party(w) : [];
            data = session.Serialize().ToArray();
        }

        using var reopened = Open(data);
        Assert.True(reopened.SaveFile.ChecksumsValid);
        var back = PalParkService.ReadSlot((SAV4)reopened.SaveFile, 2);
        Assert.Equal(written, Party(back));
        Assert.Equal(pk3.Species, back.Species);
        Assert.Equal(Locations.Transfer3, back.MetLocation);
        Assert.Equal(GameVersion.FR, back.Version);
        Assert.True(back.Stat_HPMax > 0);
        Assert.True(new LegalityAnalysis(back).Valid);
        var state = PalParkService.GetState(reopened);
        Assert.Equal(1, state.Occupied);
        Assert.True(state.Slots[2].Legal, state.Slots[2].Verdict);

        PalParkService.Clear(reopened, 2);
        Assert.Equal(PalParkService.EmptySlot(), PalParkService.Block((SAV4)reopened.SaveFile).Slice(2 * 0xEC, 0xEC).ToArray());
        Assert.Equal(0, PalParkService.GetState(reopened).Occupied);
    }

    [Fact]
    public void ANativeGen4MonNeedsHaX()
    {
        using var session = Blank();
        var pk = new PK4 { Species = (ushort)Species.Turtwig, Language = 2, OriginalTrainerName = "SOF", PID = 1, Version = GameVersion.D };
        pk.CurrentLevel = 5;
        pk.RefreshChecksum();
        session.SaveFile.SetBoxSlotAtIndex(pk, 0, 0, EntityImportSettings.None);
        var refused = PalParkService.Put(session, 0, PalParkSource.FromSave(0, 0), allowNonGba: false);
        Assert.False(refused.Allowed);
        Assert.Equal(0, PalParkService.GetState(session).Occupied);

        var hax = PalParkService.Put(session, 0, PalParkSource.FromSave(0, 0), allowNonGba: true);
        Assert.True(hax.Allowed);
        Assert.False(hax.FromGba);
        Assert.NotEmpty(hax.Warnings);
        Assert.Equal(1, PalParkService.GetState(session).Occupied);
    }

    [Fact]
    public void BlankDiamondPearlBlockSitsAtItsOffsetAndClearsTheLock()
    {
        using var session = Blank();
        var save = (SAV4DP)session.SaveFile;
        Assert.True(PalParkService.OffsetDP + PalParkService.BlockSize <= save.General.Length);
        for (var i = 0; i < 6; i++) PalParkService.Clear(session, i);
        Assert.Equal(0, PalParkService.GetState(session).Occupied);
        Assert.False(PalParkService.GetState(session).Lock.Armed);

        var block = PalParkService.Block(save);
        block[PalParkService.MacOffset] = 0x12;
        block[PalParkService.TrainerIdsOffset] = 7;
        block[PalParkService.RtcOffsetOffset] = 9;
        var state = PalParkService.GetState(session);
        Assert.True(state.Lock.Armed);
        Assert.Equal(1, state.Lock.TrainersRecorded);

        Assert.True(PalParkService.ClearLock(session, history: true));
        state = PalParkService.GetState(session);
        Assert.False(state.Lock.Armed);
        Assert.Equal(0, state.Lock.TrainersRecorded);
        Assert.Equal(9, PalParkService.Block(save)[PalParkService.RtcOffsetOffset]); // never touched
    }

    [Fact]
    public void FillPutsSourcesIntoTheEmptySlots()
    {
        using var session = Blank();
        for (var i = 0; i < 6; i++) PalParkService.Clear(session, i);
        var pk3 = FireRedMon();
        var sources = Enumerable.Repeat(PalParkSource.FromBytes(Party(pk3), "PK3"), 8);
        var (placed, _) = PalParkService.Fill(session, sources, allowNonGba: false);
        Assert.Equal(6, placed);
        Assert.Equal(6, PalParkService.GetState(session).Occupied);
    }

    [Fact]
    public void OtherGamesAreRefused()
    {
        using var session = new SaveEngineSession(BlankSaveFile.Get(GameVersion.B, "Sof", LanguageID.English), null);
        Assert.False(PalParkService.IsSupported(session));
        Assert.Throws<NotSupportedException>(() => PalParkService.GetState(session));
    }

    private static byte[] Party(PKM pk)
    {
        var data = new byte[pk.SIZE_PARTY];
        pk.WriteDecryptedDataParty(data);
        return data;
    }

    private static SaveEngineSession Blank() =>
        new(new SAV4DP(new byte[0x80000]) { Language = (int)LanguageID.English }, null);
}
