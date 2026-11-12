namespace PKForge.Domain;

/// <summary>How a status line should be shown to the player.</summary>
public enum NoticeTone
{
    /// <summary>Not worth a notice: the idle line, a control hint, or work still in progress.</summary>
    Quiet,
    Info,
    Success,
    /// <summary>Something the player asked for did not happen.</summary>
    Problem,
}

/// <summary>
/// Reads the tone of a status line. Every screen reports through one plain sentence, and a
/// screen without a readout of its own shows it as a short notice: this decides which
/// sentences deserve one, and whether it is good news or a refusal the player must not miss.
/// </summary>
public static class StatusNotice
{
    // What the screen already shows needs no notice: the marks and the box header say
    // "3 marked" and "carrying", and a Pokémon that moved is seen where it landed.
    private static readonly string[] QuietStarts =
    [
        "Ready", "Editor focus", "Box manager", "Holding box", "NO STORAGE", "MULTI-SELECT",
        "Carrying", "Put back", "MOVED", "Moved ",
    ];

    private static readonly string[] ProblemMarks =
    [
        "aborted", "refused", "cannot", "can't", "could not", "couldn't", "not enough", "no empty", "no free", "is full",
        "was full", "failed", "nothing was", "nothing to", "no save", "did not stick", "read-only", "no longer",
        "is not ", "not a ", "no room", "blocked", "mode blocks", "turned off", "went wrong",
    ];

    public static NoticeTone ToneOf(string? status) => ToneOf(status, wrote: false);

    /// <summary>
    /// The tone of <paramref name="status"/>; <paramref name="wrote"/> says the sentence reports a
    /// write that happened (a save, a deposit), so it reads as good news without having to say
    /// "restore point", and is shown even when it starts like a quiet line ("Moved 3 Pokémon to the
    /// Bank."). A refusal inside it still wins.
    /// </summary>
    public static NoticeTone ToneOf(string? status, bool wrote)
    {
        var text = status?.Trim();
        if (string.IsNullOrEmpty(text)) return NoticeTone.Quiet;
        if (!wrote)
            foreach (var start in QuietStarts)
                if (text.StartsWith(start, StringComparison.OrdinalIgnoreCase)) return NoticeTone.Quiet;
        // "No changes" is a plain answer, not a failure and not a save.
        if (text.StartsWith("Nothing changed", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("No changes", StringComparison.OrdinalIgnoreCase)) return NoticeTone.Info;
        // "Not saved. …", "Not moved. …": what did not happen leads the sentence.
        if (text.StartsWith("Not ", StringComparison.OrdinalIgnoreCase)) return NoticeTone.Problem;
        foreach (var mark in ProblemMarks)
            if (text.Contains(mark, StringComparison.OrdinalIgnoreCase)) return NoticeTone.Problem;
        if (wrote
            || text.Contains("restore point", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Saved", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Restored", StringComparison.OrdinalIgnoreCase))
            return NoticeTone.Success;
        // Work still in progress ("Applying edit…"): its outcome follows and is what matters.
        if (text.EndsWith('…') || text.EndsWith("...", StringComparison.Ordinal)) return NoticeTone.Quiet;
        return NoticeTone.Info;
    }

    /// <summary>
    /// How long a notice stays, in milliseconds: long enough to read it, no longer. "Saved." goes
    /// by in about a second and a half; a refusal stays a little longer, since it asks for action.
    /// </summary>
    public static int HoldMs(string? status, NoticeTone tone)
    {
        var length = status?.Trim().Length ?? 0;
        var read = 900 + 45 * length;
        return tone == NoticeTone.Problem
            ? Math.Clamp(read + 1200, 2800, 6000)
            : Math.Clamp(read, 1400, 4000);
    }
}
