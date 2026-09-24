namespace PKForge.Domain;

/// <summary>What a group of plan steps does, in the order the review lists them.</summary>
public enum LivingDexGroupKind
{
    /// <summary>Pokémon moving into the destination as they are.</summary>
    Move,
    /// <summary>Spare trade-evolution Pokémon evolving on the way in.</summary>
    Evolve,
    /// <summary>Bank entries moving into their dex-ordered slot.</summary>
    Sort,
    /// <summary>Steps that cannot run (no room, or the dry run refused them): they stay put.</summary>
    CannotMove,
    /// <summary>Nobody owns one: the player has to catch it.</summary>
    Catch,
}

/// <summary>How many Pokémon one save (or the Bank) gives to a group.</summary>
public sealed record LivingDexSourceShare(string Id, string Label, int Count);

/// <summary>One group of the review, with its plain-language sentence.</summary>
/// <param name="Title">The sentence the review shows ("Move 96 Pokémon from 4 games").</param>
/// <param name="Detail">One line under it: who gives what, or what happens.</param>
/// <param name="From">Sources giving to this group, most first.</param>
public sealed record LivingDexGroup(
    LivingDexGroupKind Kind,
    string Title,
    string Detail,
    IReadOnlyList<LivingDexStep> Steps,
    IReadOnlyList<LivingDexSourceShare> From);

/// <summary>A plain-language caveat on one step: a short row label and the full sentence.</summary>
public sealed record LivingDexCaveat(string Short, string Long, bool Serious);

/// <summary>
/// Turns a <see cref="LivingDexPlan"/> into the sentences the Autopilot's review shows.
/// Pure and deterministic, so every wording the player reads is testable.
/// </summary>
public static class LivingDexPlanSummary
{
    public static string Count(int count, string one, string many) => $"{count} {(count == 1 ? one : many)}";

    /// <summary>"the Bank" for the Bank, else the cartridge name.</summary>
    public static string Place(string id, string label) => id == LivingDexPlanner.BankId ? "the Bank" : label;

