using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>An item ready to leave the bag for the Item Bank: its key, its name, how many go,
/// and the count the bag keeps.</summary>
public sealed record ItemBankDeposit(string Key, string Name, int Moved, int Remaining);

/// <summary>
/// The open game's side of the Item Bank: which bank key each of its items has (through PKHeX's
/// Gen 1-3 item tables, never raw ids), which items may leave, and the bag edits of a deposit
/// or a withdrawal. The bank itself stays a pure <see cref="ItemBank"/>; writing it is the caller's.
/// </summary>
public static class ItemBankService
{
    public static bool IsSupported(ISaveEngineSession session) => TryGetSave(session) is not null;

    /// <summary>The game family machines keep to ("Gen5", "Gen8b"...).</summary>
    public static string Family(ISaveEngineSession session) => RequireSave(session).Context.ToString();

    /// <summary>The bank key of one of this game's items; null when the bank has no equivalent.</summary>
    public static string? KeyOf(ISaveEngineSession session, int itemId)
    {
        var save = RequireSave(session);
        if (itemId <= 0) return null;
        var english = EnglishNames(save);
        if (itemId >= english.Length || string.IsNullOrWhiteSpace(english[itemId])) return null;
        var name = english[itemId];
        if (ItemBankKeys.IsMachineName(name)) return ItemBankKeys.Machine(name, save.Context.ToString());
        var modern = ModernId(save.Context, itemId, name);
        return modern > 0 ? ItemBankKeys.Item(modern) : null;
    }

    /// <summary>Why this item may not go to the bank (key items, HMs, items with no
    /// equivalent), or null when it may.</summary>
    public static string? WhyKept(ISaveEngineSession session, string pouchName, int itemId)
    {
        var save = RequireSave(session);
        if (pouchName == nameof(InventoryType.KeyItems)) return "Key items stay with their game.";
        var english = EnglishNames(save);
        var name = itemId > 0 && itemId < english.Length ? english[itemId] ?? "" : "";
        if (ItemBankKeys.IsHiddenMachineName(name) || IsHm(save.Context, itemId)) return "HMs stay with their game.";
        if (!save.Inventory.Info.GetItems(PouchType(save, pouchName)).Contains((ushort)itemId))
            return "This game does not list it as an item its bag can hold, so it stays.";
        return KeyOf(session, itemId) is null ? "The Item Bank does not know this item." : null;
    }

    /// <summary>Where each bank key lands in this game. An item two pouches can hold (the PC's
    /// storage in Gen 2-3) goes to the bag's own pouch first.</summary>
    public static IReadOnlyDictionary<string, ItemBankPlace> Places(ISaveEngineSession session)
    {
        var save = RequireSave(session);
        var places = new Dictionary<string, ItemBankPlace>(StringComparer.Ordinal);
        var pouches = save.Inventory.Pouches.OrderBy(p => p.Type is InventoryType.PCItems or InventoryType.KeyItems ? 1 : 0);
        foreach (var pouch in pouches)
            foreach (var id in save.Inventory.Info.GetItems(pouch.Type))
                if (id > 0 && KeyOf(session, id) is { } key)
                    places.TryAdd(key, new ItemBankPlace(pouch.Type.ToString(), id));
        return places;
    }

    /// <summary>Checks a deposit without touching the bag; throws with the reason when it cannot be.</summary>
    public static ItemBankDeposit PlanDeposit(ISaveEngineSession session, string pouchName, int itemId, int amount)
    {
        if (WhyKept(session, pouchName, itemId) is { } kept) throw new InvalidOperationException(kept);
        var held = session.GetBag().FirstOrDefault(p => p.Name == pouchName)?.Items.FirstOrDefault(i => i.Id == itemId)?.Count ?? 0;
        if (held <= 0) throw new InvalidOperationException("The bag holds none.");
        var moved = Math.Clamp(amount, 1, held);
        var names = session.GetItemNames();
        var name = itemId < names.Count && names[itemId].Length > 0 ? names[itemId] : $"#{itemId}";
        return new ItemBankDeposit(KeyOf(session, itemId)!, name, moved, held - moved);
    }

