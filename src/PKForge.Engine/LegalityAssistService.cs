using PKForge.Domain;
using PKHeX.Core;
using PKHeX.Core.AutoMod;

namespace PKForge.Engine;

/// <summary>
/// PKHeX's "suggest" buttons and legality report as a preview-first service. Every
/// suggestion is computed on a clone with the pinned engine's own routine (the ones the
/// WinForms PKMEditor calls: EncounterSuggestion.GetSuggestedMetInfo, BallApplicator,
/// LegalityAnalysis.GetSuggestedRelearnMoves, MoveSetApplicator.GetMoveSet), then judged by
/// a fresh LegalityAnalysis. Nothing reaches the save until <see cref="Apply"/> writes the
/// exact previewed bytes.
/// </summary>
public sealed class LegalityAssistService : ILegalityAssistService
{
    public static LegalityAssistService Shared { get; } = new();

    private static readonly GameStrings Strings = GameInfo.GetStrings("en");

    /// <summary>The suggestions "Fix all safe" may combine (ties go to the earlier one).</summary>
    private static readonly LegalityFix[] SafeOrder =
    [
        LegalityFix.MetInfo, LegalityFix.EggLocation, LegalityFix.Ball, LegalityFix.RelearnMoves,
        LegalityFix.CurrentMoves, LegalityFix.Memories, LegalityFix.EffortValues, LegalityFix.IVs,
    ];

    // ───────────────────────────── report ─────────────────────────────

    public LegalityFixReport GetReport(ISaveEngineSession session, int box, int slot)
    {
        if (!TryGet(session, box, slot, out _, out var pk)) return LegalityFixReport.Unsupported;
        var la = new LegalityAnalysis(pk);
        var ctx = LegalityLocalizationContext.Create(la);

        var groups = new Dictionary<CheckIdentifier, List<LegalityCheckLine>>();
        void Add(CheckIdentifier id, CheckSeverity severity, string text)
        {
            if (!groups.TryGetValue(id, out var list)) groups[id] = list = [];
            list.Add(new LegalityCheckLine(severity, text));
        }

        for (var i = 0; i < la.Info.Moves.Length; i++)
        {
            var move = la.Info.Moves[i];
            if (!move.IsParsed || move.Valid) continue;
            Add(CheckIdentifier.CurrentMove, CheckSeverity.Invalid, $"Move {i + 1} ({MoveName(pk.GetMove(i))}): {move.Summary(ctx)}");
        }
        for (var i = 0; i < la.Info.Relearn.Length; i++)
        {
            var move = la.Info.Relearn[i];
            if (!move.IsParsed || move.Valid) continue;
            Add(CheckIdentifier.RelearnMove, CheckSeverity.Invalid, $"Relearn {i + 1} ({MoveName(pk.GetRelearnMove(i))}): {move.Summary(ctx)}");
        }
        foreach (var check in la.Results)
        {
            if (check.Valid && check.Judgement != Severity.Fishy) continue;
            Add(check.Identifier, ToSeverity(check.Judgement), StripSeverity(ctx, check));
        }

        var list = groups
            .Select(pair => new LegalityCheckGroup(pair.Key.ToString(), Title(pair.Key),
                pair.Value.Max(line => line.Severity), pair.Value, FixFor(pair.Key, pk)))
            .OrderByDescending(g => g.Severity)
            .ThenBy(g => g.Title, StringComparer.Ordinal)
            .ToList();
        if (list.Count == 0)
            list.Add(new LegalityCheckGroup("valid", "All checks", CheckSeverity.Valid,
                [new LegalityCheckLine(CheckSeverity.Valid, "Every check passes.")], null));
        return new LegalityFixReport(true, la.Valid, EncounterText(la), list);
    }

    public MoveLegality GetMoveLegality(ISaveEngineSession session, int box, int slot)
    {
        if (!TryGet(session, box, slot, out _, out var pk)) return MoveLegality.Unsupported;
        return GetMoveLegality(pk);
    }

    internal static MoveLegality GetMoveLegality(PKM pk)
    {
        var la = new LegalityAnalysis(pk);
        var ctx = LegalityLocalizationContext.Create(la);
        var moves = new List<MoveVerdict>(4);
        for (var i = 0; i < la.Info.Moves.Length; i++)
            moves.Add(Verdict(ctx, la.Info.Moves[i], pk.GetMove(i)));
        var relearn = new List<MoveVerdict>(4);
        if (pk.Format >= 6)
            for (var i = 0; i < la.Info.Relearn.Length; i++)
                relearn.Add(Verdict(ctx, la.Info.Relearn[i], pk.GetRelearnMove(i)));
        return new MoveLegality(true, moves, relearn);
    }

    private static MoveVerdict Verdict(in LegalityLocalizationContext ctx, MoveResult result, ushort move)
    {
        if (!result.IsParsed) return new MoveVerdict(move, true, "Not analyzed");
        var summary = result.Summary(ctx).Trim();
        if (move == 0 && result.Valid) summary = "Empty";
        return new MoveVerdict(move, result.Valid, summary.Length == 0 ? result.Valid ? "Legal" : "Not legal" : summary);
    }

    // ───────────────────────────── preview / apply ─────────────────────────────

