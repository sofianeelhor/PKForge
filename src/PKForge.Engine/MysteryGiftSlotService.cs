using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One Mystery Gift slot of the save, empty or not.</summary>
/// <param name="Format">The card format the slot holds (PGT, PCD, PGF, WC6, WC7, WR7).</param>
/// <param name="Used">The card was already collected from the delivery person.</param>
public sealed record GiftSlot(
    int Index,
    string Format,
    bool IsEmpty,
    int CardId,
    string Title,
    bool IsEntity,
    int Species,
    int Level,
    bool Used);

/// <summary>The save's Mystery Gift slots and the card numbers flagged "already received".</summary>
/// <param name="FlagMax">Card numbers 1 to FlagMax-1 have a received flag; 0 when the game keeps none.</param>
public sealed record GiftAlbum(int Generation, IReadOnlyList<GiftSlot> Slots, IReadOnlyList<int> ReceivedFlags, int FlagMax);

/// <summary>A gift card read from a file or the event database, checked against the open save.</summary>
/// <param name="Refusal">Why it cannot go into this save (other game/generation, unsupported card), or null.</param>
/// <param name="FreeSlot">The slot it goes into, or null when every slot of its format is taken.</param>
/// <param name="Replaceable">The slots it may replace when none is free.</param>
public sealed record GiftCardCheck(
    string Title,
    string Format,
    int CardId,
    int Species,
    int Level,
    string? Refusal,
    int? FreeSlot,
    IReadOnlyList<int> Replaceable);

/// <summary>
/// Writes, deletes and flags Mystery Gift cards in the save's own slots, so the player collects
/// them in game from the delivery person; the PKHeX.Core logic of PKHeX.WinForms SAV_Wondercard
/// (<see cref="IMysteryGiftStorage"/> / <see cref="IMysteryGiftFlags"/>):
///  - Gen 4 D/P/Pt/HG/SS (<see cref="MysteryBlock4"/>): 8 PGT slots (the gifts waiting for the
///    delivery man) and 3 PCD slots (the card album). A PCD/WC4 card goes in as its PGT gift plus
///    the card itself when the album has room; the delivery man is switched on and every PGT is
///    re-linked to its card (<see cref="MysteryBlock4.UpdateSlotPGT(ReadOnlySpan{DataMysteryGift}, bool)"/>).
///    The HG/SS Lock Capsule slot is left alone.
///  - Gen 5 B/W/B2/W2 (<see cref="MysteryBlock5"/>, PGF, re-encrypted after every write).
///  - Gen 6 X/Y/OR/AS (WC6), Gen 7 S/M/US/UM (WC7), Let's Go (WR7 records).
/// Cards are refused for another generation, or carrying a species/move/item the game lacks
/// (<see cref="MysteryUtil.IsCardCompatible"/>). Writing a card also sets its received flag, as
/// the game and PKHeX do; clearing that flag lets the event be received again.
/// </summary>
public static class MysteryGiftSlotService
{
    private const int Gen4PgtSlots = 8;

    public static bool IsSupported(ISaveEngineSession session) => Storage(session) is not null;

    private static IMysteryGiftStorage? Storage(ISaveEngineSession session) =>
        session is SaveEngineSession { SaveFile: IMysteryGiftStorageProvider provider } ? provider.MysteryGiftStorage : null;

    private static SaveFile Save(ISaveEngineSession session) => session is SaveEngineSession engine
        ? engine.SaveFile
        : throw new NotSupportedException("This save has no Mystery Gift card storage PKForge can edit.");

    public static GiftAlbum? Load(ISaveEngineSession session)
    {
        if (Storage(session) is not { } storage) return null;
        var album = Album(storage);
        var slots = album.Select((gift, index) => Describe(index, gift)).ToList();
        var flags = new List<int>();
        var max = 0;
        if (storage is IMysteryGiftFlags f)
        {
            max = f.MysteryGiftReceivedFlagMax;
            for (var i = 1; i < max; i++)
                if (f.GetMysteryGiftReceivedFlag(i)) flags.Add(i);
        }
        EndAccess(storage);
        return new GiftAlbum(Save(session).Generation, slots, flags, max);
    }

    /// <summary>Reads a gift file the way PKHeX does: by extension, else by size.</summary>
    public static DataMysteryGift? Parse(byte[] data, string fileName)
    {
        if (!MysteryGift.IsMysteryGift(data.Length)) return null;
        var ext = Path.GetExtension(fileName);
        return MysteryGift.GetMysteryGift((byte[])data.Clone(), ext) ?? MysteryGift.GetMysteryGift((byte[])data.Clone());
    }

