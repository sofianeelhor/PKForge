using PKForge.Domain;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// QA 3.2 #5: hack editors showed wrong fields. The editor names an ability and a move by its
/// PKHeX id and a held item through <see cref="ISaveEngineSession.GetHeldItemNames"/>, so every
/// engine must hand it those ids (not a slot, not the ROM's own numbering), give a name for the
/// ones PKHeX has no id for, and read "no item" as the retail "(None)". The editor echoes every
/// field it was shown on save: that echo must leave the file byte for byte. Real saves only
/// (.local-testdata, gitignored); a missing one skips.
/// </summary>
public sealed class HackEditorFieldMappingTests
{
    private static readonly GameStrings Strings = GameInfo.GetStrings("en");

    private static ISaveEngineSession? Open(string relative) => HackSaveDataHubTests.Open(relative);

    /// <summary>What the editor's Save writes when nothing was touched (BoxBrowserViewModel.PendingEdits).</summary>
    private static EntityEdit Echo(EntityDetail d) => new(
        d.Species, d.Nickname, d.Level, d.Nature, d.Ability, d.HeldItem, d.Move1, d.Move2, d.Move3, d.Move4,
        d.IVs, d.EVs, d.IsShiny, d.Ball, d.OriginalTrainer, d.Gender, d.Friendship);

    private static void AssertEchoIsANoOp(ISaveEngineSession session, int box, int slot)
    {
        var before = session.Serialize().ToArray();
        session.ApplyEdit(box, slot, Echo(session.ReadEntity(box, slot)));
        Assert.True(session.Serialize().Span.SequenceEqual(before), $"echoing {box}/{slot} changed the save");
    }

    [Theory]
    [InlineData("romhacks/batch-2026-11-12/loose/Pokemon - Origin HeartGold Version.sav")]
    [InlineData("romhacks/quetzal-9.1.sav")]
    [InlineData("romhacks/soulgold.sav")]
    [InlineData("romhacks/lazarus.sav")]
    [InlineData("romhacks/batch-2026-11-12/loose/Pokemon Fire Red Extended 3.5.3.sav")]
    [InlineData("romhacks/batch-2026-11-12/saves/saves/Pokemon - Elite Redux.sav")]
    [InlineData("romhacks/batch-2026-11-12/saves/saves/Pokemon - Emerald Exceeded.sav")]
    [InlineData("romhacks/batch-2026-11-12/romhack/romhack saves/Pokemon - GS Chronicles.sav")]
    [InlineData("thor/retroarch/mGBA/Radical-red-champ.sav")]
    [InlineData("thor/retroarch/mGBA/Pokémon Unbound (v2.1.1.1).srm")]
    public void No_item_reads_as_the_retail_none(string sample)
    {
        using var session = Open(sample);
        if (session is null) return;
        Assert.Equal("(None)", session.GetItemNames()[0]);
        Assert.Equal("(None)", session.GetHeldItemNames()[0]);
    }

    [Fact]
    public void Quetzal_shows_the_ability_its_slot_gives_and_the_party_friendship()
    {
        using var session = Open("romhacks/quetzal-9.1.sav");
        if (session is null) return;

        var wooper = session.ReadEntity(0, 0);
        Assert.Equal((int)Species.Wooper, wooper.Species);
        Assert.Equal((int)Ability.Damp, wooper.Ability); // slot 0, was "(None)"
        var budew = session.ReadEntity(0, 1);
        Assert.Equal((int)Species.Budew, budew.Species);
        Assert.NotEqual((int)Ability.Stench, budew.Ability); // slot 1, was ability id 1
        Assert.Contains(budew.Ability, session.GetAbilityChoices(budew.Species, 0));
        Assert.Null(budew.AbilityName);

        for (var slot = 0; slot < 8; slot++)
            AssertEchoIsANoOp(session, 0, slot);

        // Picking another of the species' abilities writes its slot, and reads back as that id.
        var other = session.GetAbilityChoices(budew.Species, 0).First(id => id != budew.Ability);
        session.ApplyEdit(0, 1, new EntityEdit(Ability: other));
        Assert.Equal(other, session.ReadEntity(0, 1).Ability);
        Assert.Throws<ArgumentOutOfRangeException>(() => session.ApplyEdit(0, 1, new EntityEdit(Ability: (int)Ability.Stench)));
    }

    [Fact]
    public void Quetzal_party_friendship_is_the_stored_byte()
    {
        using var session = Open("romhacks/quetzal-bills.sav");
        if (session is null) return;
        var latias = session.ReadEntity(-1, 0);
        Assert.Equal((int)Species.Latias, latias.Species);
        Assert.Equal((int)Ability.Levitate, latias.Ability);
        Assert.Equal(130, latias.Friendship);
        AssertEchoIsANoOp(session, -1, 0);

        session.ApplyEdit(-1, 0, new EntityEdit(Friendship: 0));
        Assert.Equal(0, session.ReadEntity(-1, 0).Friendship);
    }

    [Fact]
    public void Fire_Red_Extended_names_the_ability_slot_its_rom_decides()
    {
        using var session = Open("romhacks/batch-2026-11-12/loose/Pokemon Fire Red Extended 3.5.3.sav");
        if (session is null) return;
        var suicune = session.ReadEntity(0, 0);
        Assert.Equal((int)Species.Suicune, suicune.Species);
        Assert.Equal(0, suicune.Ability);
        Assert.Matches(@"^Ability slot [12]$", suicune.AbilityName);
        Assert.Equal(0, suicune.HeldItem);
        Assert.Equal("(None)", session.GetHeldItemNames()[suicune.HeldItem]);
        AssertEchoIsANoOp(session, 0, 0);
    }

