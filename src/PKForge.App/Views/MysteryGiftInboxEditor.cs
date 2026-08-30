using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>Read-only view of the Mystery Gift cards physically stored in a save.
/// Receiving, deleting, and received-flag mutation are intentionally not exposed.</summary>
public static class MysteryGiftInboxEditor
{
    public static async Task ShowAsync(Grid host, ISaveEngineSession session)
    {
        var inbox = session.GetMysteryGiftInbox();
        if (!inbox.Supported)
        {
            await EditorMenu.ShowAsync(host, "MYSTERY GIFT INBOX",
                "This game does not expose a supported Mystery Gift card store.", "OK");
            return;
        }
        if (inbox.Cards.Count == 0)
        {
            await EditorMenu.ShowAsync(host, "MYSTERY GIFT INBOX",
                "No Mystery Gift cards or received-gift records are stored in this save.", "OK");
            return;
        }

        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        string SpeciesName(int species) => (uint)species < (uint)data.SpeciesNames.Count && data.SpeciesNames[species].Length != 0
            ? data.SpeciesNames[species]
            : $"#{species}";

        while (true)
        {
            inbox = session.GetMysteryGiftInbox();
            var options = inbox.Cards.Select(card => new PickItem(card.Slot,
                $"{card.Title} · {CardNumber(card)}")).ToArray();
            var picked = await PickerMenu.ShowAsync(host, $"MYSTERY GIFT INBOX · {inbox.Cards.Count}", options);
            if (picked is null)
                return;
            var card = inbox.Cards.FirstOrDefault(candidate => candidate.Slot == picked.Id);
            if (card is null)
                continue;

            var kind = card.IsReceivedRecord ? "Received gift record" : "Stored Wonder Card";
            var contents = card.IsEntity && card.Species > 0
                ? $"Pokémon: {SpeciesName(card.Species)} · Lv. {card.Level}"
                : "Non-Pokémon gift or record";
            var state = card.IsReceivedRecord ? "History entry" : card.GiftUsed ? "Gift used" : "Gift unused";
            await EditorMenu.ShowAsync(host, card.Title.ToUpperInvariant(),
                $"{kind}\n{CardNumber(card)} · {card.Type}\n{contents}\n{state}\n\nRead-only: PKForge does not alter cards or received flags.", "Back");
        }
    }

    private static string CardNumber(MysteryGiftCard card) => card.CardId > 0
        ? $"Card #{card.CardId:0000}"
        : $"Slot {card.Slot + 1}";
}
