using System.Collections.Concurrent;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Adapts PKHeX.Core's embedded Mystery Gift database (every real distribution,
/// bundled offline). Gifts are matched to the open save's entity context so only
/// receivable cards are offered. Pokémon cards go into a box; item cards into the bag.
/// </summary>
public sealed class EventDatabaseService : IEventDatabaseService
{
    /// <summary>Card content hash → the gallery file it was loaded from (event name, language).</summary>
    private static readonly ConcurrentDictionary<string, string> Sources = new(StringComparer.Ordinal);

    /// <summary>
    /// Remembers which gallery file every card in <paramref name="folder"/> came from, so the
    /// album can show English event names, languages and regions. PKHeX's local tables keep
    /// only the card bytes; this keeps the file names beside them, keyed by content.
    /// </summary>
    public static void IndexArchive(string folder)
    {
        Sources.Clear();
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                if (!MysteryGift.IsMysteryGift(info.Length)) continue;
                if (MysteryGift.GetMysteryGift(File.ReadAllBytes(file), info.Extension) is not { } gift) continue;
                if (ContentKey(gift) is { } key)
                    Sources.TryAdd(key, Path.GetRelativePath(folder, file).Replace('\\', '/'));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // One unreadable file never costs the album its other names.
            }
        }
    }

    /// <summary>A content fingerprint (FNV-1a 64 + length): cheap, platform-neutral, and
    /// equal exactly when two cards carry the same bytes.</summary>
    private static string? ContentKey(MysteryGift gift)
    {
        if (gift is not DataMysteryGift data) return null;
        var bytes = data.Write();
        var hash = 14695981039346656037UL;
        foreach (var b in bytes)
            hash = (hash ^ b) * 1099511628211UL;
        return $"{bytes.Length:x}:{hash:x16}";
    }

    public IReadOnlyList<EventGift> GetGifts(ISaveEngineSession session)
    {
        if (session is not SaveEngineSession engineSession) return [];
        var strings = GameInfo.Strings;
        var result = new List<EventGift>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal); // content key → index in result
        var raw = GetRawGifts(engineSession.SaveFile);
        for (var index = 0; index < raw.Count; index++)
        {
            var gift = raw[index];
            var kind = gift.IsEntity && gift.Species > 0 ? EventGiftKind.Pokemon
                : gift.IsItem && gift.ItemID > 0 ? EventGiftKind.Item
                : (EventGiftKind?)null;
            if (kind is null) continue;

            var key = ContentKey(gift);
            var source = key is null ? null : Sources.GetValueOrDefault(key);
            var entry = new EventGift(
                index,
                gift.CardTitle.Replace('　', ' ').Trim(),
                gift.CardHeader,
                kind == EventGiftKind.Pokemon ? gift.Species : 0,
                kind == EventGiftKind.Pokemon ? gift.LevelMin : 0,
                kind == EventGiftKind.Pokemon && gift.IsShiny,
                gift.CardID,
                gift.Generation,
                CardLanguage(gift),
                CardYear(gift, source),
                kind.Value,
                Describe(gift, kind.Value, source, strings));

            // The embedded database and the gallery overlap: one card, shown once - the
            // copy that knows its gallery file wins (it carries the English event name).
            if (key is not null && seen.TryGetValue(key, out var existing))
            {
                if (result[existing].Details?.SourceFile is null && source is not null) result[existing] = entry;
                continue;
            }
            if (key is not null) seen[key] = result.Count;
            result.Add(entry);
        }
        // Pokémon cards first (the order callers have always seen), item cards after them.
        return [.. result.Where(g => g.Kind == EventGiftKind.Pokemon), .. result.Where(g => g.Kind == EventGiftKind.Item)];
    }

    /// <summary>The printed facts, resolved to English names. Every read is guarded: a
    /// malformed card loses a fact, never the album.</summary>
    private static EventGiftDetails Describe(MysteryGift gift, EventGiftKind kind, string? source, GameStrings strings)
    {
        static string Name(string[] list, int id) => id > 0 && id < list.Length ? list[id] : "";
        static T Try<T>(Func<T> read, T fallback)
        {
            try { return read(); }
            catch (Exception e) when (e is not OutOfMemoryException) { return fallback; }
        }

        var details = new EventGiftDetails
        {
            SourceFile = source,
            Format = gift.Type,
            Date = CardDate(gift, source),
            Items = kind == EventGiftKind.Item ? Try(() => ItemsOf(gift, strings), []) : [],
        };
        if (kind != EventGiftKind.Pokemon) return details;

        var ot = Try(() => gift.OriginalTrainerName?.Trim() ?? "", "");
        return details with
        {
            Form = Try(() => (int)gift.Form, 0),
            IsEgg = Try(() => gift.IsEgg, false),
            Ball = Try(() => (int)gift.Ball, 0),
            BallName = Try(() => Name(strings.balllist, gift.Ball), ""),
            HeldItem = Try(() => gift.HeldItem, 0),
            HeldItemName = Try(() => Name(strings.itemlist, gift.HeldItem), ""),
            Moves = Try(() => MovesOf(gift, strings), []),
            OriginalTrainer = ot,
            TrainerId = ot.Length == 0 ? "" : Try(() => gift.TrainerIDDisplayFormat == TrainerIDFormat.SixDigit
                ? gift.DisplayTID.ToString("000000") : gift.TID16.ToString("00000"), ""),
            MetLocation = Try(() => gift.Location == 0 ? "" :
                GameInfo.GetLocationName(false, gift.Location, gift.Generation, gift.Generation, gift.Version), ""),
        };
    }

    private static IReadOnlyList<string> MovesOf(MysteryGift gift, GameStrings strings)
    {
        var moves = gift.Moves;
        return new[] { moves.Move1, moves.Move2, moves.Move3, moves.Move4 }
            .Where(m => m > 0 && m < strings.movelist.Length)
            .Select(m => strings.movelist[m])
            .ToArray();
    }

    private static IReadOnlyList<EventGiftItem> ItemsOf(MysteryGift gift, GameStrings strings)
    {
        Func<int, int>? item = null, quantity = null;
        switch (gift)
        {
            case WC7 c: item = c.GetItem; quantity = c.GetQuantity; break;
            case WB7 c: item = c.GetItem; quantity = c.GetQuantity; break;
            case WC8 c: item = c.GetItem; quantity = c.GetQuantity; break;
            case WB8 c: item = c.GetItem; quantity = c.GetQuantity; break;
            case WA8 c: item = c.GetItem; quantity = c.GetQuantity; break;
            case WC9 c: item = c.GetItem; quantity = c.GetQuantity; break;
            case WA9 c: item = c.GetItem; quantity = c.GetQuantity; break;
        }
        string NameOf(int id) => id > 0 && id < strings.itemlist.Length && strings.itemlist[id].Length > 0 ? strings.itemlist[id] : $"Item {id}";
        if (item is null || quantity is null)
            return [new EventGiftItem(gift.ItemID, NameOf(gift.ItemID), Math.Max(1, gift.Quantity))];

        var items = new List<EventGiftItem>();
        for (var i = 0; i < 6; i++)
        {
            var id = item(i);
            if (id == 0) break;
            items.Add(new EventGiftItem(id, NameOf(id), Math.Max(1, quantity(i))));
        }
        return items;
    }

    public EventGiftSaveProfile? GetSaveProfile(ISaveEngineSession session) =>
        session is SaveEngineSession engineSession
            ? new EventGiftSaveProfile(
                engineSession.SaveFile.Generation,
                engineSession.SaveFile.Language,
                engineSession.GameNames.Count > 0 ? engineSession.GameNames[0] : engineSession.SaveFile.Version.ToString())
            : null;

    /// <summary>The card's language restriction, the way distributions expressed it: gen 5
    /// PGFs and gen 6/7 wonder cards carry a RestrictLanguage byte (0 = every language).
    /// Gen 4 cards have none and gen 8+ cards are multilingual (per-language OT slots that
    /// the receive path resolves itself), so both report 0 = never blocked by language.</summary>
    private static int CardLanguage(MysteryGift gift) => gift switch
    {
        PGF pgf => pgf.RestrictLanguage,
        WC6 wc6 => wc6.RestrictLanguage,
        WC7 wc7 => wc7.RestrictLanguage,
        _ => 0,
    };

    /// <summary>The year on the card itself, when the format stores a distribution date;
    /// gen 4 cards and gen 8+ cards carry none, so they report null (undated).</summary>
    private static int? CardYear(MysteryGift gift, string? source) => CardDate(gift, source)?.Year;

    /// <summary>PKHeX stamps today's date into cards read from .wc6full / .wc7full
    /// distribution files (the date they would be received), so those carry no real date.</summary>
    private static bool IsReceiveStamped(string? source) =>
        source is not null && (source.EndsWith(".wc6full", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith(".wc7full", StringComparison.OrdinalIgnoreCase));

    /// <summary>The printed date, or the start of the known distribution window (Switch cards).</summary>
    private static DateOnly? CardDate(MysteryGift gift, string? source)
    {
        if (IsReceiveStamped(source)) return null;
        try
        {
            return gift switch
            {
                PGF pgf => pgf.Date,
                WC6 wc6 => wc6.Date,
                WC7 wc7 => wc7.Date,
                WB7 wb7 => wb7.Date ?? (wb7.GetDistributionWindow(out var w) ? w.Start : null),
                WC8 wc8 => wc8.GetDistributionWindow(out var w) ? w.Start : null,
                WB8 wb8 => wb8.GetDistributionWindow(out var w) ? w.Start : null,
                WA8 wa8 => wa8.GetDistributionWindow(out var w) ? w.Start : null,
                WC9 wc9 => wc9.GetDistributionWindow(out var w) ? w.Start : null,
                WA9 wa9 => wa9.GetDistributionWindow(out var w) ? w.Start : null,
                _ => null,
            };
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or ArgumentException)
        {
            return null; // an impossible printed date (0/0) is simply undated
        }
    }

    public GenerationOutcome Receive(ISaveEngineSession session, int giftId, int box, int slot)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;

        var gifts = GetRawGifts(save);
        if (giftId < 0 || giftId >= gifts.Count)
            return new GenerationOutcome(false, "Unknown gift.");

        var gift = gifts[giftId];
        var created = gift.ConvertToPKM(save);
        if (created.Species == 0)
            return new GenerationOutcome(false, "This gift could not be converted for your save.");

        save.SetBoxSlotAtIndex(created, box, slot, EntityImportSettings.None);
        return new GenerationOutcome(true, "Here's your gift. Take good care of it!");
    }

    public GenerationOutcome ReceiveItems(ISaveEngineSession session, int giftId)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;
        var gifts = GetRawGifts(save);
        if (giftId < 0 || giftId >= gifts.Count || gifts[giftId] is not { IsItem: true } gift)
            return new GenerationOutcome(false, "Unknown item gift.");

        var items = ItemsOf(gift, GameInfo.Strings);
        try
        {
            var bag = save.Inventory;
            var delivered = new List<string>();
            foreach (var item in items)
            {
                var pouch = bag.Pouches.FirstOrDefault(p => bag.Info.GetItems(p.Type).Contains((ushort)item.ItemId));
                if (pouch is null) continue;
                if (pouch.GiveItem(bag, (ushort)item.ItemId, item.Quantity) < 0) continue;
                delivered.Add(item.Quantity > 1 ? $"{item.Name} ×{item.Quantity}" : item.Name);
            }
            if (delivered.Count == 0)
                return new GenerationOutcome(false, "This game's bag has no room for these items.");
            pouchCleanup(bag);
            bag.CopyTo(save);
            return new GenerationOutcome(true, $"Received {string.Join(", ", delivered)}. Check your bag!");
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException or InvalidOperationException)
        {
            return new GenerationOutcome(false, "This game's bag cannot take gift items.");
        }

        static void pouchCleanup(PlayerBag bag)
        {
            foreach (var pouch in bag.Pouches) pouch.ClearCount0();
        }
    }

    public SlotExport? ExportCard(ISaveEngineSession session, int giftId)
    {
        if (session is not SaveEngineSession engineSession) return null;
        var gifts = GetRawGifts(engineSession.SaveFile);
        if (giftId < 0 || giftId >= gifts.Count || gifts[giftId] is not DataMysteryGift gift) return null;
        return new SlotExport(gift.Write().ToArray(), SafeName($"{gift.CardID:0000} {gift.CardTitle}") + "." + gift.Extension);
    }

    public SlotExport? ExportEntity(ISaveEngineSession session, int giftId)
    {
        if (session is not SaveEngineSession engineSession) return null;
        var save = engineSession.SaveFile;
        var gifts = GetRawGifts(save);
        if (giftId < 0 || giftId >= gifts.Count || !gifts[giftId].IsEntity) return null;
        var created = gifts[giftId].ConvertToPKM(save);
        if (created.Species == 0) return null;
        var data = new byte[created.SIZE_PARTY];
        created.WriteDecryptedDataParty(data);
        return new SlotExport(data, SafeName(created.FileName), EntityBytes.FormatOf(created));
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_')).Trim();

    /// <summary>The context-correct gift table, in a stable order (indices are gift ids).</summary>
    private static IReadOnlyList<MysteryGift> GetRawGifts(SaveFile save) => save.Context switch
    {
        // MGDB is PKHeX's embedded database; EGDB holds the EventsGallery folders loaded
        // at runtime (EventArchive), which carry every language and later additions.
        EntityContext.Gen4 => [.. EncounterEvent.MGDB_G4, .. EncounterEvent.EGDB_G4],
        EntityContext.Gen5 => [.. EncounterEvent.MGDB_G5, .. EncounterEvent.EGDB_G5],
        EntityContext.Gen6 => [.. EncounterEvent.MGDB_G6, .. EncounterEvent.EGDB_G6],
        EntityContext.Gen7 => [.. EncounterEvent.MGDB_G7, .. EncounterEvent.EGDB_G7],
        EntityContext.Gen7b => [.. EncounterEvent.MGDB_G7GG, .. EncounterEvent.EGDB_G7GG],
        EntityContext.Gen8 => [.. EncounterEvent.MGDB_G8, .. EncounterEvent.EGDB_G8],
        EntityContext.Gen8a => [.. EncounterEvent.MGDB_G8A, .. EncounterEvent.EGDB_G8A],
        EntityContext.Gen8b => [.. EncounterEvent.MGDB_G8B, .. EncounterEvent.EGDB_G8B],
        EntityContext.Gen9 => [.. EncounterEvent.MGDB_G9, .. EncounterEvent.EGDB_G9],
        _ => [],
    };
}
