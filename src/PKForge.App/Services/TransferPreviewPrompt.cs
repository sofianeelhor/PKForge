using PKForge.App.Views;
using PKForge.Engine;

namespace PKForge.App.Services;

/// <summary>
/// The transfer preview's confirm step, shared by every send-to-game call site: the
/// conversion diff, the legality verdict, and the user's call. Sending over an
/// Illegal verdict or a backwards (downgrade) conversion is allowed - the user is the
/// boss - but never silently: every warning is listed and the primary option becomes
/// "Send anyway".
/// </summary>
public static class TransferPreviewPrompt
{
    private const int MaxChangeLines = 8;
    private const int MaxWarningLines = 8;

    /// <summary>
    /// Shows <paramref name="preview"/> and asks whether to send. True when the user
    /// picked the send option (or "Send anyway" over an illegal verdict); false when
    /// they cancelled, backed out, or the transfer cannot happen at all - the failure
    /// is explained on screen first.
    /// </summary>
    public static async Task<bool> ConfirmAsync(
        Grid host, TransferPreviewOutcome preview, string nickname, string targetLabel)
    {
        if (!preview.Success || preview.Preview is null)
        {
            await PadMenu.ShowAsync(host, "Transfer", preview.Message, "OK");
            return false;
        }

        var detail = preview.Preview;
        var lines = new List<string> { $"{nickname} → {targetLabel}", string.Empty, detail.Verdict };
        if (detail.Warnings.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add(detail.Backwards ? "Warnings (backwards transfer)" : "Warnings");
            lines.AddRange(detail.Warnings.Take(MaxWarningLines).Select(w => $"• {w}"));
            if (detail.Warnings.Count > MaxWarningLines)
                lines.Add($"… and {detail.Warnings.Count - MaxWarningLines} more warnings");
        }
        if (detail.Changes.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Changes");
            lines.AddRange(detail.Changes.Take(MaxChangeLines));
            if (detail.Changes.Count > MaxChangeLines)
                lines.Add($"… and {detail.Changes.Count - MaxChangeLines} more");
        }
        var risky = detail.Legality == TransferLegality.Illegal || detail.Backwards;
        var send = risky ? "Send anyway" : "Send";
        var choice = await PadMenu.ShowAsync(host, "Transfer preview", string.Join('\n', lines), send, "Cancel");
        return choice == send;
    }
}