    public LegalityFixPreview Preview(ISaveEngineSession session, int box, int slot, LegalityFix fix)
    {
        if (!TryGet(session, box, slot, out var engine, out var pk))
            return Unavailable(fix, box, slot, "Legality suggestions need a game with offline legality tables.");
        if (pk.Species == 0)
            return Unavailable(fix, box, slot, "Empty slot.");
        var save = engine.SaveFile;

        var basis = pk.Clone();
        var before = new LegalityAnalysis(basis);
        PKM candidate;
        string? failure;
        if (fix == LegalityFix.AllSafe)
        {
            (candidate, failure) = FixAllSafe(basis, save);
        }
        else
        {
            candidate = basis.Clone();
            failure = Suggest(fix, candidate, before, save);
        }
        if (failure is not null)
            return Unavailable(fix, box, slot, failure, basis, before);

        candidate.RefreshChecksum();
        var after = new LegalityAnalysis(candidate);
        var changes = Diff(basis, candidate);
        if (changes.Count == 0)
            return Unavailable(fix, box, slot, fix == LegalityFix.AllSafe
                ? "No safe fix applies: every suggestion either changes nothing or adds a problem."
                : "Already matches the suggestion.", basis, before);
        return new LegalityFixPreview(fix, TitleOf(fix), box, slot, true, null, changes,
            before.Valid, after.Valid, Problems(before), Problems(after), Stored(basis), Bytes(candidate));
    }

    public GenerationOutcome Apply(ISaveEngineSession session, LegalityFixPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.Available || preview.Candidate.IsEmpty)
            return new GenerationOutcome(false, preview.Unavailable ?? "Nothing to apply.");
        if (!TryGet(session, preview.Box, preview.Slot, out var engine, out var current))
            return new GenerationOutcome(false, "This session cannot be edited here.");
        if (!Stored(current).AsSpan().SequenceEqual(preview.Basis.Span))
            return new GenerationOutcome(false, "The Pokémon changed since the preview; open the suggestion again.");
        if (current.Data.Length != preview.Candidate.Length)
            return new GenerationOutcome(false, "The preview does not match this Pokémon's format.");

