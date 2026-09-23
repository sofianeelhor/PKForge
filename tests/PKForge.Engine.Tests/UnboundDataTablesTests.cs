using System.Buffers.Binary;
using PKForge.Domain;
using PKForge.Engine.Unbound;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Unbound's ROM ids against published ground truth, and the bridge to the PKHeX ids
/// the UI speaks. Expected values come from CFRU's include/constants/{moves,abilities,
/// items}.h (github.com/Skeli789/Complete-Fire-Red-Upgrade, UNBOUND defines) and
/// Skeli789/Unbound-Cloud's unbound_2_1 tables; see Unbound/Data/README.md.
/// </summary>
public sealed class UnboundDataTablesTests
{
    private static readonly GameStrings Strings = GameInfo.GetStrings("en");

    [Theory]
    [InlineData(1, "Pound", "Pound")]
    [InlineData(136, "High Jump Kick", "High Jump Kick")]     // ROM text is "Hi Jump Kick"
    [InlineData(354, "Psycho Boost", "Psycho Boost")]         // last id shared with retail Gen 3
    [InlineData(355, "Leech Fang", null)]                     // CFRU MOVE_LEECHFANG 0x163 (national 355 is Roost)
    [InlineData(365, "Close Combat", "Close Combat")]         // national 370
    [InlineData(369, "Draco Meteor", "Draco Meteor")]         // national 434
    [InlineData(572, "Thousand Arrows", "Thousand Arrows")]   // ROM text is "1000 Arrows"
    [InlineData(490, "Petal Storm", "Petal Blizzard")]        // CFRU MOVE_PETALBLIZZARD, Unbound's label
    [InlineData(635, "Crafty Guard", "Crafty Shield")]        // CFRU MOVE_CRAFTYSHIELD
    [InlineData(499, "Metal Bash", null)]                     // CFRU MOVE_STEELYHIT, no PKHeX move
    [InlineData(760, "Ceaseless Edge", "Ceaseless Edge")]
    [InlineData(765, "Lunar Blessing", "Lunar Blessing")]
    public void MovesDecodeAndBridge(int rom, string name, string? pkhexName)
    {
        Assert.Equal(name, UnboundData.MoveName(rom));
        var national = UnboundData.MoveToNational(rom);
        if (pkhexName is null)
        {
            Assert.Equal(0, national);
            return;
        }
        Assert.Equal(pkhexName, Strings.movelist[national]);
        Assert.Equal(rom, UnboundData.MoveFromNational(national));
    }

    [Theory]
    [InlineData(22, "Intimidate", "Intimidate")]
    [InlineData(72, "Transistor", "Transistor")]              // CFRU: "Was ABILITY_VITALSPIRIT"
    [InlineData(74, "Neutralizing Gas", "Neutralizing Gas")]
    [InlineData(86, "Regenerator", "Regenerator")]
    [InlineData(164, "Wandering Spirit", "Wandering Spirit")]
    [InlineData(239, "Dauntless Shield", "Dauntless Shield")]
    public void AbilitiesDecodeAndBridge(int rom, string name, string pkhexName)
    {
        Assert.Equal(name, UnboundData.AbilityName(rom));
        var national = UnboundData.AbilityToNational(rom);
        Assert.Equal(pkhexName, Strings.abilitylist[national]);
        Assert.Equal(rom, UnboundData.AbilityFromNational(national));
    }

    [Fact]
    public void AsOneSplitsByOwner()
    {
        Assert.Equal((int)Ability.AsOneG, UnboundData.AbilityToNational(153)); // ABILITY_ASONE_GRIM (Spectrier)
        Assert.Equal((int)Ability.AsOneI, UnboundData.AbilityToNational(154)); // ABILITY_ASONE_CHILLING (Glastrier)
    }

