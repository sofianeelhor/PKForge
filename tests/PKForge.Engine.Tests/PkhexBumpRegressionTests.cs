using PKForge.Domain;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Behaviour brought by the PKHeX bump to upstream 26.10.18+ (kwsch/PKHeX after 96e33e743)
/// that PKForge relies on.
/// </summary>
[Collection(nameof(LegalityEraCollection))] // MakeActive sets the process-wide Game Boy / GBA era
public sealed class PkhexBumpRegressionTests
{
    private static readonly string[] HeartGoldSaves =
    [
        "thor/nds/Pokemon - HeartGold Version (axekin.com).sav",
        "thor/nds/Pokemon - Version Or HeartGold (France).sav",
    ];

    private static byte[]? Sample(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", relative);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>
    /// Before upstream 9cfd47942, SAV4HGSS stamped its internal "HGSS" lump value on every load,
    /// before the block offsets were known: it landed on byte 0x1C of the general block (the
    /// trainer's ROM code sits at Trainer1 + 0x1C). The real saves already carry that stamp from
    /// earlier PKHeX writes, so a copy is first given a different byte there (with a valid block
    /// checksum); loading, editing and writing it must leave that byte and the ROM code alone.
    /// </summary>
    [Fact]
    public void HeartGoldLoadNoLongerStampsTheGeneralBlock()
    {
        foreach (var file in HeartGoldSaves)
        {
            if (Sample(file) is not { } original) continue;

            // A copy whose general block byte 0x1C differs from any GameVersion stamp.
            var prepared = (SAV4HGSS)SaveUtil.GetSaveFile(original.ToArray())!;
            Assert.Equal(GameVersion.HG, prepared.Version);
            prepared.General[0x1C] = 0x00;
            var bytes = prepared.Write().ToArray();
            Assert.Equal(original.Length, bytes.Length);
            AssertStampFree(bytes);

            var engine = new SaveEngine();
            Assert.Equal("HeartGold", engine.TryDescribe(bytes, file)!.GameName);

            // Untouched: byte for byte.
            using (var session = engine.OpenSession(bytes, file))
                Assert.True(session.Serialize().Span.SequenceEqual(bytes), $"{file}: no-op write changed bytes");

            // Edited: the stray byte and the ROM code survive, and the edition stays HeartGold.
            byte[] edited;
            using (var session = engine.OpenSession(bytes, file))
            {
                session.SetTrainer(session.GetTrainer() with { Money = 4321 });
                edited = session.Serialize().ToArray();
            }
            AssertStampFree(edited);
            Assert.Equal(4321u, SaveUtil.GetSaveFile(edited)!.Money);
            Assert.Equal("HeartGold", engine.TryDescribe(edited, file)!.GameName);
        }
    }

    /// <summary>Loading must not stamp anything: the active general block keeps 0x00 at 0x1C and the HG ROM code.</summary>
    private static void AssertStampFree(byte[] bytes)
    {
        var sav = (SAV4HGSS)SaveUtil.GetSaveFile(bytes.ToArray())!;
        Assert.Equal(0x00, sav.General[0x1C]);
        Assert.Equal((byte)GameVersion.HG, sav.ROMCode);
        Assert.Equal(GameVersion.HG, sav.Version);
    }

    /// <summary>
    /// FireRed on Nintendo Switch (upstream 1db2d3c3b): a FireRed Pokémon sent to HOME from the
    /// Switch release carries version FRX (56). Scarlet/Violet keeps that origin and must name it
    /// and judge the Pokémon legal; Sword/Shield stores it as Sword with HOME's "FireRed (VC)"
    /// met location, which must be legal too.
    /// </summary>
    [Fact]
    public void FireRedSwitchOriginIsNamedAndLegal()
    {
        Assert.Equal(56, (int)GameVersion.FRX);
        Assert.Equal(57, (int)GameVersion.LGX);
        Assert.Equal("FireRed [NX]", GameInfo.GetVersionName(GameVersion.FRX));
        Assert.Equal("LeafGreen [NX]", GameInfo.GetVersionName(GameVersion.LGX));

        var trainer = new SimpleTrainerInfo(GameVersion.FR)
        {
            OT = "SOF", TID16 = 12345, SID16 = 54321, Language = (int)LanguageID.English, Context = EntityContext.Gen3,
        };
        var template = new PK3 { Species = (ushort)Species.Psyduck };
        var encounter = EncounterMovesetGenerator.GenerateEncounters(template, trainer, ReadOnlyMemory<ushort>.Empty, GameVersion.FR)
            .OfType<EncounterSlot3>().First();
        var pk3 = (PK3)encounter.ConvertToPKM(trainer);
        var home = PKH.ConvertFromPKM(pk3);
        Assert.Equal(GameVersion.FRX, home.Version);

        var pk9 = home.ConvertToPK9();
        Assert.NotNull(pk9);
        Assert.Equal(GameVersion.FRX, pk9!.Version);
        Assert.Equal(EntityContext.Gen3, pk9.Version.GetContextFromSaved());
        AssertImportedLegal(9, pk9, "FireRed [NX]");

        var pk8 = home.ConvertToPK8();
        Assert.NotNull(pk8);
        Assert.Equal(GameVersion.SW, pk8!.Version);
        Assert.Equal(LocationsHOME.SWFR, pk8.MetLocation);
        AssertImportedLegal(8, pk8, "Sword");
    }

    /// <summary>Received into a blank save of that generation the way the player would (import into a box).</summary>
    private static void AssertImportedLegal(int generation, PKM pk, string versionName)
    {
        using var session = new SaveEngine().OpenBlankSession(generation);
        session.MakeActive();
        // The Switch game takes the Pokémon in as its current handler, as it does for every
        // Virtual Console transfer.
        var owner = session.GetTrainer();
        var receiver = new SimpleTrainerInfo(generation == 9 ? GameVersion.SL : GameVersion.SW)
        {
            OT = owner.Name, TID16 = (ushort)owner.TID, SID16 = (ushort)owner.SID, Gender = (byte)owner.Gender,
            Language = (int)LanguageID.English,
        };
        ((IHandlerUpdate)pk).UpdateHandler(receiver);
        Assert.Equal(1, pk.CurrentHandler);
        pk.RefreshChecksum();
        var bytes = new byte[pk.SIZE_STORED];
        pk.WriteDecryptedDataStored(bytes);
        Assert.True(session.ImportSlot(0, 0, bytes), $"Gen {generation}: import refused");
        Assert.Equal(versionName, session.GetMetInfo(0, 0).VersionName);
        var report = new LegalityService().Analyze(session, 0, 0);
        Assert.True(report.Valid, $"Gen {generation}: " + string.Join("\n", report.Lines));
    }

    /// <summary>
    /// Upstream now checks Gen 3/4 EVs against the experience the Pokémon has gained since it was
    /// met (EffortExpLegality): a fresh low-level catch cannot carry full EVs. Auto-Legality must
    /// still hand back a legal Pokémon for a Showdown set that asks for them: at Level 5 the EVs
    /// are capped at the 100 vitamins give when needed (and the player is told), at Level 60 they
    /// are kept. (Not Level 50: Auto-Legality turns a Level 50 request into Level 100.)
    /// </summary>
    [Theory]
    [InlineData(GameVersion.E, "Zigzagoon")]
    [InlineData(GameVersion.FR, "Pidgey")]
    [InlineData(GameVersion.Pt, "Bidoof")]
    [InlineData(GameVersion.HG, "Sentret")]
    public void EffortValuesOnALowLevelGen3Or4SetStayLegal(GameVersion version, string species)
    {
        var save = BlankSaveFile.Get(version, "PKForge", LanguageID.English);
        using var session = new SaveEngineSession(save, null);
        foreach (var (slot, level) in new[] { (0, 5), (1, 60) })
        {
            var set = $"{species}\nLevel: {level}\nEVs: 252 Atk / 252 Spe / 4 HP";
            var outcome = new LegalizerService().GenerateFromShowdown(session, 0, slot, set);
            Assert.True(outcome.Success, $"{version} L{level}: {outcome.Message}");
            var made = save.GetBoxSlotAtIndex(0, slot);
            var la = new LegalityAnalysis(made);
            Assert.True(la.Valid, $"{version} L{level}: {la.Report()}");
            Assert.Equal(level, made.CurrentLevel);
            var lowered = outcome.Message.Contains("EVs lowered", StringComparison.Ordinal);
            if (lowered)
                Assert.All(new[] { made.EV_HP, made.EV_ATK, made.EV_SPE }, ev => Assert.InRange(ev, 0, 100));
            else
                Assert.Equal(252, made.EV_ATK);
        }
    }

    /// <summary>
    /// Upstream 1db2d3c3b maps PK3 to HOME's PK8 side data, so PKHeX's converter, asked for a PK3
    /// from anything past Gen 3, went PK8 -> HOME -> PK8 forever (and held PKForge's transfer lock,
    /// freezing every other transfer). The fork's guard restores "no transfer route": PKForge
    /// then takes its own downgrade path.
    /// </summary>
    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(9)]
    public async Task ConvertingTowardGen3EndsWithNoRoute(int generation)
    {
        var pk = EntityBlank.GetBlank((byte)generation);
        pk.Species = (ushort)Species.Torchic;
        pk.CurrentLevel = 10;
        var converted = await Task.Run(() => EntityConverter.ConvertToType(pk, typeof(PK3), out _))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(converted);
    }

