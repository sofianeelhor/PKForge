using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One slot's raw bytes plus its readability, the unit of the write-safety diff.</summary>
internal readonly record struct SlotImage(SlotRef Slot, byte[] Bytes, bool Empty, bool Valid);

/// <summary>
/// The structural safety net behind <see cref="ISaveEngine.CheckWriteSafety"/> and
/// <see cref="ISaveEngine.DescribeLayoutRisk"/>.
///
/// Incident it exists for (user report: "raised one of my Pokémon to level 100, made all
/// my party Pokémon appear to come from eggs, and lost one of my Pokémon boxes"): a CFRU
/// ROM-hack save (Radical Red / Unbound family) that the structural detectors do not
/// match falls through to stock PKHeX as vanilla FireRed. PKHeX then reads the CFRU
/// plaintext party as encrypted retail PK3 - every party mon fails its checksum (shown
/// as bad eggs), EXP decodes as garbage (level 100) - and any write re-checksums the live
/// sections over the VANILLA windows (0xF80 instead of CFRU's 0xFF0 etc.), so the game
/// rejects those sectors (party, misc, PC) and the box data is lost. Measured on the real
/// Radical Red sample forced down the vanilla route: 6/6 party mons checksum-invalid,
/// levels 91/85/1/100/100/91, and an untouched open-&gt;write rewrote the checksum of 7
/// live sectors plus sector 30's footer.
/// </summary>
internal static class WriteSafety
{
    private sealed record Parsed(string Route, Func<IReadOnlyList<SlotImage>> Images, SaveFile? Stock);

    private static Parsed? Parse(ReadOnlyMemory<byte> bytes)
    {
        var decoded = RetroArchSaveContainer.Decode(bytes.Span);
        if (SaveParser.IsPokemonUnbound(decoded))
        {
            var session = new Unbound.UnboundEngineSession(bytes);
            return new Parsed("Unbound", () => session.SlotImages().ToList(), null);
        }
        if (SaveParser.IsPokemonRadicalRed(decoded))
        {
            var session = new RadicalRed.RadicalRedEngineSession(bytes);
            return new Parsed("Radical Red", () => session.SlotImages().ToList(), null);
        }
        if (!SaveParser.TryGetSaveFile(bytes.ToArray(), out var save) || save is null)
            return null;
        return new Parsed($"{save.GetType().Name} (Gen {save.Generation})", () => StockImages(save), save);
    }

    private static List<SlotImage> StockImages(SaveFile save)
    {
        var images = new List<SlotImage>();
        if (save.HasParty)
        {
            for (var slot = 0; slot < 6; slot++)
            {
                var pk = save.GetPartySlotAtIndex(slot);
                images.Add(Image(new SlotRef(-1, slot), pk, pk.Data.ToArray()));
            }
        }
        if (save.HasBox)
        {
            for (var box = 0; box < save.BoxCount; box++)
            for (var slot = 0; slot < save.BoxSlotCount; slot++)
            {
                var pk = save.GetBoxSlotAtIndex(box, slot);
                images.Add(Image(new SlotRef(box, slot), pk, pk.Data.ToArray()));
            }
        }
        return images;

        static SlotImage Image(SlotRef where, PKM pk, byte[] data)
        {
            var empty = data.All(b => b == 0) || pk.Species == 0 && pk.ChecksumValid;
            return new SlotImage(where, data, empty, empty || pk.ChecksumValid);
        }
    }

    public static string? DescribeLayoutRisk(ReadOnlyMemory<byte> bytes)
    {
        Parsed? parsed;
        try { parsed = Parse(bytes); }
        catch (InvalidDataException) { return null; }
        if (parsed?.Stock is not SAV3 save)
            return null; // recognized hack sessions and non-Gen-3 formats are layout-certain

        // (1) Vanilla PKHeX must reproduce the file exactly. A save whose sector checksums
        // only hold under another layout (CFRU windows, expansion save blocks) is rewritten
        // by the very first write, which is how the incident destroyed a PC box.
        using (var session = new SaveEngineSession(bytes))
        {
            if (!session.ValidateUnchangedRoundTrip())
                return $"this save does not round-trip as vanilla {save.Version}: its sector checksums follow a different layout, " +
                       "so it is most likely a ROM hack PKForge does not recognize, and writing it as vanilla would corrupt it.";
        }

        // (2) Retail Gen 3 mons always carry a valid checksum (bad eggs are the only
        // exception). Most of them failing means the Pokémon are in a foreign format.
        var images = StockImages(save);
        var party = images.Where(i => i.Slot.Box == -1 && i.Slot.Slot < save.PartyCount && i.Bytes.Any(b => b != 0)).ToList();
        if (party.Count > 0 && party.Count(i => !i.Valid) * 2 > party.Count)
            return $"{party.Count(i => !i.Valid)} of {party.Count} party Pokémon do not decode as vanilla {save.Version} data, " +
                   "so this is most likely a ROM hack PKForge does not recognize.";
        var boxes = images.Where(i => i.Slot.Box >= 0 && !i.Empty).ToList();
        if (boxes.Count >= 3 && boxes.Count(i => !i.Valid) * 2 > boxes.Count)
            return $"{boxes.Count(i => !i.Valid)} of {boxes.Count} PC Pokémon do not decode as vanilla {save.Version} data, " +
                   "so this is most likely a ROM hack PKForge does not recognize.";
        return null;
    }

    public static string? CheckWriteSafety(ReadOnlyMemory<byte> original, ReadOnlyMemory<byte> candidate, WriteScope? scope)
    {
        if (scope is { Unrestricted: true })
            return null;

        Parsed? before, after;
        try
        {
            before = Parse(original);
            after = Parse(candidate);
        }
        catch (InvalidDataException error)
        {
            return $"the save could not be re-read for the safety check ({error.Message}).";
        }
        if (before is null)
            return null; // nothing to compare against (not an engine-readable baseline)
        if (after is null)
            return "the new bytes are no longer a readable save.";
        if (before.Route != after.Route)
            return $"the write would turn a {before.Route} save into a {after.Route} one.";

        IReadOnlyList<SlotImage> a, b;
        try
        {
            a = before.Images();
            b = after.Images();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return $"the Pokémon slots could not be compared ({error.Message}).";
        }
        if (a.Count != b.Count)
            return "the number of Pokémon slots changed.";

        var partyInScope = scope?.Slots.Any(s => s.Box == -1) == true;
        for (var i = 0; i < a.Count; i++)
        {
            var old = a[i];
            var now = b[i];
            if (old.Valid && !old.Empty && !now.Valid)
                return $"{Label(old.Slot)} held a readable Pokémon that the new bytes corrupt.";
            if (scope is null)
                continue;
            var changed = old.Empty != now.Empty || (!(old.Empty && now.Empty) && !old.Bytes.AsSpan().SequenceEqual(now.Bytes));
            if (!changed)
                continue;
            var allowed = scope.Slots.Contains(old.Slot) || (old.Slot.Box == -1 && partyInScope);
            if (!allowed)
                return $"{Label(old.Slot)} changed although this edit did not target it.";
        }
        return null;
    }

    private static string Label(SlotRef slot) =>
        slot.Box == -1 ? $"Party slot {slot.Slot + 1}" : $"Box {slot.Box + 1}, slot {slot.Slot + 1}";
}