    [Theory]
    [InlineData(4, "Poké Ball", "Poké Ball")]
    [InlineData(174, "Starf Berry", "Starf Berry")]
    [InlineData(176, "Choice Band", "Choice Band")]            // CFRU: ITEM_CHOICE_BAND 0xB0 under UNBOUND
    [InlineData(227, "Weakness Policy", "Weakness Policy")]
    [InlineData(447, "Charizardite X", "Charizardite X")]
    [InlineData(707, "Heavy Duty Boots", "Heavy-Duty Boots")]
    [InlineData(726, "Black Augurite", "Black Augurite")]
    [InlineData(727, "Peat Block", "Peat Block")]
    [InlineData(728, "Hisui Rock", null)]                      // Unbound-only evolution item
    public void ItemsDecodeAndBridge(int rom, string name, string? pkhexName)
    {
        Assert.Equal(name, UnboundData.ItemName(rom));
        var national = UnboundData.ItemToNational(rom);
        if (pkhexName is null)
        {
            Assert.Equal(0, national);
            return;
        }
        Assert.Equal(pkhexName, Strings.itemlist[national]);
        Assert.Equal(rom, UnboundData.ItemFromNational(national));
    }

    [Theory]
    [InlineData(29, 29)]     // Nidoran♀
    [InlineData(246, 246)]   // Larvitar: ids agree through Gen 2
    [InlineData(277, 252)]   // Treecko: Gen-3 internal order
    [InlineData(413, 201)]   // Unown B form
    [InlineData(770, 662)]   // Fletchinder (ROM text "Fletchindr")
    [InlineData(777, 669)]   // Flabébé
    [InlineData(1115, 823)]  // Corviknight (ROM text "Corvknight")
    [InlineData(1256, 903)]  // Sneasler
    [InlineData(1261, 6)]    // trailing 2.1 form, bridged by name
    public void SpeciesBridgeToNational(int rom, int national) =>
        Assert.Equal(national, UnboundData.NationalIdOf(rom));

    [Theory]
    [InlineData(252, 277)]
    [InlineData(201, 201)]
    [InlineData(903, 1256)]
    public void NationalSpeciesResolveToTheBaseRomId(int national, int rom) =>
        Assert.Equal(rom, UnboundData.SpeciesFromNational(national));

    [Fact]
    public void EveryStorableIdBridges()
    {
        for (var move = 1; move <= 766; move++) // 767+ are battle-only Z/Max moves
            if (move is not (355 or 499)) // Leech Fang, Metal Bash: Unbound originals
                Assert.True(UnboundData.MoveToNational(move) > 0, $"move {move} {UnboundData.MoveName(move)}");
        for (var ability = 1; ability <= 254; ability++)
            if (ability is not (77 or 208)) // ABILITY_UNUSED; ABILITY_PORTALPOWER is a CFRU original
                Assert.True(UnboundData.AbilityToNational(ability) > 0, $"ability {ability} {UnboundData.AbilityName(ability)}");
        // Species: 1180 Unbound-Cloud entries + 52 battle-only forms Unbound-Cloud omits
        // (megas 869-918, Zygarde 835/836) + the 8 trailing 2.1 forms, bridged by name;
        // the rest are the Gen-3 filler block (252 is "Egg", 253-276 "?") and Bad Egg.
        Assert.Equal(1240, Enumerable.Range(1, 1267).Count(species => UnboundData.NationalIdOf(species) > 0));
        Assert.Equal(0, UnboundData.NationalIdOf(252));
    }

    [Fact]
    public void HisuiEvolutionItemsAreAddableToTheItemsPocket()
    {
        using var session = UnboundSessionTestsAccess.Open();
        if (session is null) return;
        var legal = session.GetPouchLegalItems("Items");
        Assert.Contains(726, legal);
        Assert.Contains(727, legal);
        Assert.Contains(728, legal);
        Assert.Equal("Hisui Rock", session.GetItemNames()[728]);
        Assert.Equal(1, session.SetItemCount("Items", 728, 1));
        using var reloaded = new UnboundEngineSession(session.Serialize().ToArray());
        Assert.Contains(reloaded.GetBag().First(p => p.Name == "Items").Items, item => item is { Id: 728, Count: 1 });
    }

