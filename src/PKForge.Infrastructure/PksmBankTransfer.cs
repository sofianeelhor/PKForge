using System.IO.Compression;
using System.Security.Cryptography;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>What the engine made of one PKSM mon: bank-ready bytes plus facts, or why not.</summary>
public sealed record PksmDecoded(byte[]? Bytes, BankEntryInfo? Info, string? Reason)
{
    public static PksmDecoded Skip(string reason) => new(null, null, reason);
}

/// <summary>
/// Turns PKSM mon bytes into bank bytes. <paramref name="generation"/> is PKSM's tag, or null
/// for a loose file whose format must be detected from its content. Supplied by the engine
/// (PKHeX lives there) so this layer stays engine-free and testable.
/// </summary>
public delegate PksmDecoded PksmEntityDecoder(PksmGeneration? generation, byte[] entityBytes, string sourceName);

/// <summary>What the engine made of one bank mon for PKSM: a tag plus payload, or why not.</summary>
public sealed record PksmEncoded(PksmGeneration Generation, byte[]? Data, string? Reason)
{
    public static PksmEncoded Skip(string reason) => new(PksmGeneration.Unused, null, reason);
}

public delegate PksmEncoded PksmEntityEncoder(byte[] bankBytes);

/// <summary>One mon found in an import source, ready or skipped with a reason.</summary>
/// <param name="Box">Box in the source bank; -1 for loose files (they have no layout).</param>
public sealed record PksmImportItem(int Box, int Slot, string Label, byte[]? Bytes, BankEntryInfo? Info, string? SkipReason)
{
    public bool Ready => Bytes is not null && Info is not null;
}

/// <summary>
/// One import source (a .bnk with its names, or a batch of loose files) decoded but not yet
/// written: what the preview shows and what <see cref="PksmBankTransfer.Apply"/> writes.
/// </summary>
public sealed record PksmImportPlan(
    string Source,
    int SourceVersion,
    bool Truncated,
    IReadOnlyList<string> BoxNames,
    IReadOnlyList<PksmImportItem> Items)
{
    public bool IsBank => SourceVersion >= 0;
    public int ReadyCount => Items.Count(i => i.Ready);
    public int SkippedCount => Items.Count(i => !i.Ready);

    /// <summary>Boxes the source actually uses: through the last box holding a ready mon.</summary>
    public int UsedBoxes => IsBank
        ? Items.Where(i => i.Ready).Select(i => i.Box + 1).DefaultIfEmpty(0).Max()
        : (ReadyCount + PksmBankFile.SlotsPerBox - 1) / PksmBankFile.SlotsPerBox;

    /// <summary>"Gen 4" → count, over ready mons, in generation order.</summary>
    public IReadOnlyList<(string Label, int Count)> CountsByGeneration => Items
        .Where(i => i.Ready).GroupBy(i => i.Label)
        .OrderBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => (g.Key, g.Count())).ToArray();

    /// <summary>Skip reasons with how many mons each one covers, most common first.</summary>
    public IReadOnlyList<(string Reason, int Count)> SkipReasons => Items
        .Where(i => !i.Ready).GroupBy(i => i.SkipReason ?? "unreadable")
        .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => (g.Key, g.Count())).ToArray();
}

public enum PksmImportMode
{
    /// <summary>Each source bank lands in fresh boxes after the bank's last used box, same box/slot layout and box names.</summary>
    NewBoxes,
    /// <summary>Mons fill the bank's first free slots; the source layout is not kept.</summary>
    Merge,
}

public sealed record PksmImportResult(int Imported, int Duplicates, int FirstNewBox, int BoxesUsed);

public enum PksmExportLayout
{
    /// <summary>Box/slot positions are kept, boxes counted from the first exported box.</summary>
    KeepPositions,
    /// <summary>Mons are packed into consecutive slots from box 1 (for scattered selections).</summary>
    Pack,
}

public sealed record PksmExportResult(
    byte[] Bank,
    byte[] BoxNames,
    int Boxes,
    int Written,
    IReadOnlyList<(BankEntry Entry, string Reason)> Skipped);

/// <summary>Files gathered from a pick: banks (paired with their names) and loose mon files.</summary>
public sealed record PksmImportSources(
    IReadOnlyList<(string Name, byte[] Bank, byte[]? BoxNames)> Banks,
    IReadOnlyList<(string Name, byte[] Bytes)> LooseFiles,
    IReadOnlyList<string> Ignored);