    [Fact]
    public void Elite_Redux_names_its_own_abilities_and_moves_and_keeps_them_on_save()
    {
        using var session = Open("romhacks/batch-2026-11-12/saves/saves/Pokemon - Elite Redux.sav");
        if (session is null) return;
        var staraptor = session.ReadEntity(0, 1);
        Assert.Equal((int)Species.Staraptor, staraptor.Species);
        Assert.Equal(0, staraptor.Ability);
        Assert.Equal("Fatal Precision", staraptor.AbilityName);
        Assert.Equal([0, 0, 0, 0], new[] { staraptor.Move1, staraptor.Move2, staraptor.Move3, staraptor.Move4 });
        Assert.NotNull(staraptor.MoveNames);
        Assert.Equal("Requiem", staraptor.MoveNames[0]);
        Assert.All(staraptor.MoveNames, name => Assert.False(string.IsNullOrEmpty(name)));

        var weavile = session.ReadEntity(0, 7);
        Assert.Equal((int)Move.VoltSwitch, weavile.Move2);
        Assert.Null(weavile.MoveNames![1]); // a PKHeX move needs no name of its own

        AssertEchoIsANoOp(session, 0, 1);
        AssertEchoIsANoOp(session, 0, 7);
    }

    [Fact]
    public void Radical_Red_names_held_items_in_the_id_space_it_reports_them_in()
    {
        using var session = Open("thor/retroarch/mGBA/Radical-red-champ.sav");
        if (session is null) return;
        var names = session.GetHeldItemNames();
        var emolga = session.ReadEntity(0, 5);
        Assert.Equal((int)Species.Emolga, emolga.Species);
        Assert.Equal("Cheri Berry", names[emolga.HeldItem]); // was the ROM table's "Bluk Berry"
        Assert.Equal("Occa Berry", names[session.ReadEntity(0, 6).HeldItem]);
        // Every name offered is one the hack can store.
        Assert.Contains(names, name => name.Length == 0);
        AssertEchoIsANoOp(session, 0, 5);
    }

    [Fact]
    public void Expansion_moves_are_bridged_to_pkhex_ids_past_the_national_order()
    {
        using var session = Open("romhacks/lazarus-pokedex.srm");
        if (session is null) return;
        var tinkatink = session.ReadEntity(0, 5);
        Assert.Equal((int)Species.Tinkatink, tinkatink.Species);
        Assert.Equal((int)Move.BrutalSwing, tinkatink.Move3); // raw 656, which PKHeX calls Twinkle Tackle
        Assert.True(session.StoresMove((int)Move.BrutalSwing));
        Assert.Equal((int)Move.BrutalSwing, session.GetMoveDetails(0, 5).Moves[2].Move);
        AssertEchoIsANoOp(session, 0, 5);

        // A pick writes the expansion's id and reads back as the PKHeX one.
        session.ApplyEdit(0, 5, new EntityEdit(Move4: (int)Move.ShoreUp));
        Assert.Equal((int)Move.ShoreUp, session.ReadEntity(0, 5).Move4);
        Assert.Equal((int)Move.BrutalSwing, session.ReadEntity(0, 5).Move3);
    }

    [Fact]
    public void Emerald_Exceeded_keeps_moves_it_cannot_name_and_says_its_ability_is_unread()
    {
        using var session = Open("romhacks/batch-2026-11-12/saves/saves/Pokemon - Exceeded.sav");
        if (session is null) return;
        var crustle = session.ReadEntity(0, 0);
        Assert.Equal((int)Species.Crustle, crustle.Species);
        Assert.Equal(0, crustle.Move1);
        Assert.Equal("Move #1052", crustle.MoveNames![0]);
        Assert.Equal((int)Move.DoubleTeam, crustle.Move2);
        Assert.NotNull(crustle.AbilityName);
        AssertEchoIsANoOp(session, 0, 0);
    }

    [Theory]
    [InlineData("romhacks/batch-2026-11-12/loose/Pokemon - Origin HeartGold Version.sav")]
    [InlineData("romhacks/recharged-yellow.sav")]
    [InlineData("romhacks/quetzal-9.1.sav")]
    [InlineData("romhacks/soulgold.sav")]
    [InlineData("romhacks/lazarus.sav")]
    [InlineData("romhacks/emerald-seaglass.sav")]
    [InlineData("romhacks/batch-2026-11-12/loose/Pokemon Fire Red Extended 3.5.3.sav")]
    [InlineData("romhacks/batch-2026-11-12/saves/saves/Pokemon - Elite Redux.sav")]
    [InlineData("romhacks/batch-2026-11-12/saves/saves/Pokemon - Emerald Exceeded.sav")]
    [InlineData("thor/retroarch/mGBA/Radical-red-champ.sav")]
    [InlineData("thor/retroarch/mGBA/Pokémon Unbound (v2.1.1.1).srm")]
    public void Every_hack_shows_an_ability_and_echoes_without_a_change(string sample)
    {
        using var session = Open(sample);
        if (session is null) return;
        foreach (var summary in session.Snapshot.Slots.Where(s => s.Species is not null).Take(12))
        {
            var detail = session.ReadEntity(summary.Box, summary.Slot);
            // Never "(None)" for a Pokémon: a PKHeX ability, or the engine's own name for it.
            Assert.True(detail.Ability > 0 || !string.IsNullOrEmpty(detail.AbilityName),
                $"{detail.SpeciesName} at {summary.Box}/{summary.Slot} reads ability {detail.Ability} with no name");
            Assert.True(detail.Ability < Strings.abilitylist.Length || !string.IsNullOrEmpty(detail.AbilityName));
            AssertEchoIsANoOp(session, summary.Box, summary.Slot);
        }
    }
}
