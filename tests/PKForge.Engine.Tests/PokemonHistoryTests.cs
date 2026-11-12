using PKForge.Domain;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Pokémon History: a Pokémon that goes back to an older game and returns gets the data it had
/// back (hidden ability, PID, met location, ribbons) with its progress over there kept, and with
/// history off every transfer is exactly what it always was.
/// </summary>
public sealed class PokemonHistoryTests
{
    private static SaveEngineSession Game(GameVersion version, IPokemonHistoryStore? store)
    {
        var session = new SaveEngineSession(BlankSaveFile.Get(version, "Sof", LanguageID.English), null);
        if (store is not null) session.HistorySource = () => store;
        return session;
    }

    private static int Location5(string name)
    {
        for (var id = 1; id < 200; id++)
            if (GameInfo.GetLocationName(false, (ushort)id, 5, 5, GameVersion.W) == name) return id;
        throw new InvalidOperationException(name);
    }

    /// <summary>A White Pokémon with its Hidden Ability, met in Pinwheel Forest, with a Sinnoh ribbon.</summary>
    private static PK5 White(Species species = Species.Breloom, uint pid = 0x2468_ACE1, int[]? ivs = null)
    {
        var pk = new PK5
        {
            Species = (ushort)species,
            CurrentLevel = 40,
            Version = GameVersion.W,
            Language = (int)LanguageID.English,
            OriginalTrainerName = "Sof",
            TID16 = 24680,
            SID16 = 13579,
            Ball = (byte)Ball.Dusk,
            MetLocation = (ushort)Location5("Pinwheel Forest"),
            MetLevel = 20,
            PID = pid,
            Nature = (Nature)(pid % 25), // the Gen 3 reading of the PID, so a downgrade has no reason to roll a new one
            OriginalTrainerFriendship = 120,
            RibbonAbility = true,
        };
        pk.Gender = EntityGender.GetFromPID((ushort)species, pid);
        pk.ClearNickname();
        pk.SetMoves([(ushort)Move.MachPunch, (ushort)Move.Spore, (ushort)Move.SeedBomb, (ushort)Move.ForcePalm]);
        pk.RefreshAbility(2); // Technician (Quick Feet for Shroomish): hidden, unknown to Gen 3
        pk.SetIVs(ivs ?? [31, 30, 29, 28, 27, 26]);
        pk.ResetPartyStats();
        pk.HealPP();
        pk.RefreshChecksum();
        return pk;
    }

    private static byte[] Bytes(PKM pk) => pk.Data.ToArray();

    /// <summary>Sends <paramref name="mon"/> into a fresh game; the history remembers it as the save is written.</summary>
    private static (PKM Landed, TransferConversion Conversion) Send(PKM mon, GameVersion to, IPokemonHistoryStore? store)
    {
        var game = Game(to, store);
        var conversion = game.ImportSlotWithReport(0, 0, Bytes(mon), out var refusal, EntityBytes.FormatOf(mon));
        Assert.True(conversion is not null, refusal);
        game.CommitHistory();
        return (game.GetEntity(0, 0), conversion!);
    }

    /// <summary>Play in Emerald: levels, a new move, EVs.</summary>
    private static PK3 Play(PKM landed, int level = 54)
    {
        var pk = Assert.IsType<PK3>(landed.Clone());
        pk.CurrentLevel = (byte)level;
        pk.Move4 = (ushort)Move.SkyUppercut;
        pk.EV_ATK = 100;
        pk.ResetPartyStats();
        pk.HealPP();
        pk.RefreshChecksum();
        return pk;
    }

    [Fact]
    public void BreloomComesBackToWhiteWithItsHiddenAbilityAndKeepsWhatItDidInEmerald()
    {
        var store = new InMemoryPokemonHistoryStore();
        var white = White();

        var (inEmerald, down) = Send(white, GameVersion.E, store);
        Assert.True(down.Backwards);
        Assert.NotEqual((int)Ability.Technician, inEmerald.Ability);

        var played = Play(inEmerald);
        var (back, up) = Send(played, GameVersion.W, store);
        var pk5 = Assert.IsType<PK5>(back);

        Assert.Equal((int)Ability.Technician, pk5.Ability);
        Assert.True(pk5.HiddenAbility);
        Assert.Equal(white.PID, pk5.PID);
        Assert.Equal(white.MetLocation, pk5.MetLocation);
        Assert.Equal(white.Version, pk5.Version);
        Assert.Equal(white.Ball, pk5.Ball);
        Assert.True(pk5.RibbonAbility);
        Assert.Equal(54, pk5.CurrentLevel);
        Assert.Equal((ushort)Move.SkyUppercut, pk5.Move4);
        Assert.Equal(100, pk5.EV_ATK);

        Assert.Contains(up.Warnings, w => w.StartsWith("Restored from your White data", StringComparison.Ordinal)
            && w.Contains("Hidden Ability Technician") && w.Contains("met in Pinwheel Forest"));
        Assert.Contains(up.Warnings, w => w.StartsWith("Kept from Emerald", StringComparison.Ordinal)
            && w.Contains("level 54") && w.Contains("new moves") && w.Contains("EVs"));
        Assert.DoesNotContain(up.Warnings, w => w.Contains("Backwards transfer"));

        var record = Assert.Single(store.All());
        Assert.Equal(2, record.Trips.Count);
        Assert.True(record.Trips[1].Restored);
        Assert.Equal(["White", "Emerald"], record.GamesVisited());
    }