/// <summary>
/// PKSM ↔ PKForge bank transfer: plan an import from a .bnk (+ .json names), a PKSM "dumps"
/// zip or loose .pk files, apply it to the bank, and build a PKSM bank from bank entries.
/// Byte-level layout lives in <see cref="PksmBankFile"/>; entity conversion is the engine's
/// (<see cref="PksmEntityDecoder"/> / <see cref="PksmEntityEncoder"/>).
/// </summary>
public static class PksmBankTransfer
{
    /// <summary>Help copy: where the files live on the 3DS and how to bring a bank back.</summary>
    public const string SdCardHelp =
        "On the 3DS SD card PKSM keeps banks in /3ds/PKSM/banks/ (NAME.bnk + NAME.json; the default bank is pksm_1) " +
        "and single-mon dumps in /3ds/PKSM/dumps/. If that folder is empty, PKSM is storing banks in extdata: " +
        "turn off \"Use extdata\" in PKSM's settings, reopen PKSM, then copy the files over.";

    public const string ReturnHelp =
        "To use an exported bank on the 3DS, copy NAME.bnk and NAME.json into /3ds/PKSM/banks/ and list it in " +
        "/3ds/PKSM/banks.json as \"NAME\": box count (or name it pksm_1 to replace the default bank - back that up first). " +
        "PKSM resizes a bank to the count in banks.json, so never give it fewer boxes than the file holds.";

    /// <summary>
    /// Sorts picked files: .bnk files (or anything carrying the PKSMBANK magic, or a legacy
    /// bank.bin) pair with the .json of the same base name; .zip archives are opened and their
    /// contents sorted the same way; .pk1-.pk9, .pk and .pb7 files are loose mons.
    /// </summary>
    public static PksmImportSources Collect(IEnumerable<(string Name, byte[] Bytes)> files)
    {
        var banks = new List<(string, byte[], byte[]?)>();
        var loose = new List<(string, byte[])>();
        var ignored = new List<string>();
        var jsons = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(string Name, byte[] Bytes)>();

        void Sort(string name, byte[] bytes, bool allowZip)
        {
            var extension = Path.GetExtension(name).ToLowerInvariant();
            var stem = Path.GetFileNameWithoutExtension(name);
            if (extension == ".zip" && allowZip)
            {
                try
                {
                    using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                    foreach (var entry in zip.Entries)
                    {
                        if (entry.Length == 0 || entry.FullName.EndsWith('/')) continue;
                        if (entry.Length > 64 * 1024 * 1024) { ignored.Add($"{entry.FullName} (too large)"); continue; }
                        using var stream = entry.Open();
                        using var buffer = new MemoryStream();
                        stream.CopyTo(buffer);
                        Sort(entry.FullName, buffer.ToArray(), allowZip: false);
                    }
                }
                catch (InvalidDataException)
                {
                    ignored.Add($"{name} (not a readable zip)");
                }
                return;
            }
            if (extension == ".json")
            {
                jsons[stem] = bytes;
                return;
            }
            if (extension == ".bnk" || IsBankMagic(bytes) || Path.GetFileName(name).Equals("bank.bin", StringComparison.OrdinalIgnoreCase))
            {
                pending.Add((name, bytes));
                return;
            }
            if (IsLooseMonFileName(name))
            {
                loose.Add((Path.GetFileName(name), bytes));
                return;
            }
            ignored.Add(Path.GetFileName(name));
        }

        foreach (var (name, bytes) in files) Sort(name, bytes, allowZip: true);
        foreach (var (name, bytes) in pending)
        {
            var stem = Path.GetFileNameWithoutExtension(name);
            banks.Add((stem, bytes, jsons.Remove(stem, out var json) ? json : null));
        }
        // A lone .json with no bank beside it (banks.json, a names file picked by itself).
        ignored.AddRange(jsons.Keys.Select(k => k + ".json"));
        return new PksmImportSources(banks, loose, ignored);
    }

    public static bool IsBankMagic(ReadOnlySpan<byte> bytes) =>
        bytes.Length >= PksmBankFile.Magic.Length && bytes[..PksmBankFile.Magic.Length].SequenceEqual("PKSMBANK"u8);

    /// <summary>Loose mon files PKSM dumps (.pk1-.pk8, .pb7) plus PKHeX's .pk9 and bare .pk.</summary>
    public static bool IsLooseMonFileName(string name) =>
        BankArchive.IsPkFileName(name) || Path.GetExtension(name).Equals(".pb7", StringComparison.OrdinalIgnoreCase);

