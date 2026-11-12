using PKForge.Domain;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Issue #72: "the hidden ability changed when it evolved". The games re-read the ability
/// from the stored slot (1, 2, or 4 for hidden) on evolution, so choosing an ability in the
/// editor must move the slot with it, not only the ability id.
/// </summary>
public sealed class AbilitySlotSyncTests
{
    private const ushort Eevee = 133;
    private const int Anticipation = 107; // Eevee's hidden ability
    private const int Adaptability = 91;  // Eevee's second regular ability
    private const int RunAway = 50;       // Eevee's first regular ability

    private static PKM Eevee5To9(PKM mon, GameVersion version)
    {
        mon.Species = Eevee;
        mon.Version = version;
        mon.CurrentLevel = 20;
        mon.OriginalTrainerName = "Ash";
        mon.TID16 = 4242;
        mon.SID16 = 1717;
        mon.Language = (int)LanguageID.English;
        mon.Nickname = "EEVEE";
        mon.Move1 = 33; // Tackle
        mon.PID = 0x1234_0000; // bit 16 clear: Gen 5 regular slot 1
        mon.EncryptionConstant = 0x89AB_CDEF;
        mon.RefreshAbility(0);
        mon.RefreshChecksum();
        return mon;
    }

    public static TheoryData<PKM> Mons => new()
    {
        Eevee5To9(new PK5(), GameVersion.W2),
        Eevee5To9(new PK6(), GameVersion.X),
        Eevee5To9(new PK7(), GameVersion.SN),
        Eevee5To9(new PK8(), GameVersion.SW),
        Eevee5To9(new PK9(), GameVersion.SL),
        Eevee5To9(new PB8(), GameVersion.BD),
        Eevee5To9(new PA8(), GameVersion.PLA),
    };

    private static PKM EditAndExport(PKM mon, int ability, bool expectRunAway = true)
    {
        var engine = new SaveEngine();
        var bytes = new byte[mon.SIZE_PARTY];
        mon.WriteDecryptedDataParty(bytes);
        using var session = engine.OpenEntitySession(bytes, "ability");
        Assert.NotNull(session);
        if (expectRunAway) Assert.Equal(RunAway, session!.ReadEntity(0, 0).Ability);
        session!.ApplyEdit(0, 0, new EntityEdit(Ability: ability));
        var exported = session.ExportSlot(0, 0);
        var back = EntityFormat.GetFromBytes(exported.Data.ToArray(), mon.Context);
        Assert.NotNull(back);
        return back!;
    }

    [Theory]
    [MemberData(nameof(Mons))]
    public void ChoosingTheHiddenAbilityStoresTheHiddenSlot(PKM mon)
    {
        var edited = EditAndExport(mon, Anticipation);

        Assert.Equal(Anticipation, edited.Ability);
        Assert.Equal(4, edited.AbilityNumber);
        if (edited is PK5 pk5) Assert.True(pk5.HiddenAbility);
    }

    [Theory]
    [MemberData(nameof(Mons))]
    public void ChoosingTheSecondAbilityStoresTheSecondSlot(PKM mon)
    {
        var edited = EditAndExport(mon, Adaptability);

        Assert.Equal(Adaptability, edited.Ability);
        Assert.Equal(2, edited.AbilityNumber);
    }

    [Theory]
    [MemberData(nameof(Mons))]
    public void TheHiddenAbilitySurvivesEvolution(PKM mon)
    {
        var edited = EditAndExport(mon, Anticipation);

        // What a game does on evolution: the new species reads its ability from the slot.
        edited.Species = 135; // Jolteon
        var slot = edited.AbilityNumber switch { 4 => 2, 2 => 1, _ => 0 };
        edited.RefreshAbility(slot);

        Assert.Equal(95, edited.Ability); // Quick Feet, Jolteon's hidden ability (not Volt Absorb)
    }

    /// <summary>A mon edited before the fix (or by another tool) already carries the hidden
    /// ability id with the regular slot. Choosing the hidden ability again must repair the
    /// slot, not be skipped because the id already matches.</summary>
    [Theory]
    [MemberData(nameof(Mons))]
    public void ReChoosingTheHiddenAbilityRepairsAStaleSlot(PKM mon)
    {
        mon.Ability = Anticipation; // id says hidden, slot still says 1
        mon.RefreshChecksum();
        Assert.Equal(1, mon.AbilityNumber);

        var edited = EditAndExport(mon, Anticipation, expectRunAway: false);

        Assert.Equal(Anticipation, edited.Ability);
        Assert.Equal(4, edited.AbilityNumber);
    }

    [Fact]
    public void Gen4KeepsItsPidDerivedSlot()
    {
        var mon = new PK4
        {
            Species = Eevee, Version = GameVersion.HG, CurrentLevel = 20, OriginalTrainerName = "Ash",
            TID16 = 4242, SID16 = 1717, Language = (int)LanguageID.English, Nickname = "EEVEE",
            Move1 = 33, PID = 0x1234_5678,
        };
        mon.RefreshAbility(0);
        mon.RefreshChecksum();

        var edited = EditAndExport(mon, Adaptability);

        Assert.Equal(Adaptability, edited.Ability);
        Assert.Equal(2, edited.AbilityNumber);
        Assert.Equal(1u, edited.PID & 1);
    }
}