    /// <summary>
    /// Ported from upstream WallpaperUtil (86afdc9f4): Diamond/Pearl's special wallpapers 17-24
    /// are the "dp" pictures; the old port asked for "box_wp17" with no suffix, which does not exist.
    /// </summary>
    [Theory]
    [InlineData(GameVersion.DP, 16, "box_wp17dp")]
    [InlineData(GameVersion.D, 23, "box_wp24dp")]
    [InlineData(GameVersion.Pt, 16, "box_wp17pt")]
    [InlineData(GameVersion.HG, 16, "box_wp17hgss")]
    [InlineData(GameVersion.P, 3, "box_wp04dp")]
    public void DiamondPearlSpecialWallpapersHaveTheirPicture(GameVersion version, int index, string expected) =>
        Assert.Equal(expected, BoxLayoutService.GetWallpaperResourceName(version, index));

    /// <summary>
    /// Luminescent Platinum's internal identifier must not take an official version ID: upstream
    /// assigned 54-57 (Winds, Waves, FireRed and LeafGreen on Switch) after our fork used 54.
    /// </summary>
    [Fact]
    public void LuminescentIdentifierStaysOutOfTheStoredVersionRange()
    {
        Assert.False(GameVersion.BDSPLUMI.IsValidSavedVersion());
        Assert.DoesNotContain(GameVersion.BDSPLUMI, GameUtil.GameVersions);
        Assert.Equal(EntityContext.Gen8bLumi, GameVersion.BDSPLUMI.Context);
        Assert.Equal(GameVersion.BDSPLUMI, EntityContext.Gen8bLumi.GetSingleGameVersion());
        Assert.Equal(SaveFileType.BDSPLumi, GameVersion.BDSPLUMI.SaveFileType);
        Assert.Equal(8, GameVersion.BDSPLUMI.Generation);
    }
}
