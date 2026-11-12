using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// The detection confusion matrix: every real save available locally (.local-testdata, gitignored;
/// each case skips when its file is absent) must route to exactly its own engine, be claimed by
/// no other detector (so <see cref="SaveParser.HackRoute"/>'s order never decides), describe
/// itself correctly, carry no layout risk, and survive an untouched open → write byte for byte.
/// The adversarial half builds variants from those saves (emulator padding, a truncated file, a
/// torn newest slot, an emptied party, no Pokémon at all) and checks the safety contract: a hack
/// is either read by its own engine or refused / flagged, never written as vanilla unwarned and
/// never claimed by another hack's engine. Samples are copied to a temporary folder first.
/// </summary>
public sealed class DetectionMatrixTests
{
    private static string? Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var root = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata");
        return root is not null && Directory.Exists(root) ? root : null;
    }

    /// <summary>The sample's bytes, read from a private temporary copy (the originals stay untouched).</summary>
    private static byte[]? Sample(string relative)
    {
        if (Root() is not { } root) return null;
        var source = Path.Combine(root, relative);
        if (!File.Exists(source)) return null;
        var copy = Path.Combine(Path.GetTempPath(), "pkforge-matrix-" + Guid.NewGuid().ToString("N"));
        File.Copy(source, copy);
        try { return File.ReadAllBytes(copy); }
        finally { File.Delete(copy); }
    }

    public static TheoryData<string, SaveFormat?, string, string, string> Matrix => new()
    {
        { "issues/22/Pokemon - Emerald Version (USA, Europe).sav", null, "Emerald", "Stefan", "54:22:32" },
        // Discord: French Emerald with a Hall of Fame and a recorded battle (sectors 28 and 31).
        { "issues/emerald-fr-hof/Pokemon - Version Emeraude (France).srm", null, "Emerald", "Sobas", "112:51:16" },
        { "thor/retroarch/POKEMON_EMER_BPEJ_00.srm", null, "Emerald", "ハルカ", "14:06:51" },
        { "thor/retroarch/mGBA/POKEMON_EMER_BPEJ_00.srm", null, "Emerald", "ハルカ", "14:06:51" },
        { "thor/retroarch/mGBA/POKEMON_EMER_BPEJ_00_trimmed.sav", null, "Emerald", "ハルカ", "14:06:51" },
        { "thor/retroarch/mGBA/Pokemon Fire Red.sav", null, "FireRed", "RoC", "34:34:31" },
        { "thor/retroarch/mGBA/Pokémon Unbound (v2.1.1.1).srm", SaveFormat.Unbound, "Unbound", "Sof", "0:26:38" },
        { "thor/retroarch/mGBA/Radical-red-champ.sav", SaveFormat.RadicalRed, "Radical Red", "sri", "23:37:25" },
        { "thor/nds/Pokemon - HeartGold Version (axekin.com).sav", null, "HeartGold", "SOF", "0:48:47" },
        { "thor/nds/Pokemon - Version Or HeartGold (France).sav", null, "HeartGold", "SOF", "0:48:47" },
        { "thor/nds/Pokemon - Platinum Version (Europe).sav", null, "Platinum", "VWWXX", "0:23:32" },
        { "thor/nds/Pokemon - Version Noire (France) (NDSi Enhanced).sav", null, "Black", "mok", "8:13:19" },
        { "thor/dolphin/GC/EUR/Card A/01-GXXP-PokemonXD.gci", null, "XD: Gale of Darkness", "MICHAEL", "0:02:23" },
        { "romhacks/recharged-yellow.sav", SaveFormat.RechargedYellow, "Recharged Yellow", "YELLOW", "0:25:16" },
        { "romhacks/quetzal-9.1.sav", SaveFormat.Quetzal, "Quetzal", "Camron", "15:03:24" },
        { "romhacks/quetzal-bills.sav", SaveFormat.Quetzal, "Quetzal", "Mia", "35:07:46" },
        { "romhacks/soulgold.sav", SaveFormat.SoulGold, "Soul Gold", "Mia", "0:54:23" },
        { "romhacks/heart-and-soul.sav", SaveFormat.Expansion, "Heart and Soul 2.0", "Mia", "0:12:14" },
        { "romhacks/lazarus.sav", SaveFormat.Expansion, "Lazarus", "Mia", "0:30:43" },
        { "romhacks/lazarus-pokedex.srm", SaveFormat.Expansion, "Lazarus", "Shae", "177:03:08" },
        { "romhacks/emerald-seaglass.sav", SaveFormat.Expansion, "pokeemerald-expansion", "Mia", "1:13:45" },
        { "romhacks/emerald-enhanced.sav", SaveFormat.Expansion, "Emerald Enhanced", "Mia", "1:23:21" },
        { "romhacks/perfect-emerald.sav", SaveFormat.Expansion, "Perfect Emerald", "Mia", "0:01:10" },
    };

    private const string Batch = "romhacks/batch-2026-11-12/";

    /// <summary>The 2026-11-12 batch of real hack saves (the file name is the ground truth).
    /// Supported hacks: their own engine. Unsupported ones: never claimed by any hack engine,
    /// and either refused outright (not a recognized save) or flagged with the given risk.</summary>
    public static TheoryData<string, SaveFormat?, string?, LayoutRiskKind?> BatchMatrix => new()
    {
        { Batch + "loose/G3C.Kanto.D.Pokemon RechargedYellow Edition.sav", SaveFormat.RechargedYellow, "Recharged Yellow", null },
        { Batch + "loose/Pokemon Quetzal 9.1.sav", SaveFormat.Quetzal, "Quetzal", null },
        { Batch + "loose/PokemonQuetzalEnglishAlpha9v0.sav", SaveFormat.Quetzal, "Quetzal", null },
        { Batch + "loose/PokemonQuetzalSpanishAlpha9v0.sav", SaveFormat.Quetzal, "Quetzal", null },
        { Batch + "saves/saves/Pokemon - Quetzal.sav", SaveFormat.Quetzal, "Quetzal", null },
        { Batch + "romhack/romhack saves/Pokemon - Soul Gold.sav", SaveFormat.SoulGold, "Soul Gold", null },
        { Batch + "saves/saves/Pokemon - Soul Gold.sav", SaveFormat.SoulGold, "Soul Gold", null },
        { Batch + "saves/saves/Pokemon - Heart and Soul.sav", SaveFormat.Expansion, "Heart and Soul 2.0", null },
        { Batch + "saves/saves/Pokemon - Lazarus.sav", SaveFormat.Expansion, "Lazarus", null },
        { Batch + "mgba/mGBA Romhack Saves/Pokemon - Lazarus.srm", SaveFormat.Expansion, "Lazarus", null },
        { Batch + "saves/saves/Pokemon - Emerald Seaglass.sav", SaveFormat.Expansion, "pokeemerald-expansion", null },
        { Batch + "mgba/mGBA Romhack Saves/Pokemon - Emerald Seaglass.srm", SaveFormat.Expansion, "pokeemerald-expansion", null },
        { Batch + "saves/saves/Pokemon - Emerald Enhanced.sav", SaveFormat.Expansion, "Emerald Enhanced", null },
        { Batch + "romhack/romhack saves/Pokemon - Emerald Enhanced.sav", SaveFormat.Expansion, "Emerald Enhanced", null },
        { Batch + "saves/saves/Pokemon - Perfect Emerald.sav", SaveFormat.Expansion, "Perfect Emerald", null },
        { Batch + "romhack/romhack saves/Pokemon - GS Chronicles.sav", SaveFormat.GsChronicles, "GS Chronicles", null },
        // Elite Redux: its own engine (one rotating 20-sector slot, 52-byte packed Pokémon).
        { Batch + "saves/saves/Pokemon - Elite Redux.sav", SaveFormat.EliteRedux, "Elite Redux", null },
        // Standard expansion saves the engine names by where their SaveBlock2 keeps the encryption
        // key (Lazarus 0xB0, Emerald Enhanced 0x34, Lucid 0xA4: only there do money and coins
        // decode). Seaglass and Emerald Ex keep upstream's 0xAC and nothing structural sets them
        // apart from any other expansion hack, so they stay generic.
        // Lucid: Alolan Ninetales with Powder Snow / Ice Shard; Emerald Ex: Sceptile with Leaf
        // Blade / Leaf Storm, Kyurem with Freeze Shock / Ice Burn, round-tripping byte for byte.
        { Batch + "mgba/mGBA Romhack Saves/Pokemon - Lucid.srm", SaveFormat.Expansion, "Lucid", null },
        { Batch + "romhack/romhack saves/Pokemon - Emerald Ex.sav", SaveFormat.Expansion, "pokeemerald-expansion", null },
        // The batch Heart & Soul save (player "Yuki", standing at X = 2): the same 14-box, 3968-byte
        // layout as the other two samples, detected by its SaveBlock1 magic (never the player's X).
        { Batch + "romhack/romhack saves/Pokemon - Heart and Soul.sav", SaveFormat.Expansion, "Heart and Soul 2.0", null },
        // Team Rocket Edition: its own expansion profile (4084-byte chunks, 14 boxes, its own
        // record header), editable where that header is proven.
        { Batch + "mgba/mGBA Romhack Saves/Pokemon - Team Rocket Edition.srm", SaveFormat.Expansion, "Team Rocket Edition", null },
        // Black Pearl Emerald before 2.1 (grown SaveBlock1, freed SaveBlock2, its own item enum)
        // and Fire Emerald (box names 0xA0 later): their own expansion profiles, editable.
        { Batch + "romhack/romhack saves/Pokemon - Black Pearl Emerald.sav", SaveFormat.Expansion, "Black Pearl Emerald", null },
        { Batch + "saves/saves/Pokemon - Fire Emerald.sav", SaveFormat.Expansion, "Fire Emerald", null },
        // Origin HeartGold (NDS): read by its own engine (HeartGold envelope, the hack's block sizes, 24 boxes).
        { Batch + "loose/Pokemon - Origin HeartGold Version.sav", SaveFormat.OriginHeartGold, "Origin HeartGold", null },
        // Emerald Exceeded (both builds): its own session on the expansion route (4084-byte chunks,
        // 13 boxes of plaintext 88-byte records), editable where the bytes prove the fields.
        { Batch + "saves/saves/Pokemon - Emerald Exceeded.sav", SaveFormat.Expansion, "Emerald Exceeded", null },
        { Batch + "saves/saves/Pokemon - Exceeded.sav", SaveFormat.Expansion, "Emerald Exceeded", null },
        // FireRed binary hacks on 0xFF0-chunked retail blocks, read by their own engine: Fire Red
        // Extended (CFRU windows, encrypted PK3, species past 411) and Crystal Advance Redux (one
        // flat 0xF80 window, plain G-A-E-M records with a zero checksum).
        { Batch + "loose/Pokemon Fire Red Extended 3.5.3.sav", SaveFormat.FireRedExtended, "Fire Red Extended", null },
        { Batch + "romhack/romhack saves/Pokemon - Crystal Advance - Redux.sav", SaveFormat.CrystalAdvanceRedux, "Crystal Advance Redux", null },
        // Recognized hacks with no editor: opened read-only under their own name (CorruptingLayout:
        // no confirmation lifts it), or refused naming them when no parser reads them (null game).
        { Batch + "saves/saves/Pokemon - R.O.W.E.sav", null, "R.O.W.E.", LayoutRiskKind.CorruptingLayout },
        { Batch + "mgba/mGBA Romhack Saves/Pokemon - R.O.W.E.srm", null, "R.O.W.E.", LayoutRiskKind.CorruptingLayout },
        // Never saved: 128 KB of erased flash, not a save of any game (refused, unnamed).
        { Batch + "mgba/mGBA Romhack Saves/Pokemon - Black Pearl Emerald.srm", null, null, null },
    };

    /// <summary>The recognized-hack name <see cref="RecognizedHacks.Identify"/> must give each
    /// sample. Every other save in the matrix (vanilla, supported hacks) must get none.</summary>
    private static readonly Dictionary<string, string> Recognized = new()
    {
        [Batch + "saves/saves/Pokemon - R.O.W.E.sav"] = "R.O.W.E.",
        [Batch + "mgba/mGBA Romhack Saves/Pokemon - R.O.W.E.srm"] = "R.O.W.E.",
    };

    [Fact]
    public void RecognizedHacksAreNamedAndNothingElseIs()
    {
        var files = Matrix.Select(row => (string)row[0]).Concat(BatchMatrix.Select(row => (string)row[0]))
            .Append("thor/melonds-internal/Pokemon - Version Or HeartGold (France).sav");
        foreach (var file in files)
        {
            if (Sample(file) is not { } bytes) continue;
            var expected = Recognized.GetValueOrDefault(file);
            Assert.True(expected == RecognizedHacks.Identify(RetroArchSaveContainer.Decode(bytes))?.Name, $"{file}: expected {expected ?? "no hack"}");
            if (expected is null) continue;
            var engine = new SaveEngine();
            if (engine.TryDescribe(bytes, file) is null)
            {
                // No parser reads it: the refusal names the hack and says it is not editable yet.
                var refusal = Assert.Throws<InvalidDataException>(() => engine.OpenSession(bytes, file));
                Assert.Contains($"Pokémon {expected} save", refusal.Message);
                Assert.Contains("cannot open or edit it yet", refusal.Message);
                continue;
            }
            var risk = engine.AssessLayoutRisk(bytes);
            Assert.Equal(LayoutRiskKind.CorruptingLayout, risk?.Kind);
            Assert.Contains($"Pokémon {expected} save", risk!.Reason);
            Assert.Contains("cannot edit it yet", risk.Reason);
        }
    }

    [Theory]
    [MemberData(nameof(BatchMatrix))]
    public void EveryBatchSaveIsReadByItsEngineOrKeptFromVanillaWrites(string file, SaveFormat? route, string? game, LayoutRiskKind? risk)
    {
        if (Sample(file) is not { } bytes) return;
        var engine = new SaveEngine();
        Assert.Equal(route, SaveParser.HackRoute(RetroArchSaveContainer.Decode(bytes)));
        Assert.Equal(route is null ? [] : [route.Value], Claims(bytes));
        Assert.Equal(game, engine.TryDescribe(bytes, file)?.GameName);

        if (game is null)
        {
            Assert.Throws<InvalidDataException>(() => engine.OpenSession(bytes, file));
            return;
        }
        Assert.Equal(risk, engine.AssessLayoutRisk(bytes)?.Kind);
        if (route is not null)
        {
            using var session = engine.OpenSession(bytes, file);
            Assert.True(session.Serialize().Span.SequenceEqual(bytes), "an untouched save must round-trip byte for byte");
        }
    }

    private static List<SaveFormat> Claims(byte[] bytes)
    {
        var decoded = RetroArchSaveContainer.Decode(bytes);
        var claims = new List<SaveFormat>();
        if (SaveParser.IsPokemonOriginHeartGold(decoded)) claims.Add(SaveFormat.OriginHeartGold);
        if (SaveParser.IsPokemonUnbound(decoded)) claims.Add(SaveFormat.Unbound);
        if (SaveParser.IsPokemonGsChronicles(decoded)) claims.Add(SaveFormat.GsChronicles);
        if (SaveParser.IsPokemonRadicalRed(decoded)) claims.Add(SaveFormat.RadicalRed);
        if (SaveParser.IsPokemonRechargedYellow(decoded)) claims.Add(SaveFormat.RechargedYellow);
        if (SaveParser.IsPokemonQuetzal(decoded)) claims.Add(SaveFormat.Quetzal);
        if (SaveParser.IsPokemonSoulGold(decoded)) claims.Add(SaveFormat.SoulGold);
        if (SaveParser.IsPokemonExpansion(decoded)) claims.Add(SaveFormat.Expansion);
        if (SaveParser.IsPokemonEliteRedux(decoded)) claims.Add(SaveFormat.EliteRedux);
        if (SaveParser.FireRedHackRoute(decoded) is { } fireRed) claims.Add(fireRed);
        return claims;
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void EverySaveRoutesToExactlyItsOwnEngine(string file, SaveFormat? route, string game, string trainer, string playTime)
    {
        if (Sample(file) is not { } bytes) return;
        var engine = new SaveEngine();

        Assert.Equal(route, SaveParser.HackRoute(RetroArchSaveContainer.Decode(bytes)));
        Assert.Equal(route is null ? [] : [route.Value], Claims(bytes));
        Assert.True(engine.Validate(bytes));

        var description = engine.TryDescribe(bytes, file);
        Assert.NotNull(description);
        Assert.Equal(game, description.GameName);
        Assert.Equal(trainer, description.TrainerName);
        Assert.Equal(playTime, description.PlayTime);

        Assert.Null(engine.AssessLayoutRisk(bytes));
        using var session = engine.OpenSession(bytes, file);
        Assert.True(session.Serialize().Span.SequenceEqual(bytes), "an untouched save must round-trip byte for byte");
        Assert.Null(engine.CheckWriteSafety(bytes, bytes, null));
    }

    [Fact]
    public void AnEmptyFileIsNotASave()
    {
        if (Sample("thor/melonds-internal/Pokemon - Version Or HeartGold (France).sav") is not { } bytes) return;
        var engine = new SaveEngine();
        Assert.Null(SaveParser.HackRoute(bytes));
        Assert.False(engine.Validate(bytes));
        Assert.Null(engine.TryDescribe(bytes));
        Assert.Throws<InvalidDataException>(() => engine.OpenSession(bytes));
    }

    private static IEnumerable<(string File, SaveFormat? Route)> GbaSamples() =>
        Matrix.Select(row => ((string)row[0], (SaveFormat?)row[1]))
            .Where(row => row.Item1.StartsWith("romhacks/", StringComparison.Ordinal) || row.Item1.Contains("/retroarch/") || row.Item1.StartsWith("issues/", StringComparison.Ordinal))
            .Concat(BatchMatrix.Select(row => ((string)row[0], (SaveFormat?)row[1])).Where(row => row.Item2 is not null and not SaveFormat.OriginHeartGold));

    /// <summary>The outcome the safety contract allows for a variant of a save whose real game is
    /// <paramref name="route"/> (null = vanilla): its own engine, or refused, or (for a hack only)
    /// the stock engine WITH a layout risk the player must face before any write.</summary>
    private static void AssertSafe(byte[] variant, SaveFormat? route, string label, bool vanillaMustStayClean = true)
    {
        var engine = new SaveEngine();
        var claims = Claims(variant);
        Assert.True(claims.Count <= 1, $"{label}: claimed by {string.Join(", ", claims)}");
        var actual = SaveParser.HackRoute(RetroArchSaveContainer.Decode(variant));
        if (actual is not null)
        {
            Assert.True(actual == route, $"{label}: a {route?.ToString() ?? "vanilla"} save routed to {actual}");
            using var session = engine.OpenSession(variant, label);
            Assert.True(session.Serialize().Span.SequenceEqual(variant), $"{label}: no-op write changed bytes");
            return;
        }
        ISaveEngineSession opened;
        try { opened = engine.OpenSession(variant, label); }
        catch (InvalidDataException) { return; } // refused: nothing can be written
        using (opened)
        {
            var risk = engine.AssessLayoutRisk(variant);
            if (route is not null)
                Assert.True(risk is not null, $"{label}: a {route} save opened as writable vanilla with no warning");
            else if (vanillaMustStayClean)
                Assert.True(risk is null, $"{label}: a vanilla save was flagged: {risk?.Reason}");
        }
    }

    [Fact]
    public void EmulatorPaddingAndRtcFootersKeepTheRoute()
    {
        foreach (var (file, route) in GbaSamples())
        {
            if (Sample(file) is not { } bytes) continue;
            var payload = SramPadding.Trim(bytes);
            var baseline = new SaveEngine().AssessLayoutRisk(bytes)?.Kind; // a read-only engine (Team Rocket Edition) stays read-only
            foreach (var (label, variant) in new[]
            {
                ("+8 KB 0xFF", payload.Concat(Enumerable.Repeat((byte)0xFF, 0x2000)).ToArray()),
                ("+RTC footer", payload.Concat(new byte[16]).ToArray()),
            })
            {
                Assert.Equal(route, SaveParser.HackRoute(variant));
                var engine = new SaveEngine();
                using var session = engine.OpenSession(variant, file);
                Assert.True(session.Serialize().Span.SequenceEqual(variant), $"{file} {label}");
                Assert.Equal(baseline, engine.AssessLayoutRisk(variant)?.Kind);
            }
        }
    }

    [Fact]
    public void TruncatedFilesAreRefusedNeverMisrouted()
    {
        foreach (var (file, _) in GbaSamples())
        {
            if (Sample(file) is not { } bytes) continue;
            var payload = SramPadding.Trim(bytes);
            foreach (var variant in new[] { payload[..^1], payload[..0x10000], payload[..0x1F000] })
            {
                Assert.Null(SaveParser.HackRoute(variant));
                Assert.Throws<InvalidDataException>(() => new SaveEngine().OpenSession(variant, file));
            }
        }
    }

    [Fact]
    public void ATornNewestSlotIsNeverWrittenAsAnotherGame()
    {
        foreach (var (file, route) in GbaSamples())
        {
            if (Sample(file) is not { } bytes) continue;
            var payload = SramPadding.Trim(bytes);
            var torn = payload.ToArray();
            var sector = LiveSector(torn, 1);
            if (sector < 0) continue;
            torn[sector + 0x10] ^= 0x5A; // the newest slot's SaveBlock1 no longer matches its checksum
            // A vanilla save whose newest slot is torn is legitimately refused (corrupting layout).
            AssertSafe(torn, route, $"{file} torn", vanillaMustStayClean: false);
        }
    }

    [Fact]
    public void HackSavesWithoutPokemonAreDetectedOrFlagged()
    {
        foreach (var (file, route) in GbaSamples())
        {
            if (Sample(file) is not { } bytes) continue;
            var payload = SramPadding.Trim(bytes);
            var emptyParty = EmptyParty(payload, route);
            AssertSafe(emptyParty, route, $"{file} empty party");
            AssertSafe(NoBoxMons(emptyParty), route, $"{file} no Pokémon");
        }
    }

    [Fact]
    public void RecognizedHacksStayUnwritableWithoutTheirPokemon()
    {
        foreach (var (file, name) in Recognized)
        {
            if (Sample(file) is not { } bytes) continue;
            var payload = SramPadding.Trim(bytes);
            var sector = LiveSector(payload, 1);
            var emptied = sector < 0 ? payload : Mutate(payload, sector, 0x234, [0]);
            foreach (var (label, variant) in new[] { ("empty party", emptied), ("no Pokémon", NoBoxMons(emptied)) })
            {
                Assert.True(Claims(variant).Count == 0, $"{file} {label}: claimed by {string.Join(", ", Claims(variant))}");
                var engine = new SaveEngine();
                ISaveEngineSession opened;
                try { opened = engine.OpenSession(variant, file); }
                catch (InvalidDataException) { continue; } // refused: nothing can be written
                opened.Dispose();
                Assert.True(engine.AssessLayoutRisk(variant) is not null, $"{file} {label}: opened as a writable save");
            }
        }

        // Fire Red Extended with an empty party is told apart from Radical Red by its encrypted
        // boxes and still reads through its own engine; with no Pokémon at all nothing tells it
        // from another CFRU-window hack, so no engine claims it and it never opens writable.
        if (Sample(Batch + "loose/Pokemon Fire Red Extended 3.5.3.sav") is { } fre)
        {
            var emptied = Mutate(fre, LiveSector(fre, 1), 0x34, [0]);
            Assert.False(SaveParser.IsPokemonRadicalRed(emptied));
            Assert.Equal([SaveFormat.FireRedExtended], Claims(emptied));
            var bare = NoFireRedBoxMons(emptied);
            Assert.Empty(Claims(bare));
            var engine = new SaveEngine();
            try
            {
                using var opened = engine.OpenSession(bare, "bare");
                Assert.NotNull(engine.AssessLayoutRisk(bare));
            }
            catch (InvalidDataException) { } // refused: nothing can be written
        }
        // Crystal Advance Redux needs its plain party to be told apart: emptied, it is never claimed
        // and never writable.
        if (Sample(Batch + "romhack/romhack saves/Pokemon - Crystal Advance - Redux.sav") is { } car)
        {
            var emptied = Mutate(car, LiveSector(car, 1), 0x34, [0]);
            Assert.Empty(Claims(emptied));
            var engine = new SaveEngine();
            try
            {
                using var opened = engine.OpenSession(emptied, "emptied");
                Assert.NotNull(engine.AssessLayoutRisk(emptied));
            }
            catch (InvalidDataException) { }
        }
    }

    [Fact]
    public void HeartAndSoulIsFoundByItsMagicNeverByThePlayersPosition()
    {
        // SaveBlock1+0 is the player's X coordinate: any value keeps the save Heart and Soul.
        if (Sample("romhacks/heart-and-soul.sav") is not { } bytes) return;
        var sector = LiveSector(bytes, 1);
        foreach (byte x in new byte[] { 0, 2, 7, 200 })
        {
            var moved = Mutate(bytes, sector, 0, [x, 0]);
            Assert.Equal(x, moved[sector]);
            Assert.Equal(Expansion.ExpansionGame.HeartAndSoul20, Expansion.ExpansionFormat.LooksLike(moved)?.Game);
        }
        // Without the magic it is not Heart and Soul (nor any recognized hack).
        var unmarked = Mutate(bytes, LiveSector(bytes, 2), 0x10E0 - 0xF80, [0, 0, 0, 0]);
        Assert.NotEqual(bytes, unmarked);
        Assert.NotEqual(Expansion.ExpansionGame.HeartAndSoul20, Expansion.ExpansionFormat.LooksLike(unmarked)?.Game);
    }

    [Fact]
    public void AHeartAndSoulSaveInAnotherLayoutIsNeverReadAsPerfectEmerald()
    {
        // The upstream Heart & Soul build writes Perfect Emerald's geometry (15 boxes, 4084-byte
        // chunks). Its magic must win: recognized, named and read-only, never Perfect Emerald.
        if (Sample("romhacks/perfect-emerald.sav") is not { } bytes) return;
        Assert.Equal(Expansion.ExpansionGame.PerfectEmerald, Expansion.ExpansionFormat.LooksLike(bytes)?.Game);
        var marked = Mutate(bytes, LiveSector(bytes, 2), 0x10E0 - 0xFF4, [0xBC, 0x28, 0xF8, 0xE8, 7, 0]);
        Assert.Null(Expansion.ExpansionFormat.LooksLike(marked));
        Assert.Empty(Claims(marked));
        Assert.Equal("Heart and Soul", RecognizedHacks.Identify(marked)?.Name);
        var engine = new SaveEngine();
        Assert.Equal("Heart and Soul", engine.TryDescribe(marked)?.GameName);
        Assert.Equal(LayoutRiskKind.CorruptingLayout, engine.AssessLayoutRisk(marked)?.Kind);
    }

    [Fact]
    public void AVanillaSaveWithOneForeignMonStaysVanillaButFlagged()
    {
        if (Sample("thor/retroarch/mGBA/POKEMON_EMER_BPEJ_00_trimmed.sav") is not { } bytes) return;
        Assert.True(SaveUtil.TryGetSaveFile(bytes.ToArray(), out var parsed));
        var save = (SAV3E)parsed!;
        var pk = (PK3)save.GetPartySlotAtIndex(0).Clone();
        pk.SpeciesInternal = 0x4000 | 252; // the expansion's high-bit packing: no retail species
        pk.RefreshChecksum();
        save.SetBoxSlotAtIndex(pk, 0, 0, EntityImportSettings.None);
        var injected = save.Write().ToArray();

        Assert.Null(SaveParser.HackRoute(injected));
        Assert.Empty(Claims(injected));
        Assert.Equal(LayoutRiskKind.SuspectedHack, new SaveEngine().AssessLayoutRisk(injected)?.Kind);
    }

    [Fact]
    public void ARecordedBattleNoLongerLocksAVanillaEmerald()
    {
        // GitHub #22: Emerald's recorded battle (sector 31) leaves 0xFF4 at zero, PKHeX stamped a
        // checksum there on every write, and the round-trip guard locked the save read-only.
        if (Sample("thor/retroarch/mGBA/POKEMON_EMER_BPEJ_00_trimmed.sav") is not { } bytes) return;
        var save = bytes.ToArray();
        var sector31 = save.AsSpan(0x1F000, 0x1000);
        sector31.Clear();
        for (var i = 0; i < 0xF80; i++) sector31[i] = (byte)(i * 7 + 1);

        var engine = new SaveEngine();
        Assert.Null(engine.AssessLayoutRisk(save));
        using (var session = engine.OpenSession(save))
            Assert.True(session.Serialize().Span.SequenceEqual(save));
    }

    [Fact]
    public void AGrownSaveBlockIsFlaggedEvenWithoutPokemon()
    {
        // A fresh save of a hack that grew SaveBlock2 (newer pokeemerald-expansion builds) holds
        // no Pokémon to betray it; its section 0 checksum covers more bytes than Emerald writes.
        if (Sample("thor/retroarch/mGBA/POKEMON_EMER_BPEJ_00_trimmed.sav") is not { } bytes) return;
        // Every id-0 sector is grown: this real save's newest slot is itself incomplete (two
        // id-0 sectors), so the slot the game and PKHeX read is the older one.
        var grown = bytes;
        for (var sector = 0; sector < 28 * 0x1000; sector += 0x1000)
            if (BinaryPrimitives.ReadUInt16LittleEndian(grown.AsSpan(sector + 0xFF4)) == 0)
                grown = Mutate(grown, sector, 0xF40, [0x42, 0, 0, 0], window: 0xF80);
        var risk = new SaveEngine().AssessLayoutRisk(grown);
        Assert.Equal(LayoutRiskKind.SuspectedHack, risk?.Kind);
        Assert.Contains("section 0", risk!.Reason);
    }

    private static readonly uint[] Signatures = [0x0801_2025, SaveParser.UnboundSectorSignature, SaveParser.GsChroniclesSectorSignature];

    /// <summary>The newest physical sector carrying logical id <paramref name="id"/>.</summary>
    private static int LiveSector(byte[] data, int id)
    {
        int best = -1;
        uint bestCounter = 0;
        for (var sector = 0; sector < 28; sector++)
        {
            var offset = sector * 0x1000;
            if (Array.IndexOf(Signatures, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 0xFF8))) < 0) continue;
            if (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 0xFF4)) != id) continue;
            var counter = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 0xFFC));
            if (best < 0 || counter >= bestCounter) { best = offset; bestCounter = counter; }
        }
        return best;
    }

    private static ushort Sum(ReadOnlySpan<byte> data, int length)
    {
        uint total = 0;
        for (var i = 0; i < length; i += 4) total += BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
        return (ushort)(total + (total >> 16));
    }

    /// <summary>Writes <paramref name="value"/> into a sector and re-seals it over the window the
    /// sector already validated under (the shortest one covering the edit), or <paramref name="window"/>.</summary>
    private static byte[] Mutate(byte[] source, int sector, int at, byte[] value, int? window = null)
    {
        var data = source.ToArray();
        var stored = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(sector + 0xFF6));
        var length = window ?? Enumerable.Range(1, 0xFF4 / 4).Select(i => i * 4)
            .FirstOrDefault(n => n >= at + value.Length && Sum(data.AsSpan(sector), n) == stored);
        if (length == 0)
            return data; // no retail-style window seals this sector: leave it as it is
        value.CopyTo(data, sector + at);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(sector + 0xFF6), Sum(data.AsSpan(sector), length));
        return data;
    }

    /// <summary>The party count set to zero, at the offset the save's own game keeps it.</summary>
    private static byte[] EmptyParty(byte[] payload, SaveFormat? route)
    {
        var heartAndSoul = route == SaveFormat.Expansion &&
            Expansion.ExpansionFormat.LooksLike(payload)?.Game == Expansion.ExpansionGame.HeartAndSoul20;
        var at = route switch
        {
            SaveFormat.RadicalRed or SaveFormat.Unbound or SaveFormat.GsChronicles or SaveFormat.FireRedExtended or SaveFormat.CrystalAdvanceRedux => 0x34,
            SaveFormat.RechargedYellow => RechargedYellow.RechargedYellowFormat.PartyCountOffset,
            SaveFormat.Quetzal => 0x6A4,
            _ when heartAndSoul => 0x238,
            _ => 0x234,
        };
        // Elite Redux keeps SaveBlock2 in ids 0-1: its SaveBlock1 (and party count) starts at id 2.
        var sector = LiveSector(payload, route == SaveFormat.EliteRedux ? 2 : 1);
        return sector < 0 ? payload : Mutate(payload, sector, at, [0]);
    }

    /// <summary>Every PC record of a 0xFF0-chunk FireRed hack zeroed, re-sealed over the CFRU windows.</summary>
    private static byte[] NoFireRedBoxMons(byte[] payload)
    {
        var data = payload.ToArray();
        int[] windows = [0, 0, 0, 0, 0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0xFF0, 0x450];
        for (var id = 5; id <= 13; id++)
        {
            var sector = LiveSector(data, id);
            var start = (id - 5) * 0xFF0;
            var from = Math.Max(4, start) - start;
            var to = Math.Min(4 + 14 * 30 * 80, start + 0xFF0) - start;
            if (to > from) data.AsSpan(sector + from, to - from).Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(sector + 0xFF6), Sum(data.AsSpan(sector), windows[id]));
        }
        return data;
    }

    /// <summary>Every PC record zeroed (14 boxes × 30 × 80 bytes over 0xF80-byte chunks; box
    /// names and wallpapers kept). Exact for the 0xF80-chunk family; a stress input elsewhere.</summary>
    private static byte[] NoBoxMons(byte[] payload)
    {
        var data = payload;
        for (var id = 5; id <= 13; id++)
        {
            var sector = LiveSector(data, id);
            if (sector < 0) return payload;
            var start = (id - 5) * 0xF80;
            var from = Math.Max(4, start) - start;
            var to = Math.Min(4 + 14 * 30 * 80, start + 0xF80) - start;
            if (to > from)
                data = Mutate(data, sector, from, new byte[to - from]);
        }
        return data;
    }
}
