using PKForge.App.Theme;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// The editors' "Suggest" flow, PKHeX-style but preview-first: the suggestion is computed
/// on a copy off the UI thread, every field it rewrites is listed as "before → after" with
/// the legality outcome, and only "Apply" writes the previewed bytes into the live session.
/// Callers run inside the sub-editor door (Hardcore-guarded) and persist afterwards through
/// the backed-up write, so every applied suggestion leaves a restore point.
/// </summary>
public static class LegalityAssistUi
{
    /// <summary>The registered assistant, or the engine's shared one outside the MAUI host.</summary>
    public static ILegalityAssistService Assist =>
        IPlatformApplication.Current?.Services.GetService<ILegalityAssistService>() ?? LegalityAssistService.Shared;

    /// <summary>Previews <paramref name="fix"/> and applies it on confirmation. True when written.</summary>
    public static async Task<bool> OfferAsync(Grid host, ISaveEngineSession session, int box, int slot, LegalityFix fix)
    {
        if (!session.SupportsLegalityAnalysis)
        {
            await EditorMenu.ShowAsync(host, "Suggest", "This game has no offline legality tables, so there is nothing to suggest.", "OK");
            return false;
        }

        var overlay = LoadingOverlay.Show(host, "Suggesting…", "Checking the legal options offline.");
        LegalityFixPreview preview;
        try { preview = await Task.Run(() => Assist.Preview(session, box, slot, fix)); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            overlay.Close();
            await EditorMenu.ShowAsync(host, "Suggest", error.Message, "OK");
            return false;
        }
        overlay.Close();
        return await ConfirmAsync(host, session, preview);
    }

    /// <summary>Shows an already computed preview and applies it on confirmation.</summary>
    public static async Task<bool> ConfirmAsync(Grid host, ISaveEngineSession session, LegalityFixPreview preview)
    {
        if (!preview.Available)
        {
            await EditorMenu.ShowAsync(host, preview.Title, preview.Unavailable ?? "No suggestion.", "OK");
            return false;
        }

        var applyLabel = preview.Safe ? "Apply" : "Apply anyway";
        var choice = await EditorMenu.ShowAsync(host, preview.Title, Describe(preview),
            new PadOption(applyLabel, Glyph: preview.Safe ? "●" : "!", Accent: preview.Safe ? UiTokens.Green : UiTokens.Warn,
                Detail: preview.Outcome),
            new PadOption("Cancel", Detail: "Nothing is changed"));
        if (choice != applyLabel) return false;

        var outcome = Assist.Apply(session, preview);
        if (!outcome.Success)
            await EditorMenu.ShowAsync(host, preview.Title, outcome.Message, "OK");
        return outcome.Success;
    }

    /// <summary>"Met location: Route 1 → Route 3" lines, capped so the window stays on screen.</summary>
    public static string Describe(LegalityFixPreview preview)
    {
        const int maxLines = 9;
        var lines = preview.Changes.Take(maxLines).Select(c => $"{c.Field}: {c.Before} → {c.After}").ToList();
        if (preview.Changes.Count > maxLines) lines.Add($"…and {preview.Changes.Count - maxLines} more");
        lines.Add("");
        lines.Add(preview.Outcome);
        return string.Join('\n', lines);
    }

    /// <summary>The move verdicts for a slot, or unsupported when the game has no tables (never throws).</summary>
    public static async Task<MoveLegality> MoveLegalityAsync(ISaveEngineSession session, int box, int slot)
    {
        if (!session.SupportsLegalityAnalysis) return MoveLegality.Unsupported;
        try { return await Task.Run(() => Assist.GetMoveLegality(session, box, slot)); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return MoveLegality.Unsupported;
        }
    }

    /// <summary>Row accent for a move verdict: green legal, red not legal, the type colour when unknown.</summary>
    public static Color? VerdictColor(MoveVerdict? verdict, Color? fallback) =>
        verdict is null || verdict.Move == 0 ? fallback : verdict.Valid ? UiTokens.Green : UiTokens.Bad;

