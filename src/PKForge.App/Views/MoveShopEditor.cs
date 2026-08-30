using PKForge.App.Services;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>Edits the species-permitted Move Shop and mastery flags used by Legends: Arceus.</summary>
public static class MoveShopEditor
{
    public static async Task<bool> ShowAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var info = session.GetMoveShop(box, slot);
        if (!info.Supported)
        {
            await EditorMenu.ShowAsync(host, "MOVE SHOP", "Move Shop and move mastery are only stored by Legends: Arceus Pokémon.", "OK");
            return false;
        }

        var data = IPlatformApplication.Current!.Services.GetRequiredService<IGameDataService>();
        string Name(int move) => (uint)move < (uint)data.MoveNames.Count && data.MoveNames[move].Length != 0
            ? data.MoveNames[move]
            : $"#{move}";

        var dirty = false;
        while (true)
        {
            info = session.GetMoveShop(box, slot);
            var purchased = info.Entries.Count(entry => entry.Purchased);
            var mastered = info.Entries.Count(entry => entry.Mastered);
            var options = info.Entries.Select(entry => new PickItem(entry.Index,
                $"{Name(entry.Move)} · {(entry.Purchased ? "PURCHASED" : "NOT PURCHASED")} · {(entry.Mastered ? "MASTERED" : "NOT MASTERED")}"))
                .ToArray();
            var choice = await PickerMenu.ShowAsync(host, $"MOVE SHOP · {purchased} P · {mastered} M", options);
            if (choice is null)
                return dirty;

            var entry = info.Entries.FirstOrDefault(entry => entry.Index == choice.Id);
            if (entry is null)
                continue;
            if (await EditEntryAsync(host, session, box, slot, entry, Name))
                dirty = true;
        }
    }

    private static async Task<bool> EditEntryAsync(Grid host, ISaveEngineSession session, int box, int slot,
        MoveShopEntry entry, Func<int, string> getMoveName)
    {
        var purchased = entry.Purchased ? "ON" : "OFF";
        var mastered = entry.Mastered ? "ON" : "OFF";
        var choice = await EditorMenu.ShowAsync(host, getMoveName(entry.Move), null,
            new PadOption($"Purchased · {purchased}"), new PadOption($"Mastered · {mastered}"));
        if (choice is null) return false;

        if (choice.StartsWith("Purchased", StringComparison.Ordinal))
            session.ApplyMoveShopEdit(box, slot, new MoveShopEdit(entry.Index, Purchased: !entry.Purchased));
        else if (choice.StartsWith("Mastered", StringComparison.Ordinal))
            session.ApplyMoveShopEdit(box, slot, new MoveShopEdit(entry.Index, Mastered: !entry.Mastered));
        else
            return false;
        return true;
    }
}
