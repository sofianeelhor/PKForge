using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// "I change the ability, press Save and it reverts" and "I can't change moves on FireRed
/// and Unbound": Gen 3 and its hacks store an ability SLOT and a move table of their own,
/// so a pick outside them was dropped on save. The pickers now offer only what the game
/// can store (<see cref="ISaveEngineSession.StoresMove"/>, <see cref="ISaveEngineSession.StoresAnyAbility"/>),
/// and what they offer sticks, in a save and in a Bank entry alike.
/// </summary>
public sealed class Gen3EditPersistenceTests
{
    private const int Jynx = 124, Oblivious = 12, Insomnia = 15;
    private const int Psychic = 94, PsychoBoost = 354, NightSlash = 400, Tackle = 33;

    private static byte[] Pk3Jynx()
    {
        var pk = new PK3
        {
            Species = Jynx,
            PID = 0x1234_5678,
            ID32 = (62891u << 16) | 43546,
            OriginalTrainerName = "AA",
            Language = (int)LanguageID.English,
            Version = GameVersion.FR,
            MetLevel = 20,
            Move1 = 1,
            Ball = 4,
        };
        pk.CurrentLevel = 30;
        pk.Nickname = "JYNX";
        pk.RefreshChecksum();
        return pk.Data.ToArray();
    }

    private static string? Thor(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null
            : Path.Combine(directory.FullName, ".local-testdata", "thor", "retroarch", "mGBA", file);
        return path is not null && File.Exists(path) ? path : null;
    }

    [Fact]
    public void Pk3BankEntryNeverOffersAnAbilityItCannotHold()
    {
        using var session = new SaveEngine().OpenEntitySession(Pk3Jynx(), "Jynx", "PK3")!;
        Assert.False(session.StoresAnyAbility);
        var choices = session.GetAbilityChoices(Jynx, 0);
        Assert.Contains(Oblivious, choices);
        Assert.DoesNotContain(Insomnia, choices);

        // Asked anyway (an older picker did), it stays put rather than writing garbage.
        session.ApplyEdit(0, 0, new EntityEdit(Ability: Insomnia));
        Assert.Equal(Oblivious, session.ReadEntity(0, 0).Ability);
    }

    [Fact]
    public void Pk3BankEntryMoveEditsSurviveTheBankWrite()
    {
        var engine = new SaveEngine();
        using var session = engine.OpenEntitySession(Pk3Jynx(), "Jynx", "PK3")!;
        Assert.True(session.StoresMove(PsychoBoost));
        Assert.False(session.StoresMove(PsychoBoost + 1));

        session.ApplyEdit(0, 0, new EntityEdit(Move1: Psychic, Move2: Tackle, Nickname: "ICE"));
        // A Generation 4 move cannot exist on a FireRed Pokémon: never written.
        session.ApplyEdit(0, 0, new EntityEdit(Move3: NightSlash));

        var export = session.ExportSlot(0, 0);
        using var reopened = engine.OpenEntitySession(export.Data, "Jynx", export.Format)!;
        var after = reopened.ReadEntity(0, 0);
        Assert.Equal(Psychic, after.Move1);
        Assert.Equal(Tackle, after.Move2);
        Assert.Equal(0, after.Move3);
        Assert.Equal("ICE", after.Nickname);
        Assert.True(reopened.GetMoveDetails(0, 0).Moves[0].PP > 0);
    }

    [Fact]
    public void FireRedSaveMoveAndAbilityEditsSurviveReopen()
    {
        if (Thor("Pokemon Fire Red.sav") is not { } path) return;
        var engine = new SaveEngine();
        using var session = engine.OpenSession(File.ReadAllBytes(path), "FireRed");
        var slot = session.Snapshot.Slots.First(s => s.Species is not null);
        var before = session.ReadEntity(slot.Box, slot.Slot);
        var ability = session.GetAbilityChoices(before.Species, before.Form).Last();

        session.ApplyEdit(slot.Box, slot.Slot, new EntityEdit(Move1: Psychic, Move2: NightSlash, Ability: ability));

        using var reopened = engine.OpenSession(session.Serialize().ToArray(), "FireRed");
        var after = reopened.ReadEntity(slot.Box, slot.Slot);
        Assert.Equal(Psychic, after.Move1);
        Assert.Equal(before.Move2, after.Move2);
        Assert.Equal(ability, after.Ability);
        Assert.Equal(before.Nature, after.Nature);
        Assert.Equal(before.IsShiny, after.IsShiny);
    }

    [Fact]
    public void UnboundSaveAndBankEntryKeepOfferedMoves()
    {
        if (Thor("Pokémon Unbound (v2.1.1.1).srm") is not { } path) return;
        var engine = new SaveEngine();
        using var session = engine.OpenSession(File.ReadAllBytes(path), "Unbound");
        var slot = session.Snapshot.Slots.First(s => s.Species is not null);

        // Every move the plain picker now lists is one Unbound's table holds, and it sticks.
        var offered = Enumerable.Range(1, 950).Where(session.StoresMove).ToList();
        Assert.True(offered.Count > 354);
        Assert.Contains(Enumerable.Range(1, 950), m => !session.StoresMove(m)); // the table has holes
        var sample = offered.Where((_, i) => i % 37 == 0).ToList();
        foreach (var move in sample)
        {
            session.ApplyEdit(slot.Box, slot.Slot, new EntityEdit(Move4: move));
            Assert.Equal(move, session.ReadEntity(slot.Box, slot.Slot).Move4);
        }

        var choices = session.GetAbilityChoices(session.ReadEntity(slot.Box, slot.Slot).Species, 0);
        foreach (var ability in choices.Where(a => a != 0))
        {
            session.ApplyEdit(slot.Box, slot.Slot, new EntityEdit(Ability: ability));
            Assert.Equal(ability, session.ReadEntity(slot.Box, slot.Slot).Ability);
        }

        // The Bank entry path: the CFRU record reopens in its own session and keeps the edit.
        var export = session.ExportSlot(slot.Box, slot.Slot);
        using var bank = engine.OpenEntitySession(export.Data, "x", export.Format)!;
        bank.ApplyEdit(0, 0, new EntityEdit(Move1: Psychic, Move2: Tackle));
        var stored = bank.ExportSlot(0, 0);
        using var reopened = engine.OpenEntitySession(stored.Data, "x", stored.Format)!;
        Assert.Equal(Psychic, reopened.ReadEntity(0, 0).Move1);
        Assert.Equal(Tackle, reopened.ReadEntity(0, 0).Move2);
    }
}
