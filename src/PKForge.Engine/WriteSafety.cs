using System.Buffers.Binary;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One slot's raw bytes plus its readability, the unit of the write-safety diff.</summary>
internal readonly record struct SlotImage(SlotRef Slot, byte[] Bytes, bool Empty, bool Valid);

/// <summary>
/// The structural safety net behind <see cref="ISaveEngine.CheckWriteSafety"/> and
/// <see cref="ISaveEngine.AssessLayoutRisk"/>.
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
    /// <param name="ReadOnly">Why a hack engine that reads these bytes must not write them, or null.</param>
    private sealed record Parsed(string Route, Func<IReadOnlyList<SlotImage>> Images, SaveFile? Stock, string? ReadOnly = null);

    private static Parsed? Parse(ReadOnlyMemory<byte> bytes)
    {
        var decoded = RetroArchSaveContainer.Decode(bytes.Span);
        if (SaveParser.IsPokemonOriginHeartGold(decoded))
        {
            var session = new OriginHeartGold.OriginHeartGoldEngineSession(bytes);
            return new Parsed("Origin HeartGold", () => session.SlotImages().ToList(), null);
        }
        if (SaveParser.IsPokemonUnbound(decoded))
        {
            var session = new Unbound.UnboundEngineSession(bytes);
            return new Parsed("Unbound", () => session.SlotImages().ToList(), null);
        }
        if (SaveParser.IsPokemonGsChronicles(decoded))
        {
            var session = new GsChronicles.GsChroniclesEngineSession(bytes);
            return new Parsed("GS Chronicles", () => session.SlotImages().ToList(), null);
        }
        if (SaveParser.IsPokemonRadicalRed(decoded))
        {
            var session = new RadicalRed.RadicalRedEngineSession(bytes);
            return new Parsed("Radical Red", () => session.SlotImages().ToList(), null);
        }
        if (SaveParser.IsPokemonRechargedYellow(decoded))
        {
            var session = new RechargedYellow.RechargedYellowEngineSession(bytes);
            return new Parsed("Recharged Yellow", () => session.SlotImages().ToList(), null);
        }
        if (SaveParser.IsPokemonQuetzal(decoded))
        {
            var session = new Quetzal.QuetzalEngineSession(bytes);
            return new Parsed("Quetzal", () => session.SlotImages().ToList(), null);
        }
        if (SaveParser.IsPokemonSoulGold(decoded))
        {
            var session = new SoulGold.SoulGoldEngineSession(bytes);
            return new Parsed("Soul Gold", () => session.SlotImages().ToList(), null);
        }
        if (SaveParser.IsPokemonExpansion(decoded) && Exceeded.ExceededFormat.Matches(SramPadding.Trim(decoded)))
        {
            var session = new Exceeded.ExceededEngineSession(bytes, null);
            return new Parsed(session.GameNames[0], () => session.SlotImages().ToList(), null);
        }
        if (SaveParser.IsPokemonExpansion(decoded))
        {
            var session = new Expansion.ExpansionEngineSession(bytes, null);
            return new Parsed(session.GameNames[0], () => session.SlotImages().ToList(), null, session.ReadOnlyReason);
        }
        if (SaveParser.IsPokemonEliteRedux(decoded))
        {
            var session = new EliteRedux.EliteReduxEngineSession(bytes);
            return new Parsed("Elite Redux", () => session.SlotImages().ToList(), null, session.ReadOnlyReason);
        }
        if (SaveParser.IsPokemonFireRedHack(decoded))
        {
            var session = new FireRedHacks.FireRedHackEngineSession(bytes);
            return new Parsed(session.GameNames[0], () => session.SlotImages().ToList(), null);
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
            // Gen 3 judges emptiness on the RAW species: an expansion species id maps to
            // national 0, and treating that real Pokémon as an empty slot would let the
            // structural diff wave through an overwrite of it.
            var species = pk is G3PKM g3 ? g3.SpeciesInternal : pk.Species;
            var empty = data.All(b => b == 0) || species == 0 && pk.ChecksumValid;
            return new SlotImage(where, data, empty, empty || pk.ChecksumValid);
        }
    }

    /// <summary>Raw Gen 3 internal species ids the retail games define: 1-251, 277-411, and
    /// 412 (the egg placeholder). 252-276 are unused filler; anything above 412, or with the
    /// high bits pokeemerald-expansion packs into the field, comes from another species table.</summary>
    internal static bool IsVanillaGen3Species(ushort raw) => raw is >= 1 and <= 251 or >= 277 and <= 412;

    public static string? DescribeLayoutRisk(ReadOnlyMemory<byte> bytes) => AssessLayoutRisk(bytes)?.Reason;

    public static LayoutRisk? AssessLayoutRisk(ReadOnlyMemory<byte> bytes)
    {
        Parsed? parsed;
        try { parsed = Parse(bytes); }
        catch (InvalidDataException) { return null; }
        if (parsed?.ReadOnly is { } readOnly)
            return new LayoutRisk(LayoutRiskKind.CorruptingLayout, readOnly);
        if (parsed?.Stock is not SAV3 save)
            return null; // recognized hack sessions and non-Gen-3 formats are layout-certain
        if (UnsupportedKnownHack(bytes) is { } known)
            return new LayoutRisk(LayoutRiskKind.CorruptingLayout, known);
        if (CorruptingLayout(bytes, save) is { } corrupting)
            return new LayoutRisk(LayoutRiskKind.CorruptingLayout, corrupting);
        if (ForeignSpecies(save) is { } hack)
            return new LayoutRisk(LayoutRiskKind.SuspectedHack, hack);
        if (RetailWindowMismatch(bytes, save) is { } windows)
            return new LayoutRisk(LayoutRiskKind.SuspectedHack, windows);
        if (ForeignBag(save) is { } bag)
            return new LayoutRisk(LayoutRiskKind.SuspectedHack, bag);
        return null;
    }

    /// <summary>
    /// (0) A hack PKForge recognizes by its own structure but has no editor for (see
    /// <see cref="RecognizedHacks"/>): R.O.W.E., Fire Red Extended, a Heart &amp; Soul build
    /// in another layout… Its layout is certainly not vanilla, so it opens read-only under
    /// its own name, and writing it with the retail layout is refused outright.
    /// </summary>
    private static string? UnsupportedKnownHack(ReadOnlyMemory<byte> bytes)
    {
        try { return RecognizedHacks.Identify(RetroArchSaveContainer.Decode(bytes.Span)) is { } hack ? RecognizedHacks.ReadOnlyReason(hack) : null; }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>
    /// Bytes of each live sector the retail game itself checksums: SaveBlock2, the four
    /// SaveBlock1 chunks, then PC storage (0x83D0 bytes in every Gen 3 handheld game).
    /// </summary>
    private static int[]? RetailWindows(SAV3 save) => save switch
    {
        SAV3E => [0xF2C, 0xF80, 0xF80, 0xF80, 0xF08, .. StorageWindows],
        SAV3FRLG => [0xF24, 0xF80, 0xF80, 0xF80, 0xEE8, .. StorageWindows],
        SAV3RS => [0x890, 0xF80, 0xF80, 0xF80, 0xC40, .. StorageWindows],
        _ => null,
    };

    private static readonly int[] StorageWindows = [0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0xF80, 0x7D0];

    /// <summary>
    /// (4) A hack that keeps the retail sector envelope but grew a save block (newer
    /// pokeemerald-expansion builds, Perfect Emerald, Soul Gold) checksums a sector over
    /// more bytes than the retail game does. PKHeX checksums every sector over 0xF80, so
    /// such a save still round-trips and passes (1); but the retail game would reject that
    /// sector, so a vanilla cartridge cannot have written it. This holds even on a fresh
    /// save with no Pokémon at all, where (3) has nothing to read.
    /// </summary>
    private static string? RetailWindowMismatch(ReadOnlyMemory<byte> bytes, SAV3 save)
    {
        if (RetailWindows(save) is not { } windows)
            return null;
        byte[] payload;
        try { payload = SramPadding.Trim(RetroArchSaveContainer.Decode(bytes.Span)); }
        catch (InvalidDataException) { return null; }
        const int sectorSize = 0x1000, sectionCount = 14;
        if (ActiveRetailSlot(payload) is not { } slot)
            return null;
        var slotStart = slot * sectionCount * sectorSize;
        var mismatched = new List<int>();
        for (var index = 0; index < sectionCount; index++)
        {
            var sector = payload.AsSpan(slotStart + index * sectorSize, sectorSize);
            var id = BinaryPrimitives.ReadUInt16LittleEndian(sector[0xFF4..]);
            if (id >= sectionCount)
                return null; // not a slot this check understands; (1) and (2) already ran
            if (Checksums.CheckSum32(sector[..windows[id]]) != BinaryPrimitives.ReadUInt16LittleEndian(sector[0xFF6..]))
                mismatched.Add(id);
        }
        return mismatched.Count == 0
            ? null
            : $"section {string.Join(", ", mismatched.Order())} of this save is checksummed over more bytes than vanilla {save.Version} " +
              "writes, so a ROM hack built on its layout most likely wrote it.";
    }

    /// <summary>The slot PKHeX reads (SAV3.GetActiveSlot): the one holding all 14 ids,
    /// the newer footer when both do. Null when neither slot is complete.</summary>
    private static int? ActiveRetailSlot(byte[] payload)
    {
        const int sectorSize = 0x1000, sectionCount = 14;
        int? Zero(int slot)
        {
            var start = slot * sectionCount * sectorSize;
            if (start + sectionCount * sectorSize > payload.Length)
                return null;
            var seen = 0;
            int? zero = null;
            for (var index = 0; index < sectionCount; index++)
            {
                var offset = start + index * sectorSize;
                var id = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 0xFF4));
                if (id >= sectionCount) return null;
                seen |= 1 << id;
                if (id == 0) zero = offset;
            }
            return seen == (1 << sectionCount) - 1 ? zero : null;
        }
        var first = Zero(0);
        var second = Zero(1);
        if (first is null) return second is null ? null : 1;
        if (second is null) return 0;
        return SAV3BlockDetection.CompareFooters(payload, first.Value, second.Value);
    }

    /// <summary>
    /// (5) Bag entries the retail game cannot hold: an item id past the game's last item, an
    /// item in a pocket it never goes to, or a quantity of 0 or above 999. Expansion hacks
    /// renumber the items and repack the pockets, so their bags read as nonsense through the
    /// retail layout (Emerald Enhanced: a key item ×25091), and an empty slot whose quantity does
    /// not decode to 0 means the security key is not where the retail game keeps it. Two such entries are required, so
    /// one stray value from another editor never flags a vanilla save.
    /// </summary>
    private static string? ForeignBag(SAV3 save)
    {
        var anomalies = 0;
        try
        {
            var bag = save.Inventory;
            foreach (var pouch in bag.Pouches)
            {
                var legal = bag.Info.GetItems(pouch.Type);
                foreach (var item in pouch.Items)
                {
                    if (item.Index == 0)
                    {
                        // The game stores an empty slot as quantity 0 sealed with the save's
                        // security key, so it always decodes to 0. A hack that moved the key
                        // (Emerald Enhanced: 0 at the retail offset) leaves every empty slot
                        // decoding to its real key, even on a fresh save with an empty bag.
                        // Blank saves other tools build leave the raw word at 0 (decoding to the
                        // key itself), so only a word that is neither counts.
                        var key = pouch.Type == InventoryType.PCItems ? 0 : (int)(save.SmallBlock.SecurityKey & 0xFFFF);
                        if (item.Count != 0 && item.Count != key)
                            anomalies++;
                        continue;
                    }
                    if (item.Index > save.MaxItemID || legal.IndexOf((ushort)item.Index) < 0 || item.Count is <= 0 or > 999)
                        anomalies++;
                }
            }
        }
        catch (Exception error) when (error is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            return null;
        }
        return anomalies < 2
            ? null
            : $"{anomalies} bag entries hold items or quantities vanilla {save.Version} cannot have, " +
              "so this is most likely a ROM hack built on its layout.";
    }

    /// <summary>
    /// (3) pokeemerald-expansion hacks (Black Pearl Emerald, Emerald Enhanced, Emerald Ex)
    /// keep the vanilla sector layout and PK3 crypto, so they round-trip and every checksum
    /// holds; only the species ids betray them (Emerald Ex: 26878 &amp; 0x7FF = Sceptile,
    /// Emerald Enhanced: 875 for an Alolan Vulpix). PKHeX shows those as species 0, and any
    /// edit re-derives stats from the wrong tables, so the user must knowingly accept it.
    /// </summary>
    private static string? ForeignSpecies(SAV3 save)
    {
        var foreign = 0;
        var readable = 0;
        foreach (var pk in Mons(save))
        {
            if (!pk.ChecksumValid || pk.SpeciesInternal == 0)
                continue;
            readable++;
            if (!IsVanillaGen3Species(pk.SpeciesInternal))
                foreign++;
        }
        return foreign == 0
            ? null
            : $"{foreign} of {readable} Pokémon carry species ids that do not exist in vanilla {save.Version}, " +
              "so this is most likely a ROM hack built on its layout.";
    }

    private static IEnumerable<G3PKM> Mons(SAV3 save)
    {
        for (var slot = 0; slot < SaveParser.PartySlots(save); slot++)
            if (save.GetPartySlotAtIndex(slot) is G3PKM pk) yield return pk;
        for (var box = 0; box < save.BoxCount; box++)
        for (var slot = 0; slot < save.BoxSlotCount; slot++)
            if (save.GetBoxSlotAtIndex(box, slot) is G3PKM pk) yield return pk;
    }

    private static string? CorruptingLayout(ReadOnlyMemory<byte> bytes, SAV3 save)
    {

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
        var party = images.Where(i => i.Slot.Box == -1 && i.Slot.Slot < SaveParser.PartySlots(save) && i.Bytes.Any(b => b != 0)).ToList();
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
        // The Game Boy games keep a box as a list without gaps: taking a Pokémon out of the
        // middle moves every one after it up a slot. In a box the edit targets, slots may
        // therefore shift; what the box held outside the targeted slots must still be in it.
        var listBoxes = scope is not null && after.Stock is SAV1 or SAV2
            ? scope.Slots.Where(s => s.Box >= 0).Select(s => s.Box).ToHashSet()
            : [];
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
            var allowed = scope.Slots.Contains(old.Slot) || (old.Slot.Box == -1 && partyInScope) || listBoxes.Contains(old.Slot.Box);
            if (!allowed)
                return $"{Label(old.Slot)} changed although this edit did not target it.";
        }
        foreach (var box in listBoxes)
        {
            var held = b.Where(image => image.Slot.Box == box && !image.Empty).Select(image => Convert.ToHexString(image.Bytes)).ToList();
            foreach (var image in a.Where(image => image.Slot.Box == box && !image.Empty && !scope!.Slots.Contains(image.Slot)))
                if (!held.Remove(Convert.ToHexString(image.Bytes)))
                    return $"Box {box + 1} would lose the Pokémon of slot {image.Slot.Slot + 1}, which this edit did not target.";
        }
        return null;
    }

    private static string Label(SlotRef slot) =>
        slot.Box == -1 ? $"Party slot {slot.Slot + 1}" : $"Box {slot.Box + 1}, slot {slot.Slot + 1}";
}