    public static GiftCardCheck Check(ISaveEngineSession session, byte[] data, string fileName)
    {
        var gift = Parse(data, fileName);
        if (gift is null)
            return new GiftCardCheck(Path.GetFileName(fileName), "?", 0, 0, 0, "This file is not a Mystery Gift card PKForge can read.", null, []);
        var title = TitleOf(gift, Path.GetFileNameWithoutExtension(fileName));
        var species = gift.IsEntity ? gift.Species : 0;
        var level = gift.IsEntity ? gift.LevelMin : 0;
        if (Storage(session) is not { } storage)
            return new GiftCardCheck(title, gift.Type, gift.CardID, species, level, "This game has no Mystery Gift slots PKForge can write.", null, []);
        var refusal = Refusal(gift, Save(session));
        if (refusal is not null)
            return new GiftCardCheck(title, gift.Type, gift.CardID, species, level, refusal, null, []);
        var album = Album(storage);
        EndAccess(storage);
        var target = TargetFormat(gift);
        if (WrongFormat(album, target) is { } wrong)
            return new GiftCardCheck(title, gift.Type, gift.CardID, species, level, wrong, null, []);
        var free = FirstEmpty(album, target);
        var replaceable = Enumerable.Range(0, album.Length).Where(i => album[i].Type == target && !album[i].IsEmpty).ToList();
        return new GiftCardCheck(title, gift.Type, gift.CardID, species, level, null, free, replaceable);
    }

    /// <summary>Puts the card into the first free slot of its format, or into <paramref name="replace"/>.</summary>
    public static GenerationOutcome Write(ISaveEngineSession session, byte[] data, string fileName, int? replace = null)
    {
        if (Storage(session) is not { } storage)
            return new GenerationOutcome(false, "This game has no Mystery Gift slots PKForge can write.");
        if (Parse(data, fileName) is not { } gift)
            return new GenerationOutcome(false, "This file is not a Mystery Gift card PKForge can read.");
        var save = Save(session);
        if (Refusal(gift, save) is { } refusal)
            return new GenerationOutcome(false, refusal);

        var album = Album(storage);
        var target = TargetFormat(gift);
        if (WrongFormat(album, target) is { } wrong)
        {
            EndAccess(storage);
            return new GenerationOutcome(false, wrong);
        }
        var index = replace ?? FirstEmpty(album, target);
        if (index is null)
            return new GenerationOutcome(false, $"Every {target} slot is taken: delete a card or replace one.");
        if ((uint)index.Value >= (uint)album.Length || album[index.Value].Type != target)
            return new GenerationOutcome(false, $"Slot {index.Value + 1} cannot hold a {target} card.");

        var card = (DataMysteryGift)gift.Clone();
        if (card is not WR7) card.GiftUsed = false;
        if (storage is MysteryBlock4 && card is PCD pcd)
        {
            // The delivery man hands out the PGT; the PCD is the card the album shows.
            album[index.Value] = (DataMysteryGift)pcd.Gift.Clone();
            if (FirstEmpty(album, nameof(PCD)) is { } albumSlot)
                album[albumSlot] = card;
        }
        else
        {
            album[index.Value] = card;
        }
        Commit(storage, save, album);
        if (storage is IMysteryGiftFlags flags && card.CardID > 0 && card.CardID < flags.MysteryGiftReceivedFlagMax)
            flags.SetMysteryGiftReceivedFlag(card.CardID, true);
        EndAccess(storage);
        var title = TitleOf(card, $"Card #{card.CardID:0000}");
        return new GenerationOutcome(true, $"{title} is waiting in slot {index.Value + 1}: collect it from the delivery person in game.");
    }

    /// <summary>Clears a slot; later cards of the same format move up, as the game keeps them packed.</summary>
    public static GenerationOutcome Delete(ISaveEngineSession session, int slot)
    {
        if (Storage(session) is not { } storage)
            return new GenerationOutcome(false, "This game has no Mystery Gift slots.");
        var album = Album(storage);
        if ((uint)slot >= (uint)album.Length || album[slot].IsEmpty)
        {
            EndAccess(storage);
            return new GenerationOutcome(false, $"Slot {slot + 1} is already empty.");
        }
        var title = TitleOf(album[slot], $"Slot {slot + 1}");
        album[slot].Clear();
        for (var i = slot; i + 1 < album.Length && !album[i + 1].IsEmpty && album[i + 1].Type == album[i].Type; i++)
            (album[i], album[i + 1]) = (album[i + 1], album[i]);
        Commit(storage, Save(session), album);
        EndAccess(storage);
        return new GenerationOutcome(true, $"{title} deleted.");
    }