        var updated = current.Clone();
        preview.Candidate.Span.CopyTo(updated.Data);
        updated.RefreshChecksum();
        Place(engine.SaveFile, preview.Box, preview.Slot, updated);
        return new GenerationOutcome(true, $"{preview.Title}: {preview.Changes.Count} field(s) updated.");
    }

    // ───────────────────────────── suggestions ─────────────────────────────

    /// <summary>Applies one suggestion to <paramref name="pk"/> in place; null on success, else why not.</summary>
    internal static string? Suggest(LegalityFix fix, PKM pk, LegalityAnalysis la, SaveFile save) => fix switch
    {
        LegalityFix.MetInfo => SuggestMet(pk),
        LegalityFix.Ball => SuggestBall(pk),
        LegalityFix.EggLocation => SuggestEggLocation(pk, save),
        LegalityFix.RelearnMoves => SuggestRelearn(pk, la),
        LegalityFix.CurrentMoves => SuggestMoves(pk),
        LegalityFix.Memories => SuggestMemories(pk),
        LegalityFix.EffortValues => SuggestEVs(pk),
        LegalityFix.IVs => SuggestIVs(pk, la),
        _ => "Unknown suggestion.",
    };

    /// <summary>
    /// PKHeX's met suggestion first. Its pick is the lowest-level encounter for the species,
    /// which ignores PID/IV correlation, abilities and egg state; when that pick is not
    /// legal, every encounter the generator offers for this origin is tried as a met
    /// location (eggs also try the no-location and link-trade values) and the candidate
    /// with the fewest problems wins. LegalityAnalysis is the judge of every candidate.
    /// </summary>
    private static string? SuggestMet(PKM pk)
    {
        var basis = pk.Clone();
        var primary = pk.Clone();
        var failure = SuggestMetPkhex(primary);
        var best = failure is null ? primary : null;
        var bestScore = best is null ? int.MaxValue : Problems(new LegalityAnalysis(best));
        if (bestScore > 0)
        {
            foreach (var trial in MetAlternatives(basis))
            {
                if (trial.Data.SequenceEqual(basis.Data)) continue;
                var la = new LegalityAnalysis(trial);
                var score = Problems(la);
                if (score >= bestScore) continue;
                (best, bestScore) = (trial, score);
                if (score == 0) break;
            }
        }
        if (best is null) return failure;
        best.Data.CopyTo(pk.Data);
        return null;
    }

    private const int MaxMetCandidates = 256;

    private static IEnumerable<PKM> MetAlternatives(PKM basis)
    {
        if (basis.Format < 3) yield break;
        if (basis.IsEgg)
        {
            var suggested = EncounterSuggestion.GetSuggestedMetInfo(basis)?.Location ?? 0;
            ushort[] locations = [0, basis.EggLocation, suggested, EncounterSuggestion.GetSuggestedEggMetLocation(basis),
                Locations.LinkTrade4, Locations.LinkTrade5, Locations.LinkTrade6];
            foreach (var location in locations.Distinct())
            foreach (var level in new byte[] { 0, 1 })
            {
                var egg = basis.Clone();
                egg.MetLocation = location;
                egg.MetLevel = level;
                yield return egg;
            }
            yield break;
        }

        var transfer = EncounterSuggestion.TryGetSuggestedTransferLocation(basis);
        if (transfer != EncounterSuggestion.LocationNone)
        {
            // Transferred (Pal Park, Transporter, VC, GO, HOME) origins store the fixed
            // transfer location; the met level stays what the transfer recorded.
            var moved = basis.Clone();
            moved.MetLocation = transfer;
            yield return moved;
        }
        var origin = new EvolutionOrigin(basis.Species, basis.Version.Context, basis.Generation, 1, basis.CurrentLevel, OriginOptions.SkipChecks);
        var buffer = new EvoCriteria[EvolutionTree.MaxEvolutions];
        var count = EvolutionChain.GetOriginChain(buffer, basis, origin);
        if (count == 0) yield break;
        var chain = buffer[..count];
        var generator = EncounterGenerator.GetGenerator(basis.Version, basis.Generation);
        var seen = new HashSet<(ushort, byte)>();
        var groups = EncounterTypeGroup.Slot | EncounterTypeGroup.Static | EncounterTypeGroup.Trade | EncounterTypeGroup.Egg;
        foreach (var encounter in generator.GetPossible(basis, chain, basis.Version, groups).Take(MaxMetCandidates))
        {
            var location = transfer != EncounterSuggestion.LocationNone ? transfer
                : encounter is IEncounterEgg ? EncounterSuggestion.GetSuggestedEggMetLocation(basis) : encounter.Location;
            var level = encounter is IEncounterEgg ? EncounterSuggestion.GetSuggestedEncounterEggMetLevel(basis)
                : basis.MetLevel >= encounter.LevelMin && basis.MetLevel <= encounter.LevelMax ? basis.MetLevel : encounter.LevelMin;
            if (!seen.Add((location, level))) continue;
            var trial = basis.Clone();
            trial.MetLocation = location;
            trial.MetLevel = level;
            if (trial.CurrentLevel < level) trial.CurrentLevel = level;
            if (trial.Format >= 4 && trial.MetDate is null) trial.MetDate = EncounterDate.GetDateSwitch();
            if (HasInvalid(new LegalityAnalysis(trial), CheckIdentifier.Ball))
                BallApplicator.ApplyBallLegalByColor(trial);
            yield return trial;
        }
    }

    /// <summary>PKMEditor.SetSuggestedMetLocation, field for field; plus the ball when the
    /// new origin rejects it and a met date when the format keeps one and it is missing.</summary>
    private static string? SuggestMetPkhex(PKM pk)
    {
        var encounter = EncounterSuggestion.GetSuggestedMetInfo(pk);
        if (encounter is null || (pk.Format >= 3 && encounter.Location == 0))
            return "No encounter for this species matches its origin game.";

        var level = encounter.LevelMin;
        int minLevel = EncounterSuggestion.GetLowestLevel(pk, level);
        if (minLevel == 0) minLevel = level;
        var location = encounter.Location;
        if (pk.Format < 3 && encounter.Encounter is { } x && !x.Version.Contains(GameVersion.C))
            location = 0;
        if (minLevel < level) minLevel = level;

        pk.MetLocation = location;
        pk.MetLevel = encounter.GetSuggestedMetLevel(pk);
        if (pk.Format >= 3)
        {
            if (encounter.HasGroundTile(pk.Format) && pk is IGroundTile tile)
                tile.GroundTile = encounter.GetSuggestedGroundTile();
            if (pk is { Gen6: true, WasEgg: true })
                pk.SetHatchMemory6();
            if (pk.Format >= 4 && pk.MetDate is null)
                pk.MetDate = EncounterDate.GetDateSwitch();
        }
        else if (pk is ICaughtData2 caught)
        {
            caught.MetTimeOfDay = location == 0 ? 0 : encounter.GetSuggestedMetTimeOfDay();
        }
        if (pk.CurrentLevel < minLevel)
            pk.CurrentLevel = (byte)minLevel;

        if (pk.Format >= 3 && HasInvalid(new LegalityAnalysis(pk), CheckIdentifier.Ball))
            BallApplicator.ApplyBallLegalByColor(pk);
        return null;
    }

    private static string? SuggestBall(PKM pk)
    {
        if (pk.Format < 3) return "Balls are not stored before Generation 3.";
        var before = pk.Ball;
        BallApplicator.ApplyBallLegalByColor(pk);
        return pk.Ball == before && HasInvalid(new LegalityAnalysis(pk), CheckIdentifier.Ball)
            ? "No ball is legal for the matched encounter."
            : null;
    }

    /// <summary>PKMEditor.UpdateMetAsEgg: the daycare (or link-trade) egg location for the
    /// origin generation; both are tried and the one the egg checks accept wins.</summary>
    private static string? SuggestEggLocation(PKM pk, SaveFile save)
    {
        if (!pk.IsEgg && !pk.WasEgg) return "Not hatched from an egg.";
        if (pk.Format < 4) return "This format keeps no egg location.";
        var tradedFirst = pk.IsEgg && pk.Version != save.Version;
        var options = new[] { tradedFirst, !tradedFirst }
            .Select(traded => EncounterSuggestion.GetSuggestedEncounterEggLocationEgg(pk, traded))
            .Distinct().ToArray();
        var bestScore = int.MaxValue;
        PKM? best = null;
        foreach (var location in options)
        {
            var trial = pk.Clone();
            trial.EggLocation = location;
            if (trial.EggMetDate is null) trial.EggMetDate = trial.MetDate ?? EncounterDate.GetDateSwitch();
            var score = InvalidCount(new LegalityAnalysis(trial), CheckIdentifier.Egg, CheckIdentifier.Encounter);
            if (score < bestScore) (bestScore, best) = (score, trial);
        }
        if (best is null) return "No egg location suggestion.";
        pk.EggLocation = best.EggLocation;
        pk.EggMetDate = best.EggMetDate;
        return null;
    }

    private static string? SuggestRelearn(PKM pk, LegalityAnalysis la)
    {
        if (pk.Format < 6) return "Relearn moves exist from Generation 6.";
        Span<ushort> moves = stackalloc ushort[4];
        la.GetSuggestedRelearnMoves(moves);
        pk.SetRelearnMoves(moves);
        return null;
    }

    /// <summary>
    /// PKMEditor.SetSuggestedMoves, made deterministic. PKHeX first offers the level-up set
    /// for the current level (MoveSourceType.Encounter); when that is not legal its fallback
    /// samples the learnable pool at random. Here the candidates are fixed instead: the
    /// level-up set, the current moves with only the failing slots refilled from it, then
    /// the failing slots refilled from the full learnable pool (LearnPossible) in id order.
    /// The candidate with the fewest failing moves wins; tech records follow the moves and
    /// PP is refilled, as in PKHeX.
    /// </summary>
    private static string? SuggestMoves(PKM pk)
    {
        var basis = pk.Clone();
        var la = new LegalityAnalysis(basis);
        Span<ushort> levelUp = stackalloc ushort[4];
        if (IsLevelUpSuggestionDeterministic(basis, la.EncounterOriginal))
            la.GetSuggestedCurrentMoves(levelUp, MoveSourceType.Encounter);
        var levelSet = levelUp.ToArray();

        var candidates = new List<ushort[]>();
        if (levelSet[0] != 0) candidates.Add(levelSet);
        var current = Enumerable.Range(0, 4).Select(i => basis.GetMove(i)).ToArray();
        var failing = Enumerable.Range(0, 4).Where(i => la.Info.Moves[i].IsParsed && !la.Info.Moves[i].Valid).ToArray();
        if (failing.Length > 0 && failing.Length < 4)
        {
            var refilled = current.ToArray();
            var spare = new Queue<ushort>(levelSet.Where(m => m != 0 && !current.Contains(m)));
            foreach (var index in failing) refilled[index] = spare.Count > 0 ? spare.Dequeue() : (ushort)0;
            candidates.Add(Compact(refilled));
        }

        PKM? best = null;
        var bestScore = int.MaxValue;
        foreach (var moves in candidates.Distinct(new MoveSetComparer()))
        {
            var trial = WithMoves(basis, moves);
            var score = InvalidCount(new LegalityAnalysis(trial), CheckIdentifier.CurrentMove);
            if (score < bestScore) (best, bestScore) = (trial, score);
            if (score == 0) break;
        }
        if (bestScore > 0)
        {
            var slots = failing.Length > 0 ? failing : [0, 1, 2, 3];
            var pooled = RefillFromPool(basis, la, current, slots);
            if (pooled is not null)
            {
                var score = InvalidCount(new LegalityAnalysis(pooled), CheckIdentifier.CurrentMove);
                if (score < bestScore) (best, bestScore) = (pooled, score);
            }
        }
        if (best is null) return "No legal moveset found for this format.";
        best.Data.CopyTo(pk.Data);
        return null;
    }

    /// <summary>
    /// MoveListSuggest.GetSuggestedMoves answers the level-up request from the learnset
    /// (deterministic) on exactly these branches; on every other branch it samples the
    /// learnable pool with Util.Rand (Random.Shared, unseedable), which would make a
    /// preview and "Fix all safe" disagree by luck.
    /// </summary>
    private static bool IsLevelUpSuggestionDeterministic(PKM pk, IEncounterTemplate enc) =>
        pk is { IsEgg: true, Format: <= 5 }
        || (enc.Generation <= 2 && pk.Format < 8)
        || pk.Species == enc.Species || pk.Format >= 8;

    private const int MaxPoolTriesPerSlot = 96;

    /// <summary>Keeps the legal moves and fills each failing slot with the first learnable
    /// move the checks accept there: level-up / relearn / evolution moves first, then the
    /// rest of the pool, each group in id order (deterministic).</summary>
    private static PKM? RefillFromPool(PKM basis, LegalityAnalysis la, ushort[] current, int[] failing)
    {
        var permitted = new bool[basis.MaxMoveID + 1];
        LearnPossible.Get(basis, la.EncounterOriginal, la.Info.EvoChainsAllGens, permitted, MoveSourceType.All);
        var natural = new bool[basis.MaxMoveID + 1];
        LearnPossible.Get(basis, la.EncounterOriginal, la.Info.EvoChainsAllGens, natural, MoveSourceType.Encounter | MoveSourceType.Evolve);
        var order = Enumerable.Range(1, permitted.Length - 1).Where(m => permitted[m])
            .OrderBy(m => natural[m] ? 0 : 1).ThenBy(m => m).Select(m => (ushort)m).ToArray();
        var moves = current.ToArray();
        foreach (var index in failing) moves[index] = 0;
        foreach (var index in failing)
        {
            var tries = 0;
            foreach (var move in order)
            {
                if (tries >= MaxPoolTriesPerSlot) break;
                if (moves.Contains(move)) continue;
                tries++;
                moves[index] = move;
                var trial = WithMoves(basis, Compact(moves));
                var check = new LegalityAnalysis(trial).Info.Moves;
                if (check.Take(4).Count(r => r.IsParsed && !r.Valid) == 0) break;
                moves[index] = 0;
            }
        }
        var result = Compact(moves);
        return result[0] == 0 ? null : WithMoves(basis, result);
    }

    private static PKM WithMoves(PKM basis, ushort[] moves)
    {
        var trial = basis.Clone();
        trial.SetMoves(moves);
        if (trial is ITechRecord records)
        {
            records.ClearRecordFlags();
            records.SetRecordFlags(moves, new LegalityAnalysis(trial).Info.EvoChainsAllGens.Get(trial.Context));
        }
        trial.HealPP();
        trial.RefreshChecksum();
        return trial;
    }

    /// <summary>Empty slots go last, as the games keep them.</summary>
    private static ushort[] Compact(ushort[] moves) => moves.Where(m => m != 0).Concat(moves.Where(m => m == 0)).ToArray();

    private sealed class MoveSetComparer : IEqualityComparer<ushort[]>
    {
        public bool Equals(ushort[]? x, ushort[]? y) => x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(ushort[] obj) => HashCode.Combine(obj[0], obj[1], obj[2], obj[3]);
    }

    /// <summary>
    /// Memories are judged, not guessed: the candidate rewrites (ALM's trade memory for a
    /// traded mon, a cleared handler for an untraded one, the Gen 6 hatch memory, cleared
    /// OT memories where the origin keeps none) are each scored by the memory checks and
    /// the best one wins.
    /// </summary>
    private static string? SuggestMemories(PKM pk)
    {
        if (pk is not IMemoryOT && pk is not IMemoryHT) return "This format keeps no memories.";
        var trials = new List<Action<PKM>>
        {
            p => p.SetSuggestedMemories(),
            p => { if (p is IMemoryHT h && p.IsUntraded) h.ClearMemoriesHT(); },
            p => { p.SetSuggestedMemories(); if (p is IMemoryOT o && (p.Generation < 6 || p.Generation >= 8)) o.ClearMemoriesOT(); },
            p => { if (p is { Gen6: true, WasEgg: true }) p.SetHatchMemory6(); p.SetSuggestedMemories(); },
            p => { if (p is PK6 { Gen6: true, WasEgg: false } six) six.SetRandomMemory6(); p.SetSuggestedMemories(); },
        };
        var baseline = InvalidCount(new LegalityAnalysis(pk), CheckIdentifier.Memory);
        var (bestScore, best) = (baseline, (PKM?)null);
        foreach (var trial in trials)
        {
            var candidate = pk.Clone();
            trial(candidate);
            var score = InvalidCount(new LegalityAnalysis(candidate), CheckIdentifier.Memory);
            if (score < bestScore) (bestScore, best) = (score, candidate);
        }
        if (best is null) return baseline == 0 ? "The memories already pass every check." : "No memory rewrite passes the memory checks.";
        best.Data.CopyTo(pk.Data);
        return null;
    }

    private static string? SuggestEVs(PKM pk)
    {
        if (pk.Format <= 2) return "Gen 1-2 stat experience has no cap to repair.";
        Span<int> evs = stackalloc int[6];
        pk.GetEVs(evs);
        var clamped = evs.ToArray();
        for (var i = 0; i < 6; i++) clamped[i] = Math.Clamp(clamped[i], 0, pk.MaxEV);
        var total = clamped.Sum();
        while (total > EffortValues.Max510)
        {
            var largest = Array.IndexOf(clamped, clamped.Max());
            var cut = Math.Min(clamped[largest], total - EffortValues.Max510);
            clamped[largest] -= cut;
            total -= cut;
        }

        var baseline = InvalidCount(new LegalityAnalysis(pk), CheckIdentifier.EVs);
        var (bestScore, best) = (baseline, (int[]?)null);
        foreach (var spread in new[] { clamped, new int[6] })
        {
            var trial = pk.Clone();
            trial.SetEVs(spread);
            var score = InvalidCount(new LegalityAnalysis(trial), CheckIdentifier.EVs);
            if (score < bestScore) (bestScore, best) = (score, spread);
        }
        if (best is null) return baseline == 0 ? "The EVs already pass every check." : "No EV spread passes the EV checks.";
        pk.SetEVs(best);
        return null;
    }

    /// <summary>IVs back inside the cap, then the encounter's fixed IVs or its guaranteed
    /// perfect-IV count restored on the highest stats. Correlated (PID/IV) origins reject
    /// every rewrite, and the checks decide.</summary>
    private static string? SuggestIVs(PKM pk, LegalityAnalysis la)
    {
        Span<int> current = stackalloc int[6];
        pk.GetIVs(current);
        var clamped = current.ToArray();
        for (var i = 0; i < 6; i++) clamped[i] = Math.Clamp(clamped[i], 0, pk.MaxIV);

        var trials = new List<int[]> { clamped };
        var encounter = la.EncounterMatch;
        if (encounter is IFixedIVSet { IVs.IsSpecified: true } fixedSet)
        {
            var fixedIvs = clamped.ToArray();
            for (var i = 0; i < 6; i++)
                if (fixedSet.IVs[i] >= 0) fixedIvs[i] = fixedSet.IVs[i];
            trials.Add(fixedIvs);
        }
        if (encounter is IFlawlessIVCount { FlawlessIVCount: > 0 } flawless)
        {
            var perfect = clamped.ToArray();
            var need = flawless.FlawlessIVCount - perfect.Count(v => v == pk.MaxIV);
            foreach (var index in Enumerable.Range(0, 6).OrderByDescending(i => perfect[i]))
            {
                if (need <= 0) break;
                if (perfect[index] == pk.MaxIV) continue;
                perfect[index] = pk.MaxIV;
                need--;
            }
            trials.Add(perfect);
        }

        var baseline = InvalidCount(la, CheckIdentifier.IVs);
        var (bestScore, best) = (baseline, (int[]?)null);
        foreach (var ivs in trials)
        {
            var trial = pk.Clone();
            trial.SetIVs(ivs);
            var score = InvalidCount(new LegalityAnalysis(trial), CheckIdentifier.IVs);
            if (score < bestScore) (bestScore, best) = (score, ivs);
        }
        if (best is null) return baseline == 0 ? "The IVs already pass every check." : "No IV spread passes the IV checks (the origin fixes them to its PID).";
        pk.SetIVs(best);
        return null;
    }

    /// <summary>
    /// Greedy repair on one cumulative copy: each round tries every applicable suggestion
    /// (met data always, the others only when their own checks fail) and keeps the one
    /// that lowers the total problem count the most. A step that does not strictly lower
    /// the total is never kept, so no kept step can add a problem anywhere.
    /// </summary>
    private static (PKM Candidate, string? Failure) FixAllSafe(PKM basis, SaveFile save)
    {
        var work = basis.Clone();
        var la = new LegalityAnalysis(work);
        if (la.Valid) return (work, "Already legal: there is nothing to fix.");
        var used = new HashSet<LegalityFix>();
        while (!la.Valid && used.Count < SafeOrder.Length)
        {
            var current = Problems(la);
            (LegalityFix Fix, PKM Pk, LegalityAnalysis La)? best = null;
            foreach (var fix in SafeOrder)
            {
                if (used.Contains(fix)) continue;
                if (fix != LegalityFix.MetInfo && InvalidCount(la, Targets(fix)) == 0) continue;
                var trial = work.Clone();
                if (Suggest(fix, trial, la, save) is not null) continue;
                trial.RefreshChecksum();
                var trialLa = new LegalityAnalysis(trial);
                var score = Problems(trialLa);
                if (score < current && (best is null || score < Problems(best.Value.La)))
                    best = (fix, trial, trialLa);
            }
            if (best is not { } step) break;
            (work, la) = (step.Pk, step.La);
            used.Add(step.Fix);
        }
        return (work, null);
    }


    // ───────────────────────────── helpers ─────────────────────────────

    private static CheckIdentifier[] Targets(LegalityFix fix) => fix switch
    {
        LegalityFix.MetInfo => [CheckIdentifier.Encounter, CheckIdentifier.Level],
        LegalityFix.Ball => [CheckIdentifier.Ball],
        LegalityFix.EggLocation => [CheckIdentifier.Egg],
        LegalityFix.RelearnMoves => [CheckIdentifier.RelearnMove],
        LegalityFix.CurrentMoves => [CheckIdentifier.CurrentMove],
        LegalityFix.Memories => [CheckIdentifier.Memory],
        LegalityFix.EffortValues => [CheckIdentifier.EVs],
        LegalityFix.IVs => [CheckIdentifier.IVs],
        _ => [],
    };

    private static LegalityFix? FixFor(CheckIdentifier id, PKM pk) => id switch
    {
        CheckIdentifier.Encounter or CheckIdentifier.Level => LegalityFix.MetInfo,
        CheckIdentifier.Ball when pk.Format >= 3 => LegalityFix.Ball,
        CheckIdentifier.Egg when pk.Format >= 4 && (pk.IsEgg || pk.WasEgg) => LegalityFix.EggLocation,
        CheckIdentifier.RelearnMove when pk.Format >= 6 => LegalityFix.RelearnMoves,
        CheckIdentifier.CurrentMove => LegalityFix.CurrentMoves,
        CheckIdentifier.Memory => LegalityFix.Memories,
        CheckIdentifier.EVs when pk.Format >= 3 => LegalityFix.EffortValues,
        CheckIdentifier.IVs => LegalityFix.IVs,
        _ => null,
    };

    /// <summary>Invalid findings: failed checks plus failed move and relearn slots.</summary>
    internal static int Problems(LegalityAnalysis la) =>
        la.Results.Count(r => !r.Valid) + la.Info.Moves.Count(m => m.IsParsed && !m.Valid) + la.Info.Relearn.Count(m => m.IsParsed && !m.Valid);

    private static int InvalidCount(LegalityAnalysis la, params CheckIdentifier[] ids)
    {
        var count = la.Results.Count(r => !r.Valid && ids.Contains(r.Identifier));
        if (ids.Contains(CheckIdentifier.CurrentMove)) count += la.Info.Moves.Count(m => m.IsParsed && !m.Valid);
        if (ids.Contains(CheckIdentifier.RelearnMove)) count += la.Info.Relearn.Count(m => m.IsParsed && !m.Valid);
        return count;
    }

    private static bool HasInvalid(LegalityAnalysis la, CheckIdentifier id) => InvalidCount(la, id) > 0;

    private static CheckSeverity ToSeverity(Severity judgement) => judgement switch
    {
        PKHeX.Core.Severity.Invalid => CheckSeverity.Invalid,
        PKHeX.Core.Severity.Fishy => CheckSeverity.Fishy,
        _ => CheckSeverity.Valid,
    };

    /// <summary>The humanized line without its "Invalid: " severity prefix (the group shows that).</summary>
    private static string StripSeverity(in LegalityLocalizationContext ctx, in CheckResult check)
    {
        var text = ctx.Humanize(check);
        var prefix = string.Format(ctx.Settings.Lines.F0_1, ctx.Settings.Description(check.Judgement), "");
        return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
    }

    private static string EncounterText(LegalityAnalysis la)
    {
        var enc = la.EncounterOriginal;
        var name = enc.GetEncounterName(Strings.specieslist);
        var location = enc.GetEncounterLocation();
        return string.IsNullOrEmpty(location) ? name : $"{name} · {location}";
    }

    internal static string TitleOf(LegalityFix fix) => fix switch
    {
        LegalityFix.MetInfo => "Suggested met data",
        LegalityFix.Ball => "Suggested ball",
        LegalityFix.EggLocation => "Suggested egg data",
        LegalityFix.RelearnMoves => "Suggested relearn moves",
        LegalityFix.CurrentMoves => "Suggested moves",
        LegalityFix.Memories => "Suggested memories",
        LegalityFix.EffortValues => "EVs within caps",
        LegalityFix.IVs => "IVs within caps",
        LegalityFix.AllSafe => "Fix all safe",
        _ => fix.ToString(),
    };

    private static string Title(CheckIdentifier id) => id switch
    {
        CheckIdentifier.CurrentMove => "Current moves",
        CheckIdentifier.RelearnMove => "Relearn moves",
        CheckIdentifier.Encounter => "Encounter / met data",
        CheckIdentifier.Shiny => "Shiny state",
        CheckIdentifier.EC => "Encryption constant",
        CheckIdentifier.PID => "Personality value",
        CheckIdentifier.EVs => "Effort values",
        CheckIdentifier.Trainer => "Original trainer",
        CheckIdentifier.Level => "Level",
        CheckIdentifier.Ball => "Poké Ball",
        CheckIdentifier.Memory => "Memories",
        CheckIdentifier.Geography => "Region data",
        CheckIdentifier.Egg => "Egg data",
        CheckIdentifier.Misc => "Other",
        CheckIdentifier.Fateful => "Fateful encounter",
        CheckIdentifier.Ribbon => "Ribbons",
        CheckIdentifier.Training => "Training",
        CheckIdentifier.GameOrigin => "Origin game",
        CheckIdentifier.HeldItem => "Held item",
        CheckIdentifier.RibbonMark => "Marks",
        CheckIdentifier.GVs => "Grit values",
        CheckIdentifier.Marking => "Markings",
        CheckIdentifier.AVs => "Awakening values",
        CheckIdentifier.TrashBytes => "Name bytes",
        CheckIdentifier.SlotType => "Storage slot",
        CheckIdentifier.Handler => "Handling trainer",
        _ => id.ToString(),
    };

    /// <summary>The player-facing field changes between two copies of the same mon.</summary>
    internal static IReadOnlyList<FieldChange> Diff(PKM a, PKM b)
    {
        var changes = new List<FieldChange>();
        void Field(string name, string before, string after)
        {
            if (!string.Equals(before, after, StringComparison.Ordinal)) changes.Add(new FieldChange(name, before, after));
        }
        string Loc(bool egg, ushort value, PKM pk) =>
            value == 0 && !egg ? "(none)" : GameInfo.GetLocationName(egg, value, pk.Format, pk.Generation, pk.Version) is { Length: > 0 } n ? n : $"#{value}";
        string Date(DateOnly? d) => d?.ToString("yyyy-MM-dd") ?? "unset";

        Field("Level", a.CurrentLevel.ToString(), b.CurrentLevel.ToString());
        Field("Met location", Loc(false, a.MetLocation, a), Loc(false, b.MetLocation, b));
        Field("Met level", a.MetLevel.ToString(), b.MetLevel.ToString());
        if (a.Format >= 4) Field("Met date", Date(a.MetDate), Date(b.MetDate));
        if (a is IGroundTile ga && b is IGroundTile gb) Field("Ground tile", ga.GroundTile.ToString(), gb.GroundTile.ToString());
        if (a is ICaughtData2 ca && b is ICaughtData2 cb) Field("Met time of day", ca.MetTimeOfDay.ToString(), cb.MetTimeOfDay.ToString());
        if (a.Format >= 4)
        {
            Field("Egg location", a.EggLocation == 0 ? "(none)" : Loc(true, a.EggLocation, a), b.EggLocation == 0 ? "(none)" : Loc(true, b.EggLocation, b));
            Field("Egg date", Date(a.EggMetDate), Date(b.EggMetDate));
        }
        if (a.Format >= 3) Field("Ball", Name(Strings.balllist, a.Ball), Name(Strings.balllist, b.Ball));
        for (var i = 0; i < 4; i++) Field($"Move {i + 1}", MoveName(a.GetMove(i)), MoveName(b.GetMove(i)));
        if (a.Format >= 6)
            for (var i = 0; i < 4; i++) Field($"Relearn {i + 1}", MoveName(a.GetRelearnMove(i)), MoveName(b.GetRelearnMove(i)));
        if (a is ITechRecord ta && b is ITechRecord tb) Field("Tech records", $"{Records(ta)} learned", $"{Records(tb)} learned");
        Field("IVs", Spread(a, iv: true), Spread(b, iv: true));
        Field("EVs", Spread(a, iv: false), Spread(b, iv: false));
        if (a is IMemoryOT oa && b is IMemoryOT ob) Field("OT memory", Memory(oa.OriginalTrainerMemory, oa.OriginalTrainerMemoryIntensity, oa.OriginalTrainerMemoryFeeling, oa.OriginalTrainerMemoryVariable),
            Memory(ob.OriginalTrainerMemory, ob.OriginalTrainerMemoryIntensity, ob.OriginalTrainerMemoryFeeling, ob.OriginalTrainerMemoryVariable));
        if (a is IMemoryHT ha && b is IMemoryHT hb) Field("Handler memory", Memory(ha.HandlingTrainerMemory, ha.HandlingTrainerMemoryIntensity, ha.HandlingTrainerMemoryFeeling, ha.HandlingTrainerMemoryVariable),
            Memory(hb.HandlingTrainerMemory, hb.HandlingTrainerMemoryIntensity, hb.HandlingTrainerMemoryFeeling, hb.HandlingTrainerMemoryVariable));
        if (a.Format >= 6)
        {
            Field("Handling trainer", Blank(a.HandlingTrainerName), Blank(b.HandlingTrainerName));
            Field("Current handler", a.CurrentHandler == 0 ? "Original trainer" : "Handling trainer", b.CurrentHandler == 0 ? "Original trainer" : "Handling trainer");
            Field("Handler friendship", a.HandlingTrainerFriendship.ToString(), b.HandlingTrainerFriendship.ToString());
        }
        return changes;
    }

    private static string Spread(PKM pk, bool iv)
    {
        Span<int> values = stackalloc int[6];
        if (iv) pk.GetIVs(values); else pk.GetEVs(values);
        // PKHeX stores speed at index 3; show the familiar HP/Atk/Def/SpA/SpD/Spe order.
        return $"{values[0]}/{values[1]}/{values[2]}/{values[4]}/{values[5]}/{values[3]}";
    }

    private static string Memory(byte memory, byte intensity, byte feeling, ushort variable) =>
        memory == 0 ? "none" : $"#{memory} (intensity {intensity}, feeling {feeling}, detail {variable})";

    private static int Records(ITechRecord records)
    {
        var count = 0;
        for (var i = 0; i < records.Permit.RecordCountUsed; i++)
            if (records.GetMoveRecordFlag(i)) count++;
        return count;
    }

    private static string Blank(string value) => value.Length == 0 ? "(none)" : value;

    internal static string MoveName(ushort move) => move == 0 ? "(none)" : Name(Strings.movelist, move);

    private static string Name(IReadOnlyList<string> names, int id) =>
        (uint)id < (uint)names.Count && names[id].Length > 0 ? names[id] : $"#{id}";

    /// <summary>The whole decrypted buffer: what Apply copies into the written entity.</summary>
    private static ReadOnlyMemory<byte> Bytes(PKM pk) => pk.Data.ToArray();

    /// <summary>The stored (box) portion: what every slot keeps, so what the stale check compares.
    /// Party-only bytes (battle stats) are recomputed by storage and would never match.</summary>
    internal static byte[] Stored(PKM pk) => pk.Data[..pk.SIZE_STORED].ToArray();

    private static LegalityFixPreview Unavailable(LegalityFix fix, int box, int slot, string reason,
        PKM? basis = null, LegalityAnalysis? la = null) =>
        new(fix, TitleOf(fix), box, slot, false, reason, [], la?.Valid ?? false, la?.Valid ?? false,
            la is null ? 0 : Problems(la), la is null ? 0 : Problems(la), basis is null ? default : Stored(basis), default);

    internal static bool TryGet(ISaveEngineSession session, int box, int slot, out SaveEngineSession engine, out PKM pk)
    {
        ArgumentNullException.ThrowIfNull(session);
        engine = null!;
        pk = null!;
        if (session is not SaveEngineSession stock || !stock.SupportsLegalityAnalysis) return false;
        engine = stock;
        pk = stock.GetEntity(box, slot);
        return true;
    }

    /// <summary>Surgical slot write, like every PKForge edit: no dex, record or handler side effects.</summary>
    internal static void Place(SaveFile save, int box, int slot, PKM pk)
    {
        if (box == -1) save.SetPartySlotAtIndex(pk, slot, EntityImportSettings.None);
        else save.SetBoxSlotAtIndex(pk, box, slot, EntityImportSettings.None);
    }
}
