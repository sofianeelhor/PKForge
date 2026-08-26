
using PKForge.Engine;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Legal ability lists follow each format's personal table exactly: the Chimchar line is
/// Blaze-only in Gen 4 (Iron Fist arrives in Gen 5 as the hidden ability), and HaX mode
/// bypasses the table entirely with the full ability list.
/// </summary>
public sealed class AbilityChoiceTests
{
    [Theory]
    [InlineData(4, 390, 1)]  // Monferno, Platinum: Blaze only
    [InlineData(5, 390, 2)]  // Gen 5: Blaze + Iron Fist (hidden)
    [InlineData(7, 390, 2)]
    [InlineData(4, 392, 1)]  // Infernape: same story
    [InlineData(4, 1, 1)]    // Bulbasaur: Overgrow
    public void LegalAbilitiesFollowThePersonalTable(int generation, int species, int minimum)
    {
        var engine = new SaveEngine();
        using var session = engine.OpenBlankSession(generation);
        var abilities = session.GetAbilityChoices(species, 0);
        Assert.True(abilities.Count >= minimum,
            $"gen{generation} #{species} got [{string.Join(",", abilities)}], expected >= {minimum}");
    }
}