    [Fact]
    public void WithoutPlayTheReturnIsTheWhiteCopyItself()
    {
        var store = new InMemoryPokemonHistoryStore();
        var white = White();
        var (inEmerald, _) = Send(white, GameVersion.E, store);
        var (back, up) = Send(inEmerald, GameVersion.W, store);

        Assert.Equal(white.Data[..white.SIZE_STORED].ToArray(), back.Data[..white.SIZE_STORED].ToArray());
        Assert.DoesNotContain(up.Warnings, w => w.StartsWith("Kept from", StringComparison.Ordinal));
    }

    [Fact]
    public void EvolutionInEmeraldIsKeptAndTheHiddenAbilityFollowsIt()
    {
        var store = new InMemoryPokemonHistoryStore();
        var shroomish = White(Species.Shroomish);
        Assert.Equal((int)Ability.QuickFeet, shroomish.Ability);
        var (inEmerald, _) = Send(shroomish, GameVersion.E, store);

        var evolved = Assert.IsType<PK3>(inEmerald.Clone());
        evolved.Species = (ushort)Species.Breloom;
        evolved.CurrentLevel = 23;
        evolved.ClearNickname();
        evolved.RefreshChecksum();

        var (back, up) = Send(evolved, GameVersion.W, store);
        Assert.Equal((ushort)Species.Breloom, back.Species);
        Assert.Equal((int)Ability.Technician, back.Ability);
        Assert.Equal("BRELOOM", back.Nickname.ToUpperInvariant());
        Assert.Equal(shroomish.PID, back.PID);
        Assert.Contains(up.Warnings, w => w.Contains("evolved into Breloom"));
    }

    [Fact]
    public void ALegalPokemonStaysLegalThroughTheRoundTrip()
    {
        var corpus = Corpus("Legal/Generation 5/015 - Beedrill - 2F3A638FD1B9 gen3 transfer highlevel.pk5");
        var original = Assert.IsType<PK5>(EntityBytes.Parse(File.ReadAllBytes(corpus), "PK5"));
        Assert.True(new LegalityAnalysis(original).Valid);

        var store = new InMemoryPokemonHistoryStore();
        var (inEmerald, _) = Send(original, GameVersion.E, store);
        var played = Assert.IsType<PK3>(inEmerald.Clone());
        played.CurrentLevel = (byte)Math.Min(100, original.CurrentLevel + 3);
        played.RefreshChecksum();
        var (back, up) = Send(played, GameVersion.W, store);

        Assert.Equal(original.PID, back.PID);
        Assert.Equal(original.CurrentLevel + 3 > 100 ? 100 : original.CurrentLevel + 3, back.CurrentLevel);
        Assert.True(new LegalityAnalysis(back).Valid, string.Join("\n", up.Warnings));
        Assert.Contains(up.Warnings, w => w == "Legality re-checked: legal.");
    }

    [Fact]
    public void HistoryOffIsTodaysTransferExactly()
    {
        var white = White();
        var (plainDown, plainConversion) = Send(white, GameVersion.E, store: null);
        var offGame = Game(GameVersion.E, null);
        offGame.HistorySource = () => null;
        var offConversion = offGame.ImportSlotWithReport(0, 0, Bytes(white), out _, "PK5");
        Assert.NotNull(offConversion);
        // The downgrade may roll a new PID (randomly, today too): everything else is identical.
        var offDown = offGame.GetEntity(0, 0);
        offDown.PID = plainDown.PID;
        offDown.RefreshChecksum();
        Assert.Equal(Bytes(plainDown), Bytes(offDown));
        Assert.Equal(0, offGame.PendingHistoryCount);
        Assert.Equal(plainConversion.Warnings, offConversion.Warnings);

        // A history that holds the Pokémon is not used while the setting is off.
        var store = new InMemoryPokemonHistoryStore();
        var (down, _) = Send(white, GameVersion.E, store);
        var plainBack = Send(down, GameVersion.W, store: null).Landed;
        var off = Game(GameVersion.W, null);
        off.HistorySource = () => null;
        off.ImportSlotWithReport(0, 0, Bytes(down), out _, "PK3");
        Assert.Equal(Bytes(plainBack), Bytes(off.GetEntity(0, 0)));
        Assert.Equal(0, off.PendingHistoryCount);
        Assert.NotEqual((int)Ability.Technician, plainBack.Ability);
    }

