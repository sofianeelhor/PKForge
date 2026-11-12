using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>The bag tells what an item does, where it first appeared and who carries it, from a PokéAPI item document.</summary>
public sealed class ItemLoreTests
{
    private const string SmokeBall = """
    {
      "effect_entries": [{ "language": { "name": "en" }, "short_effect": "Held: Allows the holder to escape from any wild battle." }],
      "flavor_text_entries": [
        { "language": { "name": "en" }, "version_group": { "name": "sword-shield" }, "text": "An item to be held by a Pokémon. It enables the\nholder to flee from any wild Pokémon encounter without fail." },
        { "language": { "name": "fr" }, "version_group": { "name": "sword-shield" }, "text": "Un objet à tenir." },
        { "language": { "name": "en" }, "version_group": { "name": "ruby-sapphire" }, "text": "A hold item that\ncan be used to flee\nfrom a wild POKéMON." }
      ],
      "game_indices": [
        { "generation": { "name": "generation-iii" } },
        { "generation": { "name": "generation-ii" } }
      ],
      "held_by_pokemon": [
        { "pokemon": { "name": "koffing" } }, { "pokemon": { "name": "weezing" } },
        { "pokemon": { "name": "salazzle-totem" } }, { "pokemon": { "name": "salandit" } }, { "pokemon": { "name": "salazzle" } }
      ]
    }
    """;

    [Fact]
    public void The_latest_english_description_reads_on_one_line()
    {
        var lore = ItemLores.Parse(SmokeBall)!;
        Assert.Equal("An item to be held by a Pokémon. It enables the holder to flee from any wild Pokémon encounter without fail.", lore.About);
    }

    [Fact]
    public void First_seen_is_the_first_generation_with_the_item()
    {
        // Generation 2 has no description in the document: its first pair is named.
        Assert.Equal("Gold & Silver (Gen 2)", ItemLores.Parse(SmokeBall)!.FirstSeen);
    }

    [Fact]
    public void First_seen_names_the_exact_game_when_that_generation_describes_it()
    {
        const string json = """
        { "flavor_text_entries": [{ "language": { "name": "en" }, "version_group": { "name": "emerald" }, "text": "x" }],
          "game_indices": [{ "generation": { "name": "generation-iii" } }] }
        """;
        Assert.Equal("Emerald (Gen 3)", ItemLores.Parse(json)!.FirstSeen);
    }

    [Fact]
    public void Held_by_lists_three_species_without_their_forms() =>
        Assert.Equal("Koffing, Weezing and Salandit", ItemLores.Parse(SmokeBall)!.HeldBy);

    [Fact]
    public void Without_a_description_the_short_effect_is_used()
    {
        const string json = """{ "effect_entries": [{ "language": { "name": "en" }, "short_effect": "Opens a door." }] }""";
        var lore = ItemLores.Parse(json)!;
        Assert.Equal("Opens a door.", lore.About);
        Assert.Null(lore.FirstSeen);
        Assert.Null(lore.HeldBy);
    }

    [Fact]
    public void Old_capitals_are_written_as_today() =>
        Assert.Equal("Catches a wild Pokémon in a Poké Ball.", ItemLores.Tidy("Catches a wild\nPOKéMON in a\nPOKé BALL."));

    [Fact]
    public void A_broken_document_gives_no_lore() => Assert.Null(ItemLores.Parse("{ not json"));
}
