using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>Save data listed twelve entries flat, some twice over; it is now grouped by intent, each entry once.</summary>
public sealed class SaveDataMenuTests
{
    [Fact]
    public void A_plain_save_shows_four_groups_in_order()
    {
        var groups = SaveDataMenu.Build(new SaveDataCapabilities());
        Assert.Equal(["Trainer", "Items", "World", "File"], groups.Select(g => g.Title));
    }

    [Fact]
    public void A_rom_hack_engine_without_dex_records_bag_or_gifts_keeps_its_card_and_the_file()
    {
        // QA 3.2 #1: a hack session opens the hub with only what its own engine backs.
        var entries = SaveDataMenu.Build(new SaveDataCapabilities(Pokedex: false, TrainerRecords: false, Bag: false, GiftInbox: false))
            .SelectMany(g => g.Rows).Select(r => r.Entry).ToList();
        Assert.Equal([SaveDataEntry.TrainerCard, SaveDataEntry.RestorePoints, SaveDataEntry.Export, SaveDataEntry.Close], entries);
    }

    [Fact]
    public void Every_entry_appears_once()
    {
        var all = new SaveDataCapabilities(true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true, true);
        var entries = SaveDataMenu.Build(all).SelectMany(g => g.Rows).Select(r => r.Entry).ToList();
        Assert.Equal(entries.Count, entries.Distinct().Count());
        Assert.Equal(Enum.GetValues<SaveDataEntry>().Length, entries.Count);
    }

    [Fact]
    public void The_raw_tools_live_together_and_only_when_allowed()
    {
        Assert.DoesNotContain(SaveDataMenu.Build(new SaveDataCapabilities()), g => g.Title == "Advanced");
        var advanced = SaveDataMenu.Build(new SaveDataCapabilities(Bytes: true, SaveBlocks: true)).Single(g => g.Title == "Advanced");
        Assert.Equal([SaveDataEntry.SaveBlocks, SaveDataEntry.Bytes], advanced.Rows.Select(r => r.Entry));
    }

    [Fact]
    public void Entries_the_save_cannot_use_are_left_out()
    {
        var items = SaveDataMenu.Build(new SaveDataCapabilities()).Single(g => g.Title == "Items");
        Assert.Equal([SaveDataEntry.Bag], items.Rows.Select(r => r.Entry));
    }

    [Fact]
    public void Contest_treats_and_the_sinnoh_underground_sit_with_the_items()
    {
        var items = SaveDataMenu.Build(new SaveDataCapabilities(GrandUnderground: true, PoffinCase: true, PokeblockCase: true, Underground: true))
            .Single(g => g.Title == "Items");
        Assert.Equal([SaveDataEntry.Bag, SaveDataEntry.PoffinCase, SaveDataEntry.PokeblockCase, SaveDataEntry.Underground, SaveDataEntry.GrandUnderground],
            items.Rows.Select(r => r.Entry));
        Assert.Equal(["prop-case", "poffin-case", "pokeblock-case", "explorer-kit", "explorer-kit"], items.Rows.Select(r => r.Icon));
    }

    [Fact]
    public void Gen6_and_Gen7_tools_show_only_for_their_games()
    {
        var plain = SaveDataMenu.Build(new SaveDataCapabilities()).SelectMany(g => g.Rows).Select(r => r.Entry).ToList();
        Assert.DoesNotContain(SaveDataEntry.ZygardeCells, plain);
        Assert.DoesNotContain(SaveDataEntry.FestivalPlaza, plain);
        Assert.DoesNotContain(SaveDataEntry.SuperTraining, plain);

        var gen7 = SaveDataMenu.Build(new SaveDataCapabilities(ZygardeCells: true, FestivalPlaza: true));
        Assert.Contains(gen7.Single(g => g.Title == "World").Rows, r => r.Entry == SaveDataEntry.ZygardeCells);
        Assert.Contains(gen7.Single(g => g.Title == "Trainer").Rows, r => r.Entry == SaveDataEntry.FestivalPlaza);
        var gen6 = SaveDataMenu.Build(new SaveDataCapabilities(SuperTraining: true));
        Assert.Contains(gen6.Single(g => g.Title == "Trainer").Rows, r => r.Entry == SaveDataEntry.SuperTraining);
    }

    [Fact]
    public void The_pokewalker_shows_with_the_world_only_for_heartgold_and_soulsilver()
    {
        Assert.DoesNotContain(SaveDataMenu.Build(new SaveDataCapabilities()).SelectMany(g => g.Rows), r => r.Entry == SaveDataEntry.Pokewalker);
        var row = SaveDataMenu.Build(new SaveDataCapabilities(Pokewalker: true)).Single(g => g.Title == "World").Rows
            .Single(r => r.Entry == SaveDataEntry.Pokewalker);
        Assert.Equal("power-anklet", row.Icon);
    }

    [Fact]
    public void The_e_reader_sits_with_the_ticket_events_for_gen3_saves_only()
    {
        var plain = SaveDataMenu.Build(new SaveDataCapabilities()).SelectMany(g => g.Rows).Select(r => r.Entry);
        Assert.DoesNotContain(SaveDataEntry.EReader, plain);
        var world = SaveDataMenu.Build(new SaveDataCapabilities(KeyItemEvents: true, EReader: true)).Single(g => g.Title == "World");
        Assert.Equal([SaveDataEntry.KeyItemEvents, SaveDataEntry.EReader], world.Rows.Select(r => r.Entry).TakeLast(2));
        Assert.Equal("enigma-berry", world.Rows.Last().Icon);
    }

    [Fact]
    public void The_pal_park_shows_with_the_world_only_for_gen4_saves()
    {
        Assert.DoesNotContain(SaveDataMenu.Build(new SaveDataCapabilities()).SelectMany(g => g.Rows), r => r.Entry == SaveDataEntry.PalPark);
        var world = SaveDataMenu.Build(new SaveDataCapabilities(Pokewalker: true, PalPark: true)).Single(g => g.Title == "World");
        var rows = world.Rows.Select(r => r.Entry).ToList();
        Assert.Equal(rows.IndexOf(SaveDataEntry.Pokewalker) + 1, rows.IndexOf(SaveDataEntry.PalPark));
        Assert.Equal("park-ball", world.Rows.Single(r => r.Entry == SaveDataEntry.PalPark).Icon);
    }
}
