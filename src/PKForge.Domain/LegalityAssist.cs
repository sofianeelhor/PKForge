namespace PKForge.Domain;

/// <summary>
/// The repairs the legality assistant can suggest, each backed by the pinned engine's own
/// suggestion routine (the same ones PKHeX's editor buttons call).
/// </summary>
public enum LegalityFix
{
    /// <summary>Met location, met level, met date (when missing) and ground tile from
    /// EncounterSuggestion.GetSuggestedMetInfo; the ball too when the new origin rejects it.</summary>
    MetInfo,
    /// <summary>A ball the matched encounter allows (BallApplicator, colour-matched like PKHeX).</summary>
    Ball,
    /// <summary>The egg location PKHeX suggests for a hatched Pokémon or an egg.</summary>
    EggLocation,
    /// <summary>LegalityAnalysis.GetSuggestedRelearnMoves.</summary>
    RelearnMoves,
    /// <summary>The legal current moveset (MoveSetApplicator.GetMoveSet, level-up first).</summary>
    CurrentMoves,
    /// <summary>OT / handler memories that satisfy the memory checks (trade memory, cleared HT, hatch memory).</summary>
    Memories,
    /// <summary>Effort values brought back inside the format's caps (or cleared when none are allowed).</summary>
    EffortValues,
    /// <summary>IVs inside the caps, with the encounter's guaranteed perfect IVs restored.</summary>
    IVs,
    /// <summary>The fixes above combined greedily: each round keeps the suggestion that
    /// removes the most problems, and a step that does not strictly lower the total is
    /// never kept.</summary>
    AllSafe,
}

/// <summary>One field a suggestion rewrites, as the player reads it ("Met location: Route 1 → Route 3").</summary>
public sealed record FieldChange(string Field, string Before, string After);

/// <summary>
/// What applying a suggestion would change, computed on a copy: nothing is written until
/// <see cref="ILegalityAssistService.Apply"/> is called with this exact preview.
/// <see cref="Basis"/> is the decrypted stored (box) bytes the preview was computed from and
/// <see cref="Candidate"/> the full decrypted entity to write; Apply refuses when the slot's
/// stored bytes are no longer <see cref="Basis"/>.
/// </summary>
public sealed record LegalityFixPreview(
    LegalityFix Fix,
    string Title,
    int Box,
    int Slot,
    bool Available,
    string? Unavailable,
    IReadOnlyList<FieldChange> Changes,
    bool ValidBefore,
    bool ValidAfter,
    int ProblemsBefore,
    int ProblemsAfter,
    ReadOnlyMemory<byte> Basis,
    ReadOnlyMemory<byte> Candidate)
{
    /// <summary>A suggestion is safe when it changes something and adds no new problem.</summary>
    public bool Safe => Available && Changes.Count > 0 && ProblemsAfter <= ProblemsBefore;

    /// <summary>The plain-language outcome line under the change list.</summary>
    public string Outcome => !Available
        ? Unavailable ?? "No suggestion for this Pokémon."
        : ValidAfter
            ? ValidBefore ? "Stays legal." : "Makes it legal."
            : ProblemsAfter < ProblemsBefore
                ? $"Fixes {ProblemsBefore - ProblemsAfter} of {ProblemsBefore} problems; the rest need another fix."
                : ProblemsAfter > ProblemsBefore
                    ? $"Adds {ProblemsAfter - ProblemsBefore} new problem(s)."
                    : "Does not change the legality verdict.";
}

/// <summary>One learned-move slot's verdict: <paramref name="Reason"/> is how it is learned
/// ("Level Up", "TM") or why it is not.</summary>
public sealed record MoveVerdict(int Move, bool Valid, string Reason);

/// <summary>Per-slot verdicts for the four current moves and the four relearn moves.</summary>
public sealed record MoveLegality(bool Supported, IReadOnlyList<MoveVerdict> Moves, IReadOnlyList<MoveVerdict> Relearn)
{
    public static MoveLegality Unsupported { get; } = new(false, [], []);

