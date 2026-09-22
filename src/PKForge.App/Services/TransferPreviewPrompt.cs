using PKForge.App.Views;
using PKForge.Engine;

namespace PKForge.App.Services;

/// <summary>
/// The transfer preview's confirm step, shared by every send-to-game call site: the
/// conversion diff, the legality verdict, and the user's call. Sending over an
/// Illegal verdict is allowed - the user is the boss - but never silently: the
/// primary option becomes "Send anyway".
/// </summary>
public static class TransferPreviewPrompt
{
    private const int MaxChangeLines = 8;

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
            await PadMenu.ShowAsync(host, "TRANSFER", preview.Message, "OK");
            return false;
        }

        var detail = preview.Preview;
        var lines = new List<string> { $"{nickname} → {targetLabel}", string.Empty, detail.Verdict };
        lines.AddRange(detail.Changes.Take(MaxChangeLines));
        if (detail.Changes.Count > MaxChangeLines)
            lines.Add($"… and {detail.Changes.Count - MaxChangeLines} more");
        var send = detail.Legality == TransferLegality.Illegal ? "Send anyway" : "Send";
        var choice = await PadMenu.ShowAsync(host, "TRANSFER PREVIEW", string.Join('\n', lines), send, "Cancel");
        return choice == send;
    }
}
