using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>The Mystery Gift cards stored in a save. Where the game keeps writable gift slots
/// (Gen 4 to 7, see <see cref="MysteryGiftSlotService"/>) a card can be added from the event
/// database or a gift file, deleted, and its "received" flag set or cleared; every write goes
/// through <see cref="BoxBrowserViewModel.RunMutationAsync"/> (Hardcore guard, restore point).
/// Other games keep the read-only view.</summary>
public static class MysteryGiftInboxEditor
{
    private const string Title = "Gifts in this save";
    private const int AddFromDatabase = -1;
    private const int AddFromFile = -2;
    private const int ReceivedFlags = -3;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!MysteryGiftSlotService.IsSupported(session))
        {
            await ShowReadOnlyAsync(host, session);
            return;
        }

        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        int? current = null;
        while (true)
        {
            if (MysteryGiftSlotService.Load(session) is not { } album) return;
            var items = new List<PickItem>
            {
                new(AddFromDatabase, "Add a card from the event database", IconPath: "events", Detail: "Cards made for this game"),
                new(AddFromFile, "Add a card from a file", IconPath: "import", Detail: ".pgt .pcd .wc4 .pgf .wc6 .wc7 and full variants"),
            };
            if (album.FlagMax > 0)
                items.Add(new(ReceivedFlags, $"Received flags · {album.ReceivedFlags.Count}", IconPath: "journal",
                    Detail: "Card numbers the game counts as already received"));
            items.AddRange(album.Slots.Select(slot => new PickItem(slot.Index, SlotName(slot),
                Detail: slot.IsEmpty ? null : SlotDetail(data, slot)) { Muted = slot.IsEmpty }));

            var picked = await PickerMenu.ShowAsync(host, $"{Title} · {album.Slots.Count(s => !s.IsEmpty)}/{album.Slots.Count}", items, current);
            if (picked is null) return;
            current = picked.Id;
            switch (picked.Id)
            {
                case AddFromDatabase: await AddFromDatabaseAsync(host, session, viewModel); break;
                case AddFromFile: await AddFromFileAsync(host, session, viewModel); break;
                case ReceivedFlags: await FlagsAsync(host, session, viewModel); break;
                default:
                    var slot = album.Slots.FirstOrDefault(s => s.Index == picked.Id);
                    if (slot is { IsEmpty: false }) await SlotAsync(host, viewModel, album, slot, data);
                    break;
            }
        }
    }

    private static async Task SlotAsync(Grid host, BoxBrowserViewModel viewModel, GiftAlbum album, GiftSlot slot, IGameDataService data)
    {
        const string Delete = "Delete this card";
        const string MarkReceived = "Mark as received";
        const string Unmark = "Allow receiving it again";
        var flagged = album.ReceivedFlags.Contains(slot.CardId);
        var options = new List<string> { Delete };
        if (slot.CardId > 0 && slot.CardId < album.FlagMax) options.Add(flagged ? Unmark : MarkReceived);
        options.Add("Back");
        var state = slot.Used ? "Already collected" : "Waiting for the delivery person";
        var message = $"{SlotDetail(data, slot)}\n{slot.Format} · slot {slot.Index + 1}\n{state}";
        if (slot.CardId > 0 && slot.CardId < album.FlagMax)
            message += flagged ? "\nCard number flagged as received." : "\nCard number not flagged as received.";
        var choice = await EditorMenu.ShowAsync(host, slot.Title, message, [.. options]);
        if (choice == Delete)
        {
            if (await EditorMenu.ShowAsync(host, "Delete card?", $"{slot.Title} leaves slot {slot.Index + 1}. Restore points can undo it.", "Delete", "Cancel") == "Delete")
                await WriteAsync(host, viewModel, s => MysteryGiftSlotService.Delete(s, slot.Index));
        }
        else if (choice is MarkReceived or Unmark)
        {
            await WriteAsync(host, viewModel, s => MysteryGiftSlotService.SetReceivedFlag(s, slot.CardId, choice == MarkReceived));
        }
    }

    private static async Task FlagsAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (MysteryGiftSlotService.Load(session) is { } album)
        {
            if (album.ReceivedFlags.Count == 0)
            {
                await EditorMenu.ShowAsync(host, "Received flags",
                    "No card number is flagged as received. Open a stored card to flag its number.", "OK");
                return;
            }
            var items = album.ReceivedFlags.Select(id => new PickItem(id, $"Card #{id:0000}", Detail: "Pick to allow receiving it again")).ToArray();
            var picked = await PickerMenu.ShowAsync(host, $"Received flags · {items.Length}", items);
            if (picked is null) return;
            await WriteAsync(host, viewModel, s => MysteryGiftSlotService.SetReceivedFlag(s, picked.Id, false));
        }
    }

    private static async Task AddFromDatabaseAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        var service = IPlatformApplication.Current?.Services.GetService<IEventDatabaseService>();
        if (service is null) return;
        var gifts = await Task.Run(() => service.GetGifts(session));
        if (gifts.Count == 0)
        {
            await EditorMenu.ShowAsync(host, Title, "The event database has no card for this game.", "OK");
            return;
        }
        var items = gifts.Select(g => new PickItem(g.Id, g.Title,
            Detail: g.CardId > 0 ? $"Card #{g.CardId:0000}{(g.Year is { } y ? $" · {y}" : "")}" : g.Header)).ToArray();
        var picked = await PickerMenu.ShowAsync(host, "Which card?", items);
        if (picked is null) return;
        var export = await Task.Run(() => service.ExportCard(session, picked.Id));
        if (export is null)
        {
            await EditorMenu.ShowAsync(host, Title, "This card could not be read.", "OK");
            return;
        }
        await AddAsync(host, session, viewModel, export.Data, export.FileName);
    }

    private static async Task AddFromFileAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        // Gift extensions have no registered MIME type on Android: any file, checked on read.
        var pick = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Pick a Mystery Gift card file" });
        if (pick is null) return;
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(pick.FullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            await EditorMenu.ShowAsync(host, Title, "That file could not be read.", "OK");
            return;
        }
        await AddAsync(host, session, viewModel, bytes, pick.FileName);
    }

    private static async Task AddAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, byte[] bytes, string fileName)
    {
        var check = await Task.Run(() => MysteryGiftSlotService.Check(session, bytes, fileName));
        if (check.Refusal is { } refusal)
        {
            await EditorMenu.ShowAsync(host, check.Title, refusal, "OK");
            return;
        }
        int? replace = null;
        if (check.FreeSlot is null)
        {
            if (check.Replaceable.Count == 0)
            {
                await EditorMenu.ShowAsync(host, check.Title, $"No {check.Format} slot can take this card.", "OK");
                return;
            }
            var album = MysteryGiftSlotService.Load(session);
            var options = check.Replaceable.Select(i => new PickItem(i,
                album?.Slots.ElementAtOrDefault(i) is { } s ? SlotName(s) : $"Slot {i + 1}")).ToArray();
            var picked = await PickerMenu.ShowAsync(host, "Every slot is taken: replace which card?", options);
            if (picked is null) return;
            replace = picked.Id;
        }
        var what = check.CardId > 0 ? $"Card #{check.CardId:0000} · {check.Format}" : check.Format;
        var where = replace is { } r ? $"It replaces the card in slot {r + 1}." : $"It goes into slot {check.FreeSlot!.Value + 1}.";
        if (await EditorMenu.ShowAsync(host, check.Title, $"{what}\n{where}\nRestore points can undo it.", "Add", "Cancel") != "Add")
            return;
        await WriteAsync(host, viewModel, s => MysteryGiftSlotService.Write(s, bytes, fileName, replace));
    }

    /// <summary>One write: Hardcore guard, restore point and atomic save write; the outcome is shown.</summary>
    private static async Task WriteAsync(Grid host, BoxBrowserViewModel viewModel, Func<ISaveEngineSession, GenerationOutcome> edit)
    {
        if (await WorldEventsMenu.BlockedAsync(host, Title, "The save's gift slots are read-only here.", SaveAction.InjectEvent))
            return;
        GenerationOutcome? outcome = null;
        var ok = await viewModel.RunMutationAsync(s => outcome = edit(s), Math.Max(0, viewModel.SelectedSlot),
            refreshSlot: false, action: SaveAction.InjectEvent);
        await EditorMenu.ShowAsync(host, Title, ok ? outcome?.Message : viewModel.Status, "OK");
    }

    private static string SlotName(GiftSlot slot) => slot.IsEmpty
        ? $"Slot {slot.Index + 1} · empty {slot.Format}"
        : $"Slot {slot.Index + 1} · {slot.Title}";

    private static string SlotDetail(IGameDataService data, GiftSlot slot)
    {
        var number = slot.CardId > 0 ? $"Card #{slot.CardId:0000}" : slot.Format;
        return slot.IsEntity && slot.Species > 0
            ? $"{number} · {SpeciesName(data, slot.Species)} Lv. {slot.Level}"
            : $"{number} · item or other gift";
    }

    private static string SpeciesName(IGameDataService data, int species) =>
        (uint)species < (uint)data.SpeciesNames.Count && data.SpeciesNames[species].Length != 0
            ? data.SpeciesNames[species]
            : $"#{species}";

    /// <summary>Games without writable gift slots: the stored cards and records, to read.</summary>
    private static async Task ShowReadOnlyAsync(Grid host, ISaveEngineSession session)
    {
        var inbox = session.GetMysteryGiftInbox();
        if (!inbox.Supported)
        {
            await EditorMenu.ShowAsync(host, "Mystery gift inbox",
                "This game does not expose a supported Mystery Gift card store.", "OK");
            return;
        }
        if (inbox.Cards.Count == 0)
        {
            await EditorMenu.ShowAsync(host, "Mystery gift inbox",
                "No Mystery Gift cards or received-gift records are stored in this save.", "OK");
            return;
        }

        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        while (true)
        {
            inbox = session.GetMysteryGiftInbox();
            var options = inbox.Cards.Select(card => new PickItem(card.Slot,
                $"{card.Title} · {CardNumber(card)}")).ToArray();
            var picked = await PickerMenu.ShowAsync(host, $"Mystery gift inbox · {inbox.Cards.Count}", options);
            if (picked is null)
                return;
            var card = inbox.Cards.FirstOrDefault(candidate => candidate.Slot == picked.Id);
            if (card is null)
                continue;

            var kind = card.IsReceivedRecord ? "Received gift record" : "Stored Wonder Card";
            var contents = card.IsEntity && card.Species > 0
                ? $"Pokémon: {SpeciesName(data, card.Species)} · Lv. {card.Level}"
                : "Non-Pokémon gift or record";
            var state = card.IsReceivedRecord ? "History entry" : card.GiftUsed ? "Gift used" : "Gift unused";
            await EditorMenu.ShowAsync(host, card.Title,
                $"{kind}\n{CardNumber(card)} · {card.Type}\n{contents}\n{state}\n\nThis game's gift store is read-only in PKForge.", "Back");
        }
    }

    private static string CardNumber(MysteryGiftCard card) => card.CardId > 0
        ? $"Card #{card.CardId:0000}"
        : $"Slot {card.Slot + 1}";
}