    /// <summary>Deposit: the bag keeps the rest, the returned bank holds the moved items.</summary>
    public static ItemBank Deposit(ISaveEngineSession session, ItemBank bank, string pouchName, int itemId, int amount,
        out ItemBankDeposit deposit)
    {
        deposit = PlanDeposit(session, pouchName, itemId, amount);
        var banked = bank.Deposit(deposit.Key, deposit.Name, deposit.Moved, GameName(session), session.Generation);
        session.SetItemCount(pouchName, itemId, deposit.Remaining);
        return banked;
    }

    /// <summary>Withdraw: puts up to <paramref name="amount"/> in the item's pouch, within the
    /// game's own cap, and returns the bank without what went in (<paramref name="moved"/>, 0
    /// when nothing fit).</summary>
    public static ItemBank Withdraw(ISaveEngineSession session, ItemBank bank, string key, int amount, out int moved)
    {
        moved = 0;
        if (!Places(session).TryGetValue(key, out var place))
            throw new InvalidOperationException("This game has no such item.");
        var wanted = Math.Min(amount, bank.CountOf(key));
        if (wanted <= 0) return bank;
        var held = session.GetBag().FirstOrDefault(p => p.Name == place.Pouch)?.Items.FirstOrDefault(i => i.Id == place.ItemId)?.Count ?? 0;
        var stored = session.SetItemCount(place.Pouch, place.ItemId, held + wanted);
        moved = Math.Max(0, stored - held);
        return moved > 0 ? bank.Withdraw(key, moved) : bank;
    }

    private static string GameName(ISaveEngineSession session) =>
        session.GameNames.Count > 0 ? string.Join("/", session.GameNames) : $"Gen {session.Generation}";

    private static InventoryType PouchType(SaveFile save, string pouchName) =>
        save.Inventory.Pouches.FirstOrDefault(p => p.Type.ToString() == pouchName)?.Type
        ?? throw new InvalidOperationException($"No pouch named {pouchName}.");

    private static bool IsHm(EntityContext context, int itemId) => context switch
    {
        EntityContext.Gen1 => ItemConverter.IsItemHM1((ushort)itemId),
        EntityContext.Gen2 => ItemConverter.IsItemHM2((ushort)itemId),
        EntityContext.Gen3 => ItemConverter.IsItemHM3((ushort)itemId),
        EntityContext.Gen8b => false, // BDSP turned the HM slots into TMs.
        _ => ItemConverter.IsItemHM4((ushort)itemId),
    };

    /// <summary>The Gen 4+ index of an item: Gen 2 and 3 through PKHeX's tables; Gen 1, whose
    /// bag ids PKHeX does not map, through its English name.</summary>
    private static int ModernId(EntityContext context, int itemId, string englishName)
    {
        switch (context)
        {
            case EntityContext.Gen2 when itemId <= byte.MaxValue:
                var two = ItemConverter.GetItemFuture2((byte)itemId);
                return ItemConverter.IsItemTransferable34(two) ? two : ByName(englishName);
            case EntityContext.Gen3:
                var three = ItemConverter.GetItemFuture3((ushort)itemId);
                return ItemConverter.IsItemTransferable34(three) ? three : ByName(englishName);
            case EntityContext.Gen1:
            case EntityContext.Gen2:
                return ByName(englishName);
            default:
                return itemId;
        }
    }

    private static Dictionary<string, int>? _modernByName;

    private static int ByName(string englishName)
    {
        var table = _modernByName ??= BuildModernByName();
        return table.GetValueOrDefault(ItemBankKeys.NameKey(englishName));
    }

    private static Dictionary<string, int> BuildModernByName()
    {
        var names = GameInfo.GetStrings("en").GetItemStrings(EntityContext.Gen7);
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var id = 1; id < names.Length; id++)
            if (!string.IsNullOrWhiteSpace(names[id]) && !names[id].StartsWith('?'))
                table.TryAdd(ItemBankKeys.NameKey(names[id]), id);
        return table;
    }

    private static string[] EnglishNames(SaveFile save) => GameInfo.GetStrings("en").GetItemStrings(save.Context, save.Version);

    private static SaveFile? TryGetSave(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile : null;

    private static SaveFile RequireSave(ISaveEngineSession session) =>
        TryGetSave(session) ?? throw new NotSupportedException("The Item Bank needs a save PKHeX reads.");
}
