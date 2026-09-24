using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Key-item Mystery Gift events (Eon/Aurora/Mystic Ticket, Old Sea Map, Member Card, Oak's
/// Letter, Azure Flute, Secret Key, Enigma Stone, Liberty Pass, Crystal's GS Ball): shows whether each is armed and writes the item plus the
/// exact flags/vars the distribution sets, through the one safe write path.
/// </summary>
public static class KeyItemEventsEditor
{
    private const string Title = "Key item events";

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!KeyItemEventService.IsSupported(session))
        {
            await EditorMenu.ShowAsync(host, Title,
                "Key-item events are mapped for Crystal, Ruby/Sapphire, Emerald, FireRed/LeafGreen, Diamond/Pearl, Platinum, HeartGold/SoulSilver, Black/White and Omega Ruby/Alpha Sapphire.", "OK");
            return;
        }

        var slot = Math.Max(0, viewModel.SelectedSlot);
        while (true)
        {
            var events = KeyItemEventService.GetEvents(session);
            var options = events.Select(e => new PadOption($"{e.Title} · {StateLabel(e.State)}", IconPath: "key",
                Accent: e.State switch
                {
                    KeyItemEventState.Enabled => UiTokens.Green,
                    KeyItemEventState.Partial => UiTokens.GiftRed,
                    _ => null,
                })).ToArray();
            var choice = await EditorMenu.ShowAsync(host, Title, "Item + event flags, as the real distribution writes them", options);
            if (choice is null) return;

            var selected = events.FirstOrDefault(e => choice.StartsWith($"{e.Title} ·", StringComparison.Ordinal));
            if (selected is null) continue;
            if (!await ShowEventAsync(host, selected, slot, viewModel)) return;
        }
    }

    /// <summary>False when a write was attempted and failed (status already set).</summary>
    private static async Task<bool> ShowEventAsync(Grid host, KeyItemEventStatus status, int slot, BoxBrowserViewModel viewModel)
    {
        var lines = new List<string>
        {
            $"{status.ItemName} → {status.Destination}",
            status.NeedsItem
                ? $"Item in bag: {YesNo(status.HasItem)} · Event flags: {YesNo(status.EventArmed)}"
                : $"Event flags: {YesNo(status.EventArmed)} · the {status.ItemName} is handed over in game",
            StateDetail(status.State),
        };
        if (status.Prerequisite is { } prerequisite)
            lines.Add($"Note: {prerequisite}");

        var enable = status.State is KeyItemEventState.Enabled ? null : "Enable event";
        var disable = status.HasItem || status.EventArmed ? "Disable event" : null;
        var options = new[] { enable, disable, "Back" }.OfType<string>()
            .Select(label => new PadOption(label, Accent: label switch
            {
                "Enable event" => UiTokens.Green,
                "Disable event" => UiTokens.GiftRed,
                _ => null,
            })).ToArray();
        var choice = await EditorMenu.ShowAsync(host, status.Title, string.Join("\n", lines), options);
        if (choice is not ("Enable event" or "Disable event")) return true;

        if (HardcoreMode.Blocks(SaveAction.InjectEvent, out var blocked))
        {
            await EditorMenu.ShowAsync(host, "Hardcore mode", blocked, "OK");
            return true;
        }

        var enabling = choice == "Enable event";
        var confirmed = await PadMenu.ConfirmAsync(host, enabling ? "Enable event?" : "Disable event?",
            (enabling, status.NeedsItem) switch
            {
                (true, true) => $"Adds the {status.ItemName} and sets the event flags. A restore point is created first.",
                (true, false) => "Sets the event flags. A restore point is created first.",
                (false, true) => $"Removes the {status.ItemName} and clears the event flags. Shown/caught history is kept. A restore point is created first.",
                (false, false) => "Clears the event flags. Received/caught history is kept. A restore point is created first.",
            },
            enabling ? "Enable" : "Disable");
        if (!confirmed) return true;

        var id = status.Id;
        return await viewModel.RunMutationAsync(
            s => enabling ? KeyItemEventService.Enable(s, id) : KeyItemEventService.Disable(s, id),
            slot, refreshSlot: false, action: SaveAction.InjectEvent);
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string StateLabel(KeyItemEventState state) => state switch
    {
        KeyItemEventState.Enabled => "Ready",
        KeyItemEventState.Partial => "Incomplete",
        KeyItemEventState.Used => "Used",
        KeyItemEventState.Completed => "Completed",
        _ => "Off",
    };

    private static string StateDetail(KeyItemEventState state) => state switch
    {
        KeyItemEventState.Enabled => "Ready: the event will trigger in game.",
        KeyItemEventState.Partial => "Incomplete: item and flags disagree, so the game ignores it. Enable to fix.",
        KeyItemEventState.Used => "Already used in game: the ticket was shown to the sailor, or the GS Ball was received.",
        KeyItemEventState.Completed => "The event Pokémon was already caught or defeated.",
        _ => "Not enabled.",
    };
}
