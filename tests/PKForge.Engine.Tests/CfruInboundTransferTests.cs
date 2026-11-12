using PKForge.Domain;
using PKForge.Engine.RadicalRed;
using PKForge.Engine.Unbound;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Pokémon from other games entering Radical Red and Unbound (GitHub #78). The import used
/// to downgrade everything to a PK3 first, which PKHeX never does from Generation 4 on, so
/// every later Pokémon was refused; a PK3 was refused too when the hack's table put its
/// ability in the other slot, or gave its species other abilities. A CFRU record reads
/// nature, gender, shininess and ability slot from the PID, so those must survive the trip.
/// </summary>
public sealed class CfruInboundTransferTests
{
    private const int Lucario = 448, Garchomp = 445, Gengar = 94;

    private static ISaveEngineSession RadicalRed() =>
        CfruEngineSession.Blank(RadicalRedEngineSession.Profile, CfruEntity.BlankImage(CfruEntity.RetailSignature), null);

    private static ISaveEngineSession Unbound() =>
        new UnboundEngineSession(CfruEntity.BlankImage(SaveParser.UnboundSectorSignature));

    /// <summary>What the hack reads from the landed record, every id in national numbering.</summary>
    private static (int Species, int Nature, int Gender, bool Shiny, int Ability, int[] Moves) Landed(ISaveEngineSession session, int box, int slot)
    {
        var export = session.ExportSlot(box, slot);
        if (export.Format == CfruEntity.Unbound)
        {
            var mon = new UnboundMon(export.Data, 0, party: false);
            return (UnboundData.NationalIdOf(mon.Species), mon.Nature, UnboundData.GenderOf(mon.Pid, mon.Species), mon.IsShiny,
                UnboundData.AbilityToNational(UnboundData.ActiveAbility(mon)), [.. mon.Moves.Select(UnboundData.MoveToNational)]);
        }
        var data = RadicalRedGameData.Instance;
        var cfru = new RadicalRedMon(export.Data, 0, false, data);
        return (data.NationalIdOf(cfru.Species), cfru.Nature, data.GenderOf(cfru.Pid, cfru.Species), cfru.IsShiny,
            data.ActiveAbility(cfru), [.. cfru.Moves.Select(data.MoveToNational)]);
    }

    /// <summary>The national ids of the hack's two ability slots and hidden slot for a species.</summary>
    private static (int A1, int A2, int Hidden) HackAbilities(bool unbound, int national)
    {
        if (!unbound)
            return RadicalRedGameData.Instance.AbilityIds(RadicalRedGameData.Instance.SpeciesFromNational(national));
        var (a1, a2, hidden) = UnboundData.AbilityIds(UnboundData.SpeciesFromNational(national));
        return (UnboundData.AbilityToNational(a1), UnboundData.AbilityToNational(a2), UnboundData.AbilityToNational(hidden));
    }

    private static T Named<T>(T pk) where T : PKM
    {
        pk.Language = (int)LanguageID.English;
        pk.Nickname = SpeciesName.GetSpeciesName(pk.Species, pk.Language);
        pk.OriginalTrainerName = "Sof";
        pk.TID16 = 12345;
        pk.SID16 = 54321;
        pk.EXP = Experience.GetEXP(50, pk.PersonalInfo.EXPGrowth);
        pk.Ball = (byte)Ball.Poke;
        return pk;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGeneration8PokemonLandsWithItsNatureGenderShininessAndAbility(bool unbound)
    {
        var (a1, a2, _) = HackAbilities(unbound, Lucario);
        // An even PID picks slot one in the hack; ask for slot two so the PID must be solved again.
        var ability = a2 != 0 ? a2 : a1;
        var pk = Named(new PK8 { Species = Lucario });
        pk.PID = 0x1234_5670; // PID % 25 is Serious, not the Adamant it shows
        pk.Nature = Nature.Adamant;
        pk.StatAlignment = Nature.Adamant;
        pk.Gender = 0;
        pk.Ability = ability;
        pk.SetMoves([(ushort)Move.AuraSphere, (ushort)Move.CloseCombat, 0, 0]);
        pk.RefreshChecksum();

        using var session = unbound ? Unbound() : RadicalRed();
        Assert.True(session.ImportSlot(0, 0, pk.Data.ToArray(), "PK8"));

        var landed = Landed(session, 0, 0);
        Assert.Equal(Lucario, landed.Species);
        Assert.Equal((int)Nature.Adamant, landed.Nature);
        Assert.Equal(0, landed.Gender);
        Assert.Equal(pk.IsShiny, landed.Shiny);
        Assert.Equal(ability, landed.Ability);
        Assert.Equal([(int)Move.AuraSphere, (int)Move.CloseCombat, 0, 0], landed.Moves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGeneration4PokemonLandsAndAShinyStaysShiny(bool unbound)
    {
        var pk = Named(new PK4 { Species = Garchomp });
        // A Generation 4 shiny PID: (TID ^ SID ^ high ^ low) below 8.
        pk.PID = (uint)(12345 ^ 54321 ^ 0x0042) << 16 | 0x0042;
        pk.Gender = EntityGender.GetFromPID(Garchomp, pk.PID);
        pk.Ability = pk.PersonalInfo.GetAbilityAtIndex((int)(pk.PID & 1));
        pk.SetMoves([(ushort)Move.DragonClaw, (ushort)Move.Earthquake, 0, 0]);
        pk.RefreshChecksum();
        Assert.True(pk.IsShiny);
        var (a1, a2, hidden) = HackAbilities(unbound, Garchomp);
        Assert.Contains(pk.Ability, new[] { a1, a2, hidden }); // the hack gives Garchomp this ability

        using var session = unbound ? Unbound() : RadicalRed();
        Assert.Null(CfruEntity.ImportRefusal(pk, session.Snapshot.Format));
        Assert.True(session.ImportSlot(1, 3, pk.Data.ToArray(), "PK4"));

        var landed = Landed(session, 1, 3);
        Assert.Equal(Garchomp, landed.Species);
        Assert.Equal((int)pk.Nature, landed.Nature);
        Assert.Equal(pk.Gender, landed.Gender);
        Assert.True(landed.Shiny);
        Assert.Equal(pk.Ability, landed.Ability);
    }

    [Fact]
    public void AFireRedAbilityTheHackChangedTakesTheHacksAbilityAndSaysSo()
    {
        // Radical Red uses the modern abilities: Gengar's Generation 3 Levitate is now Cursed Body.
        var pk = Named(new PK3 { Species = Gengar });
        pk.PID = 0x0BAD_F00D;
        pk.SetMoves([(ushort)Move.ShadowBall, 0, 0, 0]);
        pk.RefreshChecksum();
        Assert.Equal((int)Ability.Levitate, pk.Ability);

        using var session = RadicalRed();
        var tag = session.Snapshot.Format;
        Assert.Null(CfruEntity.ImportRefusal(pk, tag));
        Assert.Contains(CfruEntity.ImportAdjustments(pk, tag), line => line.Contains("Levitate") && line.Contains("Cursed Body"));
        Assert.True(session.ImportSlot(0, 0, pk.Data.ToArray(), "PK3"));

        var landed = Landed(session, 0, 0);
        Assert.Equal(Gengar, landed.Species);
        Assert.Equal((int)Ability.CursedBody, landed.Ability);
        Assert.Equal((int)(pk.PID % 25), landed.Nature);
    }

    private static byte[]? Local(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", file);
        return path is not null && File.Exists(path) ? File.ReadAllBytes(path) : null; // gitignored: dev-only
    }

    [Theory]
    [InlineData("thor/retroarch/mGBA/Pokémon Unbound (v2.1.1.1).srm")]
    [InlineData("thor/retroarch/mGBA/Radical-red-champ.sav")]
    public void AGeneration8PokemonSurvivesARealSavesWriteAndReopen(string file)
    {
        if (Local(file) is not { } bytes) return;
        var engine = new SaveEngine();
        var pk = Named(new PK8 { Species = Lucario });
        pk.PID = 0x1234_5670;
        pk.Nature = Nature.Adamant;
        pk.StatAlignment = Nature.Adamant;
        pk.Gender = 0;
        pk.Ability = pk.PersonalInfo.GetAbilityAtIndex(0);
        pk.SetMoves([(ushort)Move.AuraSphere, 0, 0, 0]);
        pk.RefreshChecksum();

        byte[] written;
        (int Box, int Slot) free;
        using (var session = engine.OpenSession(bytes, file))
        {
            var slot = session.Snapshot.Slots.First(s => s.Box >= 0 && s.Species is null);
            free = (slot.Box, slot.Slot);
            Assert.True(session.ImportSlot(free.Box, free.Slot, pk.Data.ToArray(), "PK8"));
            written = session.Serialize().ToArray();
        }
        using var reopened = engine.OpenSession(written, file);
        var landed = Landed(reopened, free.Box, free.Slot);
        Assert.Equal(Lucario, landed.Species);
        Assert.Equal((int)Nature.Adamant, landed.Nature);
        Assert.Equal((int)Move.AuraSphere, landed.Moves[0]);
    }

    [Fact]
    public void AFormTheHackCannotStoreIsRefusedNotChanged()
    {
        var pk = Named(new PK8 { Species = (ushort)Species.Raichu, Form = 1 }); // Alolan Raichu
        pk.Ability = pk.PersonalInfo.GetAbilityAtIndex(0);
        pk.SetMoves([(ushort)Move.Thunderbolt, 0, 0, 0]);
        pk.RefreshChecksum();

        using var session = RadicalRed();
        Assert.Contains("form", CfruEntity.ImportRefusal(pk, session.Snapshot.Format));
        Assert.False(session.ImportSlot(0, 0, pk.Data.ToArray(), "PK8"));
    }
}