    /// <summary>The generation a dump's extension names (PKSM's <c>PKX::extension()</c>); null = detect.</summary>
    public static PksmGeneration? GenerationFromFileName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pb7" => PksmGeneration.LetsGo,
        ".pk1" => PksmGeneration.One,
        ".pk2" => PksmGeneration.Two,
        ".pk3" => PksmGeneration.Three,
        ".pk4" => PksmGeneration.Four,
        ".pk5" => PksmGeneration.Five,
        ".pk6" => PksmGeneration.Six,
        ".pk7" => PksmGeneration.Seven,
        ".pk8" => PksmGeneration.Eight,
        _ => null,
    };

    public static string Describe(PksmBankError error) => error switch
    {
        PksmBankError.TooSmall => "the file is too short to be a PKSM bank",
        PksmBankError.BadMagic => "it is not a PKSM bank (no PKSMBANK header)",
        PksmBankError.NewerVersion => "it was written by a newer PKSM than PKForge understands",
        PksmBankError.UnknownVersion => "its bank version is unknown",
        PksmBankError.BadBoxCount => "its box count is zero or over 500",
        _ => error.ToString(),
    };

    /// <summary>
    /// Decodes a bank into a plan. Throws <see cref="InvalidDataException"/> with a readable
    /// reason when the bytes are not a bank; per-mon failures become skipped items instead.
    /// </summary>
    public static PksmImportPlan PlanBank(string bankName, ReadOnlySpan<byte> bank, ReadOnlySpan<byte> boxNames, PksmEntityDecoder decode)
    {
        var parsed = PksmBankFile.Parse(bank);
        if (parsed.Contents is not { } contents)
            throw new InvalidDataException($"{bankName}: {Describe(parsed.Error!.Value)}.");

        var names = (boxNames.IsEmpty ? null : PksmBankFile.ParseBoxNames(boxNames)) ?? [];
        var fullNames = Enumerable.Range(0, contents.Boxes)
            .Select(b => b < names.Count && !string.IsNullOrWhiteSpace(names[b]) ? names[b] : PksmBankFile.DefaultBoxName(b))
            .ToArray();

        var items = new List<PksmImportItem>();
        foreach (var slot in contents.Slots)
        {
            if (slot.IsEmpty) continue;
            var label = PksmBankFile.Label(slot.Generation);
            var source = $"PKSM bank {bankName} · {fullNames[slot.Box]} slot {slot.Slot + 1}";
            var entity = PksmBankFile.EntityBytes(slot.Generation, slot.Data);
            if (entity is null)
            {
                items.Add(new(slot.Box, slot.Slot, label, null, null,
                    slot.Generation == PksmGeneration.Nine ? "Gen 9 tag (PKSM banks never hold Gen 9)" : $"unknown generation tag 0x{(uint)slot.Generation:X}"));
                continue;
            }
            items.Add(ToItem(slot.Box, slot.Slot, label, decode(slot.Generation, entity, source)));
        }
        return new PksmImportPlan(bankName, contents.SourceVersion, contents.Truncated, fullNames, items);
    }

    /// <summary>Decodes loose mon files (PKSM dumps, PKHeX exports) into a layout-free plan.</summary>
    public static PksmImportPlan PlanLoose(string source, IEnumerable<(string Name, byte[] Bytes)> files, PksmEntityDecoder decode)
    {
        var items = new List<PksmImportItem>();
        foreach (var (name, bytes) in files)
        {
            var hint = GenerationFromFileName(name);
            var label = hint is { } g ? PksmBankFile.Label(g) : "other";
            var decoded = decode(hint, bytes, $"PKSM dump {name}");
            if (decoded.Info is { } info && hint is null) label = $"Gen {info.Generation}";
            items.Add(ToItem(-1, items.Count, label, decoded));
        }
        return new PksmImportPlan(source, -1, false, [], items);
    }

    private static PksmImportItem ToItem(int box, int slot, string label, PksmDecoded decoded) =>
        decoded is { Bytes: { } bytes, Info: { } info }
            ? new PksmImportItem(box, slot, label, bytes, info, null)
            : new PksmImportItem(box, slot, label, null, null, decoded.Reason ?? "unreadable");

    /// <summary>
    /// Writes plans into the bank. Exact byte copies of mons already in the bank (or earlier in
    /// this import) are skipped as duplicates, so importing the same bank twice is harmless.
    /// In <see cref="PksmImportMode.NewBoxes"/> each source starts in the first box after the
    /// bank's last occupied box and keeps its slots; source box names are applied there.
    /// </summary>
    public static PksmImportResult Apply(IBankService bank, BankBoxNames? boxNames, IReadOnlyList<PksmImportPlan> plans, PksmImportMode mode)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in bank.GetAll()) known.Add(Sha256Hex(bank.GetData(entry.Id)));

        var imported = 0;
        var duplicates = 0;
        var existing = bank.GetAll();
        var nextBox = existing.Count == 0 ? 0 : existing.Max(e => e.Box) + 1;
        var firstNewBox = nextBox;

        foreach (var plan in plans)
        {
            var ready = new List<PksmImportItem>();
            foreach (var item in plan.Items.Where(i => i.Ready))
            {
                if (known.Add(Sha256Hex(item.Bytes!))) ready.Add(item);
                else duplicates++;
            }
            if (ready.Count == 0) continue;

            if (mode == PksmImportMode.Merge)
            {
                foreach (var item in ready) bank.Add(item.Bytes!, item.Info!);
                imported += ready.Count;
                continue;
            }

            // Target layout: a bank keeps box/slot (boxes renumbered from the first used one
            // so a bank whose first boxes are empty does not leave empty boxes behind); loose
            // files are packed in order.
            var placements = new List<(Guid, int, int)>(ready.Count);
            var firstSourceBox = plan.IsBank ? ready.Min(i => i.Box) : 0;
            for (var i = 0; i < ready.Count; i++)
            {
                var item = ready[i];
                var entry = bank.Add(item.Bytes!, item.Info!);
                var (box, slot) = plan.IsBank
                    ? (nextBox + item.Box - firstSourceBox, item.Slot)
                    : (nextBox + i / PksmBankFile.SlotsPerBox, i % PksmBankFile.SlotsPerBox);
                placements.Add((entry.Id, box, slot));
            }
            bank.Place(placements);
            imported += ready.Count;

            var lastBox = placements.Max(p => p.Item2);
            if (plan.IsBank && boxNames is not null)
            {
                boxNames.SetMany(Enumerable.Range(firstSourceBox, lastBox - nextBox + 1)
                    .Select(b => (nextBox + b - firstSourceBox, (string?)plan.BoxNames[b])));
            }
            nextBox = lastBox + 1;
        }
        return new PksmImportResult(imported, duplicates, firstNewBox, mode == PksmImportMode.NewBoxes ? nextBox - firstNewBox : 0);
    }

    /// <summary>
    /// Builds a PKSM bank (.bnk bytes + .json names) from bank entries. Mons PKSM cannot hold
    /// (Gen 9, BDSP, Legends, …) are left out and reported with the reason.
    /// </summary>
    public static PksmExportResult Export(
        IBankService bank,
        IReadOnlyList<BankEntry> entries,
        PksmEntityEncoder encode,
        Func<int, string?> boxName,
        PksmExportLayout layout)
    {
        var ordered = entries.OrderBy(e => e.Box).ThenBy(e => e.Slot).ToArray();
        var skipped = new List<(BankEntry, string)>();
        var encoded = new List<(BankEntry Entry, PksmGeneration Generation, byte[] Data)>();
        foreach (var entry in ordered)
        {
            PksmEncoded result;
            try { result = encode(bank.GetData(entry.Id)); }
            catch (Exception error) { result = PksmEncoded.Skip(error.Message); }
            if (result.Data is null || result.Generation == PksmGeneration.Unused)
                skipped.Add((entry, result.Reason ?? "PKSM has no slot for this format"));
            else
                encoded.Add((entry, result.Generation, result.Data));
        }

        var baseBox = encoded.Count == 0 ? 0 : encoded.Min(e => e.Entry.Box);
        var placed = new List<(int Box, int Slot, PksmGeneration Generation, ReadOnlyMemory<byte> Data)>(encoded.Count);
        for (var i = 0; i < encoded.Count; i++)
        {
            var (entry, generation, data) = encoded[i];
            var (box, slot) = layout == PksmExportLayout.KeepPositions
                ? (entry.Box - baseBox, entry.Slot)
                : (i / PksmBankFile.SlotsPerBox, i % PksmBankFile.SlotsPerBox);
            placed.Add((box, slot, generation, data));
        }

        var boxes = placed.Count == 0 ? 1 : placed.Max(p => p.Box) + 1;
        if (boxes > PksmBankFile.MaxBoxes)
            throw new InvalidOperationException($"That needs {boxes} boxes; a PKSM bank holds at most {PksmBankFile.MaxBoxes}. Export fewer boxes at a time.");

        var names = Enumerable.Range(0, boxes)
            .Select(b => layout == PksmExportLayout.KeepPositions
                ? boxName(b + baseBox) ?? PksmBankFile.DefaultBoxName(b)
                : PksmBankFile.DefaultBoxName(b))
            .ToArray();
        return new PksmExportResult(PksmBankFile.Write(boxes, placed), PksmBankFile.WriteBoxNames(names), boxes, placed.Count, skipped);
    }

    /// <summary>A bank name PKSM and every file manager accept: file-safe, never empty.</summary>
    public static string SanitizeBankName(string? raw)
    {
        var cleaned = BankArchive.SanitizeFileName(raw ?? "").Replace('.', '_');
        return cleaned.Length == 0 ? "pkforge" : cleaned;
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