    public static GenerationOutcome SetReceivedFlag(ISaveEngineSession session, int cardId, bool value)
    {
        if (Storage(session) is not IMysteryGiftFlags flags)
            return new GenerationOutcome(false, "This game keeps no received-gift flags.");
        if (cardId < 1 || cardId >= flags.MysteryGiftReceivedFlagMax)
            return new GenerationOutcome(false, $"Card numbers go from 1 to {flags.MysteryGiftReceivedFlagMax - 1}.");
        flags.SetMysteryGiftReceivedFlag(cardId, value);
        EndAccess((IMysteryGiftStorage)flags);
        return new GenerationOutcome(true, value
            ? $"Card #{cardId:0000} marked as received."
            : $"Card #{cardId:0000} can be received again.");
    }

    private static string? Refusal(DataMysteryGift gift, SaveFile save)
    {
        if (gift.Generation != save.Generation || gift.Context != save.Context)
            return $"This is a Generation {gift.Generation} {gift.Type} card; this save is Generation {save.Generation} ({save.Version}).";
        if (gift is PCD { IsLockCapsule: true })
            return "The Lock Capsule card has its own slot; PKForge does not write it.";
        if (gift is PCD { CanConvertToPGT: false })
            return "This PCD card carries no gift the delivery man can hand out.";
        if (!gift.IsCardCompatible(save, out var message))
            return string.IsNullOrWhiteSpace(message) ? "This card is not compatible with this game." : message;
        return null;
    }

    /// <summary>The slot format a card goes into: Gen 4 PCD cards are delivered as their PGT.</summary>
    private static string TargetFormat(DataMysteryGift gift) => gift is PCD ? nameof(PGT) : gift.Type;

    private static string? WrongFormat(DataMysteryGift[] album, string format) =>
        album.Any(g => g.Type == format) ? null
            : $"This game's slots hold {album[0].Type} cards, not {format}.";

    private static DataMysteryGift[] Album(IMysteryGiftStorage storage)
    {
        var result = new DataMysteryGift[storage.GiftCountMax];
        for (var i = 0; i < result.Length; i++)
            result[i] = storage.GetMysteryGift(i);
        return result;
    }

    private static int? FirstEmpty(DataMysteryGift[] album, string format)
    {
        for (var i = 0; i < album.Length; i++)
            if (album[i].IsEmpty && album[i].Type == format) return i;
        return null;
    }

    private static void Commit(IMysteryGiftStorage storage, SaveFile save, DataMysteryGift[] album)
    {
        if (storage is MysteryBlock4 block4)
        {
            block4.IsDeliveryManActive = album.Take(Gen4PgtSlots).Any(g => !g.IsEmpty);
            MysteryBlock4.UpdateSlotPGT(album, save is SAV4HGSS);
        }
        for (var i = 0; i < album.Length; i++)
            storage.SetMysteryGift(i, album[i]);
    }

    /// <summary>B/W keeps the album encrypted at rest: close every access.</summary>
    private static void EndAccess(IMysteryGiftStorage storage)
    {
        if (storage is MysteryBlock5 block5) block5.EndAccess();
    }

    private static GiftSlot Describe(int index, DataMysteryGift gift)
    {
        if (gift.IsEmpty)
            return new GiftSlot(index, gift.Type, true, 0, "", false, 0, 0, false);
        var entity = gift.IsEntity;
        bool used;
        try { used = gift is not WR7 && gift.GiftUsed; }
        catch (Exception e) when (e is not OutOfMemoryException) { used = false; }
        return new GiftSlot(index, gift.Type, false, Math.Max(0, gift.CardID), TitleOf(gift, $"Slot {index + 1}"),
            entity, entity ? gift.Species : 0, entity ? gift.LevelMin : 0, used);
    }

    private static string TitleOf(DataMysteryGift gift, string fallback)
    {
        string title;
        try { title = gift.CardTitle.Replace('　', ' ').Trim(); }
        catch (Exception e) when (e is not OutOfMemoryException) { title = ""; }
        return string.IsNullOrWhiteSpace(title) ? fallback : title;
    }
}
