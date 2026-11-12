using System.Text.Json;
using System.Text.Json.Serialization;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// The Item Bank on disk: one JSON file under <paramref name="root"/>, replaced atomically on
/// every save, so a write that fails leaves the previous bank whole. The root is the caller's
/// choice (the app passes its data folder), never a path decided here.
/// <para>
/// Every change goes through one lock: <see cref="Update"/> reads, changes and writes the bank
/// as one step, so a folder sync landing while a game is being written is never overwritten by
/// a stale copy. The file also remembers the last one-time changes it took (<see cref="UpdateOnce"/>),
/// so a Pokéwalker homecoming replayed after a crash cannot bank its finds twice.
/// </para>
/// </summary>
public sealed class ItemBankStore(string root) : IItemBankStore
{
    public const string FileName = "item-bank.json";

    /// <summary>How many one-time change keys the file remembers (oldest dropped first).</summary>
    internal const int AppliedKept = 200;

    private readonly Lock _gate = new();

    public string Root { get; } = root;

    public string FilePath => Path.Combine(Root, FileName);

    public ItemBank Read()
    {
        lock (_gate) return ReadFile().Bank;
    }

    /// <summary>Replaces the bank (the one-time keys already taken are kept).</summary>
    public void Save(ItemBank bank)
    {
        ArgumentNullException.ThrowIfNull(bank);
        lock (_gate) WriteFile(bank, ReadFileOrEmpty().Applied);
    }

    /// <summary>Reads, changes and writes the bank as one step under the store's lock; returns the bank written.</summary>
    public ItemBank Update(Func<ItemBank, ItemBank> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var (bank, applied) = ReadFile();
            var changed = change(bank);
            if (!ReferenceEquals(changed, bank)) WriteFile(changed, applied);
            return changed;
        }
    }

    /// <summary>As <see cref="Update"/>, once per <paramref name="key"/>: the key is written in the same
    /// file as the change, so either both landed or neither. False when the key was already applied.</summary>
    public bool UpdateOnce(string key, Func<ItemBank, ItemBank> change)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var (bank, applied) = ReadFile();
            if (applied.Contains(key, StringComparer.Ordinal)) return false;
            WriteFile(change(bank), [.. applied, key]);
            return true;
        }
    }

    /// <summary>Replays the change from <paramref name="from"/> to <paramref name="to"/> on the bank as it is
    /// now (see <see cref="ItemBank.ThreeWay"/>), so whatever else changed meanwhile stays. Swap them to undo.</summary>
    public ItemBank Apply(ItemBank from, ItemBank to) => Update(current => ItemBank.ThreeWay(from, current, to));

    /// <summary>Merges <paramref name="incoming"/> into the stored bank (see <see cref="ItemBank.Merge"/>),
    /// in one write; returns the entries added or raised.</summary>
    public int Merge(ItemBank incoming)
    {
        lock (_gate)
        {
            var (bank, applied) = ReadFile();
            var merged = bank.Merge(incoming, out var changed);
            if (changed > 0) WriteFile(merged, applied);
            return changed;
        }
    }

    /// <summary>The stored file's bytes as they are (null when there is none), for the folder mirror.</summary>
    public byte[]? ReadBytes()
    {
        lock (_gate) return File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;
    }

    /// <summary>
    /// Takes in a copy of the bank from elsewhere (the PKForge folder): three-way against
    /// <paramref name="common"/>, the bytes both sides last agreed on (null when unknown), under the
    /// store's lock. The one-time keys of both sides are kept. Throws <see cref="InvalidDataException"/>
    /// when <paramref name="incoming"/> is not an Item Bank.
    /// </summary>
    public ItemBank MergeIn(byte[]? common, byte[] incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        var theirs = Parse(incoming) ?? throw new InvalidDataException("This is not an Item Bank file.");
        var ancestor = common is null ? null : Parse(common);
        lock (_gate)
        {
            var (bank, applied) = ReadFile();
            var merged = ItemBank.ThreeWay(ancestor?.Bank, bank, theirs.Bank);
            WriteFile(merged, [.. applied, .. theirs.Applied.Where(key => !applied.Contains(key, StringComparer.Ordinal))]);
            return merged;
        }
    }

    /// <summary>An Item Bank file's content, or null when the bytes are not one (not JSON, another
    /// shape, a list missing): what a pulled copy is checked with before it is taken in.</summary>
    public static (ItemBank Bank, IReadOnlyList<string> Applied)? Parse(byte[] bytes)
    {
        try
        {
            var file = JsonSerializer.Deserialize(bytes, ItemBankJsonContext.Default.ItemBankFile);
            return file is { Entries: not null } ? Normalize(file) : null;
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    public static bool IsItemBank(byte[] bytes) => Parse(bytes) is not null;

    /// <summary>A file that is valid JSON but not whole (no entries, an entry without origins) never
    /// reaches the bank as nulls: broken entries are dropped, missing origins read as none.</summary>
    private static (ItemBank Bank, IReadOnlyList<string> Applied) Normalize(ItemBankFile file)
    {
        var entries = (file.Entries ?? [])
            .Where(entry => entry is not null && entry.Count > 0 && !string.IsNullOrWhiteSpace(entry.Key))
            .Select(entry => entry with
            {
                Name = entry.Name ?? entry.Key,
                Origins = [.. (entry.Origins ?? []).Where(origin => origin is not null && origin.Game is not null && origin.Count > 0)],
            })
            .ToArray();
        return (new ItemBank(entries), [.. (file.Applied ?? []).Where(key => !string.IsNullOrWhiteSpace(key))]);
    }

    private (ItemBank Bank, IReadOnlyList<string> Applied) ReadFile()
    {
        if (!File.Exists(FilePath)) return (ItemBank.Empty, []);
        var file = JsonSerializer.Deserialize(File.ReadAllText(FilePath), ItemBankJsonContext.Default.ItemBankFile);
        if (file?.Entries is null) throw new InvalidDataException("The Item Bank file is not readable: it holds no item list.");
        return Normalize(file);
    }

    /// <summary>The keys of a damaged file are not worth refusing a save over.</summary>
    private (ItemBank Bank, IReadOnlyList<string> Applied) ReadFileOrEmpty()
    {
        try { return ReadFile(); }
        catch (Exception error) when (error is JsonException or InvalidDataException) { return (ItemBank.Empty, []); }
    }

    private void WriteFile(ItemBank bank, IReadOnlyList<string> applied)
    {
        var kept = applied.Count > AppliedKept ? applied.Skip(applied.Count - AppliedKept).ToList() : applied.ToList();
        var json = JsonSerializer.Serialize(new ItemBankFile(1, [.. bank.Entries], kept.Count == 0 ? null : kept), ItemBankJsonContext.Default.ItemBankFile);
        AtomicFile.WriteText(FilePath, json);
    }
}

/// <summary>The Item Bank file's shape on disk. <see cref="Applied"/> (one-time change keys) is absent
/// from older files and ignored by older app versions.</summary>
internal sealed record ItemBankFile(int Version, List<ItemBankEntry> Entries,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] List<string>? Applied = null);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ItemBankFile))]
internal partial class ItemBankJsonContext : JsonSerializerContext
{
}