    [Fact]
    public void KnownMonDecodesThroughPkHexNames()
    {
        using var session = UnboundSessionTestsAccess.Open();
        if (session is null) return;

        // Edit the party lead into a mon whose ids all diverge between ROM and PKHeX,
        // then read it back from the serialized save.
        var bytes = session.Serialize().ToArray();
        using var probe = new UnboundEngineSession(bytes);
        var national = probe.ReadEntity(-1, 0);
        Assert.False(national.IsEmpty);

        probe.ApplyEdit(-1, 0, new EntityEdit(
            Species: 903, Nickname: null, Level: null, Nature: null, Ability: null,
            HeldItem: 220, Move1: 434, Move2: 370, Move3: 0, Move4: 0,
            IVs: null, EVs: null, IsShiny: null, Ball: null, OriginalTrainer: null, Gender: null));
        using var reopened = new UnboundEngineSession(probe.Serialize().ToArray());
        var mon = reopened.ReadEntity(-1, 0);
        Assert.Equal(903, mon.Species);
        Assert.Equal("Sneasler", mon.SpeciesName);
        Assert.Equal("Choice Band", Strings.itemlist[mon.HeldItem]);
        Assert.Equal("Draco Meteor", Strings.movelist[mon.Move1]);
        Assert.Equal("Close Combat", Strings.movelist[mon.Move2]);
        Assert.Equal(0, mon.Move3);
        Assert.Contains(mon.Ability, reopened.GetAbilityChoices(903, 0));

        // The .pk3 export speaks PKHeX ids, not the ROM's 369/365.
        var export = reopened.ExportSlot(-1, 0).Data;
        var pk3 = new PK3(export); // (PK3 cannot hold species past Gen 3; moves are u16)
        Assert.Equal(434, pk3.Move1);
        Assert.Equal(370, pk3.Move2);
    }

    [Fact]
    public void EchoedEditLeavesTheMonUntouched()
    {
        // The editor echoes every field it was shown (in PKHeX ids). Before the bridge
        // this rewrote species/moves/items raw: national 252 (Treecko) is ROM "Egg".
        using var session = UnboundSessionTestsAccess.Open();
        if (session is null) return;
        var before = session.ReadEntity(-1, 0);
        var exported = session.ExportSlot(-1, 0).Data;
        var d = before;
        session.ApplyEdit(-1, 0, new EntityEdit(d.Species, d.Nickname, d.Level, d.Nature, d.Ability, d.HeldItem,
            d.Move1, d.Move2, d.Move3, d.Move4, d.IVs, d.EVs, null, d.Ball, d.OriginalTrainer, d.Gender));
        using var reopened = new UnboundEngineSession(session.Serialize().ToArray());
        var after = reopened.ReadEntity(-1, 0);
        Assert.Equal(exported, reopened.ExportSlot(-1, 0).Data);
        Assert.Equal((before.Species, before.Ability, before.HeldItem, before.Move1, before.Move2, before.Move3, before.Move4),
            (after.Species, after.Ability, after.HeldItem, after.Move1, after.Move2, after.Move3, after.Move4));
    }

    [Fact]
    public void CompactPcFormDecodesRomIds()
    {
        // A 58-byte PC mon built byte-for-byte: species u16 @0x1C, item u16 @0x1E,
        // four 10-bit moves packed at 0x27, hidden-ability flag = IV word bit 31.
        var raw = new byte[UnboundFormat.PcMonSize];
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x1C), 1256);       // Sneasler
        BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(0x1E), 176);        // Choice Band
        ulong packed = 369 | (365UL << 10) | (355UL << 20) | (765UL << 30);
        for (var i = 0; i < 5; i++) raw[0x27 + i] = (byte)(packed >> (8 * i));
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(0x36), 0x8000_0000u);
        var mon = new UnboundMon(raw, 0, party: false);

        Assert.Equal([369, 365, 355, 765], mon.Moves);
        Assert.Equal(["Draco Meteor", "Close Combat", "Leech Fang", "Lunar Blessing"], mon.Moves.Select(UnboundData.MoveName));
        Assert.Equal("Choice Band", UnboundData.ItemName(mon.HeldItem));
        Assert.Equal(903, UnboundData.NationalIdOf(mon.Species));
        Assert.True(mon.HiddenAbility);
        var hidden = UnboundData.ActiveAbility(mon);
        Assert.Equal(UnboundData.AbilityIds(1256).Hidden, hidden);
        Assert.Equal(Strings.abilitylist[UnboundData.AbilityToNational(hidden)], UnboundData.AbilityName(hidden));
    }
}

internal static class UnboundSessionTestsAccess
{
    public static UnboundEngineSession? Open()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        var path = directory is null ? null : Path.Combine(directory.FullName, ".local-testdata", "unbound-v2111.srm");
        return path is not null && File.Exists(path) ? new UnboundEngineSession(File.ReadAllBytes(path), "Unbound") : null;
    }
}