    /// <summary>The verdict tail for a move row: "Legal · Level Up" or "Not legal · Invalid Move".</summary>
    public static string VerdictText(MoveVerdict? verdict) =>
        verdict is null || verdict.Move == 0 ? "" : verdict.Valid ? $"Legal · {verdict.Reason}" : $"Not legal · {verdict.Reason}";
}

/// <summary>
/// The legality report as a fix list: checks grouped by kind with plain-language lines,
/// the failing groups first, each with the suggestion that addresses it, and "Fix all safe"
/// on top. Every fix previews before it writes. Mutates the live session; the caller
/// persists (restore point) when this returns true.
/// </summary>
public static class LegalityReportEditor
{
    private const string FixAll = "Fix all safe";

    public static async Task<bool> ShowAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            LegalityFixReport report;
            try { report = await Task.Run(() => LegalityAssistUi.Assist.GetReport(session, box, slot)); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
            {
                await EditorMenu.ShowAsync(host, "Legality", error.Message, "OK");
                return dirty;
            }

            var options = new List<PadOption>();
            var fixable = report.Fixes.Count;
            if (!report.Valid && fixable > 0)
                options.Add(new PadOption(FixAll, Glyph: "●", Accent: UiTokens.Green,
                    Detail: $"Combines the {fixable} suggestion(s) below, keeping only steps that remove problems"));
            foreach (var group in report.Groups)
                options.Add(GroupOption(group));

            var verdict = !report.Supported ? "Not analyzed" : report.Valid ? "Legal" : "Not legal";
            var message = report.Encounter.Length > 0 ? $"{verdict} · matched: {report.Encounter}" : verdict;
            var choice = await EditorMenu.ShowAsync(host, "Legality report", message, options.ToArray());
            if (choice is null) return dirty;

            if (choice == FixAll)
            {
                if (await LegalityAssistUi.OfferAsync(host, session, box, slot, LegalityFix.AllSafe)) dirty = true;
                continue;
            }
            var picked = report.Groups.FirstOrDefault(g => g.Title == choice);
            if (picked is null) continue;
            if (picked.Fix is { } fix && !picked.Valid)
            {
                if (await LegalityAssistUi.OfferAsync(host, session, box, slot, fix)) dirty = true;
            }
            else
            {
                await EditorMenu.ShowAsync(host, picked.Title, string.Join('\n', picked.Lines.Select(Line)), "OK");
            }
        }
    }

    private static PadOption GroupOption(LegalityCheckGroup group)
    {
        var (glyph, accent) = group.Severity switch
        {
            CheckSeverity.Invalid => ("!", UiTokens.Bad),
            CheckSeverity.Fishy => ("?", UiTokens.Warn),
            _ => ("●", UiTokens.Green),
        };
        var first = group.Lines.Count == 0 ? "" : group.Lines[0].Text;
        var more = group.Lines.Count > 1 ? $" (+{group.Lines.Count - 1} more)" : "";
        var action = group.Fix is not null && !group.Valid ? $" · A: {FixName(group.Fix.Value)}" : "";
        return new PadOption(group.Title, Glyph: glyph, Accent: accent, Detail: first + more + action);
    }

    private static string Line(LegalityCheckLine line) => line.Severity switch
    {
        CheckSeverity.Invalid => $"✗ {line.Text}",
        CheckSeverity.Fishy => $"? {line.Text}",
        _ => $"✓ {line.Text}",
    };

    private static string FixName(LegalityFix fix) => fix switch
    {
        LegalityFix.MetInfo => "suggest met data",
        LegalityFix.Ball => "suggest a ball",
        LegalityFix.EggLocation => "suggest egg data",
        LegalityFix.RelearnMoves => "suggest relearn moves",
        LegalityFix.CurrentMoves => "suggest moves",
        LegalityFix.Memories => "suggest memories",
        LegalityFix.EffortValues => "cap EVs",
        LegalityFix.IVs => "fix IVs",
        _ => "fix",
    };
}