    [Fact]
    public void APreviewNeverWritesHistory()
    {
        var store = new InMemoryPokemonHistoryStore();
        var game = Game(GameVersion.E, store);
        var preview = new TransferPreviewService().Preview(game, 0, 0, Bytes(White()), "PK5");
        Assert.NotNull(preview);
        Assert.Empty(store.All());
    }

    [Fact]
    public void ThePreviewSaysWhatCameBack()
    {
        var store = new InMemoryPokemonHistoryStore();
        var (inEmerald, _) = Send(White(), GameVersion.E, store);
        var preview = new TransferPreviewService().Preview(Game(GameVersion.W, store), 0, 0, Bytes(Play(inEmerald)), "PK3");
        Assert.NotNull(preview);
        Assert.Contains(preview.Warnings, w => w.Contains("Restored from your White data"));
        Assert.Contains(preview.Warnings, w => w.Contains("Kept from Emerald"));
    }

    [Fact]
    public void ClonesShareOneRecordAndEachKeepsItsOwnProgress()
    {
        var store = new InMemoryPokemonHistoryStore();
        var white = White();
        var (first, _) = Send(white, GameVersion.E, store);
        var (second, _) = Send(white, GameVersion.E, store);
        Assert.Single(store.All());

        var a = Send(Play(first, 50), GameVersion.W, store).Landed;
        var b = Send(Play(second, 60), GameVersion.W, store).Landed;
        Assert.Equal(50, a.CurrentLevel);
        Assert.Equal(60, b.CurrentLevel);
        Assert.Equal((int)Ability.Technician, a.Ability);
        Assert.Equal((int)Ability.Technician, b.Ability);
        Assert.Single(store.All());
    }

    [Fact]
    public void ADifferentPokemonWithTheSameIdentityIsNotGivenTheOldData()
    {
        var store = new InMemoryPokemonHistoryStore();
        Send(White(), GameVersion.E, store);

        // Same PID, trainer and ID, other IVs: a stranger as far as history is concerned.
        var stranger = White(ivs: [1, 2, 3, 4, 5, 6]);
        var (inEmerald, _) = Send(stranger, GameVersion.E, new InMemoryPokemonHistoryStore());
        var (back, up) = Send(inEmerald, GameVersion.W, store);
        Assert.NotEqual((int)Ability.Technician, back.Ability);
        Assert.DoesNotContain(up.Warnings, w => w.Contains("Restored from"));
    }

    [Fact]
    public void PalParkBringsBackThePlatinumCopy()
    {
        var store = new InMemoryPokemonHistoryStore();
        var platinum = new PK4
        {
            Species = (ushort)Species.Shroomish,
            CurrentLevel = 15,
            Version = GameVersion.Pt,
            Language = (int)LanguageID.English,
            OriginalTrainerName = "Sof",
            TID16 = 111,
            SID16 = 222,
            Ball = (byte)Ball.Poke,
            MetLocation = 50,
            MetLevel = 10,
            PID = 0x0000_0BAD,
            RibbonAbility = true,
        };
        platinum.Gender = EntityGender.GetFromPID(platinum.Species, platinum.PID);
        platinum.ClearNickname();
        platinum.SetMoves([(ushort)Move.Absorb, (ushort)Move.Tackle]);
        platinum.RefreshAbility(0);
        platinum.SetIVs([10, 11, 12, 13, 14, 15]);
        platinum.ResetPartyStats();
        platinum.RefreshChecksum();

        var (inEmerald, _) = Send(platinum, GameVersion.E, store);
        var played = Play(inEmerald, 30);

        var park = Game(GameVersion.Pt, store);
        var preview = PalParkService.Put(park, 0, PalParkSource.FromBytes(played.Data.ToArray(), "PK3"), allowNonGba: false);
        Assert.True(preview.Allowed, preview.Refusal);
        Assert.Contains(preview.Warnings, w => w.StartsWith("Restored from your Platinum data", StringComparison.Ordinal));
        park.CommitHistory();
        var migrated = PalParkService.ReadSlot((SAV4)park.SaveFile, 0);
        Assert.True(migrated.RibbonAbility);
        Assert.Equal(platinum.MetLocation, migrated.MetLocation);
        Assert.Equal(GameVersion.Pt, migrated.Version);
        Assert.Equal(30, migrated.CurrentLevel);
        Assert.Equal(2, Assert.Single(store.All()).Trips.Count);
    }

    [Fact]
    public void GameBoyPokemonAreLeftOut()
    {
        var pk = new PK2 { Species = (ushort)Species.Pikachu, CurrentLevel = 10, OriginalTrainerName = "SOF", TID16 = 1 };
        Assert.False(PokemonHistoryService.Covers(pk));
    }

    private static string Corpus(string relative)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "Legality", relative);
    }
}
