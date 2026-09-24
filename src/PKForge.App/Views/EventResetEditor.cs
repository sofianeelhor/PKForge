using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Puts one-time encounters back (PKHeX SAV_EventReset1 for Red/Blue/Yellow; the Crystal GS Ball
/// event state). Only used-up events can be reset; each reset is its own restore point.
/// </summary>
public static class EventResetEditor
{
    private const string Warning =
        "Resetting lets you meet or receive it again. Each copy is legal on its own; having two of a one-time Pokémon is the only tell.";

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var events = EventResetService.GetEvents(session);
            var options = events.Select(e => new PadOption(e.Title,
                Accent: e.Done ? null : UiTokens.Green, IconPath: e.Done ? "restore" : "check",
                Detail: $"{e.Where} · {(e.Done ? "used up - can reset" : "still available")}")).ToArray();
            var choice = await EditorMenu.ShowAsync(host, "Re-arm one-time encounters", Warning, options);
            var index = Array.FindIndex(options, o => o.Label == choice);
            if (index < 0) return;
            var picked = events[index];

            var detail = $"{picked.Where}{Environment.NewLine}{(picked.Done ? "Used up." : "Still waiting in the game - nothing to reset.")}";
            if (await WorldEventsMenu.BlockedAsync(host, picked.Title, detail)) continue;
            if (!picked.Done)
            {
                await EditorMenu.ShowAsync(host, picked.Title, detail, "OK");
                continue;
            }
            if (!await EditorMenu.ConfirmAsync(host, $"Reset {picked.Title}?", detail + Environment.NewLine + Warning, "Reset"))
                continue;
            var ok = await viewModel.RunMutationAsync(s => EventResetService.Reset(s, picked.Id),
                Math.Max(0, viewModel.SelectedSlot), refreshSlot: false, action: SaveAction.EditWorld);
            if (!ok) return;
        }
    }
}