    /// <summary>The review's groups; <paramref name="failed"/> marks steps the dry run refused.</summary>
    public static IReadOnlyList<LivingDexGroup> Group(LivingDexPlan plan, Func<LivingDexStep, bool>? failed = null)
    {
        bool Stuck(LivingDexStep s) => s.Kind != LivingDexStepKind.Guide && (s.Blocked is not null || failed?.Invoke(s) == true);
        var labels = plan.Sources.ToDictionary(s => s.Id, s => s.Label, StringComparer.Ordinal);
        string LabelOf(string? id) => id is null ? "?" : labels.GetValueOrDefault(id) ?? id;

        IReadOnlyList<LivingDexSourceShare> Shares(IEnumerable<LivingDexStep> steps) => [.. steps
            .Where(s => s.SourceId is not null)
            .GroupBy(s => s.SourceId!, StringComparer.Ordinal)
            .Select(g => new LivingDexSourceShare(g.Key, LabelOf(g.Key), g.Count()))
            .OrderByDescending(s => s.Count).ThenBy(s => s.Label, StringComparer.Ordinal)];

        static string FromWhere(IReadOnlyList<LivingDexSourceShare> from) => from switch
        {
            [] => "",
            [var one] => $" from {Place(one.Id, one.Label)}",
            _ when from.All(s => s.Id != LivingDexPlanner.BankId) => $" from {from.Count} games",
            _ => $" from {Count(from.Count(s => s.Id != LivingDexPlanner.BankId), "game", "games")} and the Bank",
        };

        static string Breakdown(IReadOnlyList<LivingDexSourceShare> from) =>
            string.Join(" · ", from.Take(4).Select(s => $"{(s.Id == LivingDexPlanner.BankId ? "Bank" : s.Label)} {s.Count}"))
            + (from.Count > 4 ? $" · +{from.Count - 4} more" : "");

        var groups = new List<LivingDexGroup>();
        var into = Place(plan.Options.DestinationId, plan.DestinationLabel);

        var moves = plan.Steps.Where(s => s.Kind == LivingDexStepKind.Move && !Stuck(s)).ToList();
        if (moves.Count > 0)
        {
            var from = Shares(moves);
            groups.Add(new(LivingDexGroupKind.Move, $"Move {Count(moves.Count, "Pokémon", "Pokémon")}{FromWhere(from)}",
                from.Count > 1 ? Breakdown(from) : $"Spare ones only: they leave {Place(from[0].Id, from[0].Label)} and land in {into}.", moves, from));
        }

        var evolves = plan.Steps.Where(s => s.Kind == LivingDexStepKind.EvolveAndMove && !Stuck(s)).ToList();
        if (evolves.Count > 0)
            groups.Add(new(LivingDexGroupKind.Evolve, $"Evolve {evolves.Count} by trade on the way",
                "A spare one that only evolves by trading evolves first, then moves in.", evolves, Shares(evolves)));

        var sorts = plan.Steps.Where(s => s.Kind == LivingDexStepKind.Arrange && !Stuck(s)).ToList();
        if (sorts.Count > 0)
            groups.Add(new(LivingDexGroupKind.Sort, $"Sort {Count(sorts.Count, "Pokémon", "Pokémon")} already in the Bank",
                "They move to their own slot in the living dex boxes, in dex order.", sorts, []));

        var stuck = plan.Steps.Where(Stuck).ToList();
        if (stuck.Count > 0)
            groups.Add(new(LivingDexGroupKind.CannotMove, $"{Count(stuck.Count, "Pokémon", "Pokémon")} can't move",
                "They stay exactly where they are. Pick one to see why.", stuck, Shares(stuck)));

        var catches = plan.Steps.Where(s => s.Kind == LivingDexStepKind.Guide).ToList();
        if (catches.Count > 0)
            groups.Add(new(LivingDexGroupKind.Catch, $"Still to catch: {catches.Count}",
                "Nobody on your shelf has one yet. Pick one to see where to find it.", catches, []));

        return groups;
    }

    /// <summary>The review's first sentence: what the destination becomes.</summary>
    public static string Headline(LivingDexPlan plan)
    {
        var where = plan.DestinationKind == LivingDexSourceKind.Bank ? "Your Bank living dex" : $"{plan.DestinationLabel}'s living dex";
        var gain = plan.CoveredAfter - plan.CoveredBefore;
        if (plan.CoveredBefore >= plan.TargetCount) return $"{where} is complete: {plan.TargetCount} of {plan.TargetCount}.";
        return gain > 0
            ? $"{where} goes from {plan.CoveredBefore} to {plan.CoveredAfter} of {plan.TargetCount} (+{gain})."
            : $"{where} has {plan.CoveredBefore} of {plan.TargetCount}. Nothing you own can fill the rest yet.";
    }

    /// <summary>A step's caveats in plain words, most serious first.</summary>
    public static IReadOnlyList<LivingDexCaveat> Caveats(LivingDexWarnings warnings, bool? landsLegal = null)
    {
        var list = new List<LivingDexCaveat>();
        if (landsLegal == false)
            list.Add(new("may be flagged", "It would land flagged as illegal (the game still loads it).", true));
        if (warnings.HasFlag(LivingDexWarnings.IllegalSource))
            list.Add(new("already flagged", "It is already flagged illegal where it lives.", true));
        if (warnings.HasFlag(LivingDexWarnings.LastCopy))
            list.Add(new("only copy", "Its game keeps no other one (you allowed this).", true));
        if (warnings.HasFlag(LivingDexWarnings.Downgrade))
            list.Add(new("older game", "It goes to an older game: its moves, Ability or Ball may change to fit.", false));
        if (warnings.HasFlag(LivingDexWarnings.FromParty))
            list.Add(new("from party", "It leaves the party (you allowed this).", false));
        return list;
    }
}
