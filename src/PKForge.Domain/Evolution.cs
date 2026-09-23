namespace PKForge.Domain;

/// <summary>How an evolution is triggered, grouped the way the evolution card explains it.</summary>
public enum EvolutionTrigger
{
    /// <summary>Plain link trade (Machoke, Kadabra, Haunter, Graveler, ...).</summary>
    Trade,
    /// <summary>Link trade while holding an item, which the trade consumes (Onix + Metal Coat).</summary>
    TradeHoldingItem,
    /// <summary>Karrablast and Shelmet evolve only when traded for one another.</summary>
    TradeForPartner,
    /// <summary>Reaching a level.</summary>
    LevelUp,
    /// <summary>Leveling up with high friendship (optionally by day / at night).</summary>
    Friendship,
    /// <summary>Using an item from the bag on it (evolution stones and similar).</summary>
    UseItem,
    /// <summary>Leveling up while holding an item, which is consumed (Razor Fang, Oval Stone...).</summary>
    LevelUpHoldingItem,
    /// <summary>Leveling up knowing a move.</summary>
    LevelUpKnowingMove,
    /// <summary>Anything tied to a place, weather, a teammate, battle counters and so on.</summary>
    Special,
}

/// <summary>A move the new species learns on evolving (evolution moves and moves at its current level).</summary>
public sealed record EvolutionMoveOffer(int Move, string Name, bool EvolutionMove);

/// <summary>Teach <paramref name="Move"/> in <paramref name="Slot"/> (0-3); the slot's old move is forgotten.</summary>
public sealed record EvolutionMoveChoice(int Move, int Slot);

/// <summary>One evolution the selected Pokémon has in its own game, with everything the card shows.</summary>
/// <param name="Id">Stable index to pass back in <see cref="EvolutionRequest"/>.</param>
/// <param name="Requirement">What the game asks for, e.g. "Trade holding Metal Coat".</param>
/// <param name="ConditionMet">True when the requirement is satisfied (always for trades) - offered without HaX.</param>
/// <param name="BlockedReason">Why the evolution cannot happen right now; null when <see cref="Available"/>.</param>
/// <param name="ConsumedItem">The held or bag item used up by evolving, or null.</param>
/// <param name="HandlerNote">How the trade is recorded on the Pokémon (Gen 6+ handling trainer), or null.</param>
/// <param name="NewNickname">The species name it takes, or null when it keeps a custom nickname.</param>
/// <param name="StatsBefore">H/A/B/C/D/S now.</param>
/// <param name="StatsAfter">H/A/B/C/D/S after evolving.</param>
public sealed record EvolutionOption(
    int Id,
    int Species,
    int Form,
    string SpeciesName,
    EvolutionTrigger Trigger,
    string Requirement,
    bool ConditionMet,
    bool Available,
    string? BlockedReason,
    string? ConsumedItem,
    string? HandlerNote,
    string? NewNickname,
    string AbilityBefore,
    string AbilityAfter,
    IReadOnlyList<int> StatsBefore,
    IReadOnlyList<int> StatsAfter,
    IReadOnlyList<EvolutionMoveOffer> Moves)
{
    public bool IsTrade => Trigger is EvolutionTrigger.Trade or EvolutionTrigger.TradeHoldingItem or EvolutionTrigger.TradeForPartner;
}

/// <summary>The evolution card's model: the Pokémon as it is now and every way it can evolve.</summary>
/// <param name="Unavailable">Set when this save/slot cannot evolve anything (romhack, egg, no evolutions).</param>
public sealed record EvolutionPlan(
    int Species,
    int Form,
    string SpeciesName,
    string Nickname,
    bool Shiny,
    int Level,
    int Generation,
    IReadOnlyList<string> CurrentMoves,
    IReadOnlyList<EvolutionOption> Options,
    string? Unavailable);

/// <param name="OptionId">The <see cref="EvolutionOption.Id"/> chosen on the card.</param>
/// <param name="Moves">Moves to teach on evolving; null or empty learns nothing (a move goes into a free slot on its own when <see cref="LearnIntoFreeSlots"/>).</param>
/// <param name="Hax">HaX mode: requirements that are not met are ignored.</param>
/// <param name="TradePartnerName">Gen 6+: the link partner recorded as handling trainer for an untraded Pokémon.</param>
public sealed record EvolutionRequest(
    int OptionId,
    IReadOnlyList<EvolutionMoveChoice>? Moves = null,
    bool Hax = false,
    string? TradePartnerName = null,
    bool LearnIntoFreeSlots = false);

/// <summary>Lists and performs in-game-faithful evolutions for a stored Pokémon.</summary>
public interface IEvolutionService
{
    EvolutionPlan Plan(ISaveEngineSession session, int box, int slot, bool hax);

    /// <summary>Evolves in place; the caller commits the session through its guarded write path.</summary>
    GenerationOutcome Evolve(ISaveEngineSession session, int box, int slot, EvolutionRequest request);
}