    /// <summary>The verdict for a current move slot, or null when not analyzed.</summary>
    public MoveVerdict? Move(int index) => Supported && (uint)index < (uint)Moves.Count ? Moves[index] : null;

    /// <summary>The verdict for a relearn slot, or null when not analyzed.</summary>
    public MoveVerdict? RelearnAt(int index) => Supported && (uint)index < (uint)Relearn.Count ? Relearn[index] : null;
}

public enum CheckSeverity { Valid, Fishy, Invalid }

/// <summary>One humanized engine check line.</summary>
public sealed record LegalityCheckLine(CheckSeverity Severity, string Text);

/// <summary>
/// The checks of one kind (ball, met data, memories…) grouped under a plain title, with the
/// suggestion that addresses them when one exists.
/// </summary>
public sealed record LegalityCheckGroup(string Key, string Title, CheckSeverity Severity,
    IReadOnlyList<LegalityCheckLine> Lines, LegalityFix? Fix)
{
    public bool Valid => Severity != CheckSeverity.Invalid;
}

/// <summary>The legality report grouped by check, invalid groups first.</summary>
public sealed record LegalityFixReport(bool Supported, bool Valid, string Encounter,
    IReadOnlyList<LegalityCheckGroup> Groups)
{
    public static LegalityFixReport Unsupported { get; } =
        new(false, false, "", [new LegalityCheckGroup("unsupported", "Legality", CheckSeverity.Valid,
            [new LegalityCheckLine(CheckSeverity.Valid, "This game has no offline legality tables.")], null)]);

    /// <summary>The fixes worth offering: one per invalid group that has one, in order.</summary>
    public IReadOnlyList<LegalityFix> Fixes => Groups.Where(g => !g.Valid && g.Fix is not null)
        .Select(g => g.Fix!.Value).Distinct().ToList();
}

/// <summary>
/// The legality assistant: grouped reports, per-move verdicts and preview-first suggestions.
/// Read calls never mutate the session; <see cref="Apply"/> writes one previewed candidate
/// into the live session (the caller persists through the backed-up write path).
/// </summary>
public interface ILegalityAssistService
{
    LegalityFixReport GetReport(ISaveEngineSession session, int box, int slot);

    MoveLegality GetMoveLegality(ISaveEngineSession session, int box, int slot);

    /// <summary>Computes <paramref name="fix"/> on a copy of the mon and describes the result.</summary>
    LegalityFixPreview Preview(ISaveEngineSession session, int box, int slot, LegalityFix fix);

    /// <summary>Writes exactly the previewed candidate; refuses when the slot changed since.</summary>
    GenerationOutcome Apply(ISaveEngineSession session, LegalityFixPreview preview);
}

/// <summary>One pasted Showdown set after an offline legalization dry run.</summary>
/// <param name="Candidate">The generated entity (decrypted, party-size) ready to place; empty when generation failed.</param>
public sealed record ShowdownSetPreview(
    int Index,
    string Text,
    string Title,
    int Species,
    int Form,
    bool Shiny,
    bool Generated,
    bool Legal,
    string Verdict,
    IReadOnlyList<string> ParseProblems,
    ReadOnlyMemory<byte> Candidate);

/// <summary>What a bulk box action did: how many changed, how many were left alone and why.</summary>
public sealed record BulkOutcome(int Changed, int Skipped, IReadOnlyList<string> Notes)
{
    public GenerationOutcome ToOutcome(string verb, string noun = "Pokémon")
    {
        var tail = Skipped > 0 ? $" {Skipped} left unchanged" + (Notes.Count > 0 ? $" ({Notes[0]})" : "") + "." : "";
        return Changed > 0
            ? new GenerationOutcome(true, $"{verb} {Changed} {noun}.{tail}")
            : new GenerationOutcome(false, Skipped > 0 ? $"Nothing changed:{tail}" : "Nothing to do.");
    }
}
