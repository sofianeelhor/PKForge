using System.Globalization;
using System.Text;

namespace PKForge.Domain;

/// <summary>Where some of a banked item came from: the game, its generation, how many.</summary>
public sealed record ItemBankOrigin(string Game, int Generation, int Count);

/// <summary>One kind of item in the Item Bank: its generation-independent key, the name it
/// was deposited under, how many are banked, and which saves they came from.</summary>
public sealed record ItemBankEntry(string Key, string Name, int Count, IReadOnlyList<ItemBankOrigin> Origins);

/// <summary>
/// Item Bank keys. Item ids differ between generations (Gen 1-3 have their own tables), so an
/// item is keyed by its modern (Gen 4+) index, which every later game shares. Machines are the
/// exception: TM01 teaches a different move from one game family to the next, so a machine is
/// keyed by its name and the game family it came from, and only goes back there.
/// </summary>
public static class ItemBankKeys
{
    private const string ItemPrefix = "item:";
    private const string MachinePrefix = "machine:";

    public static string Item(int modernId) => $"{ItemPrefix}{modernId.ToString(CultureInfo.InvariantCulture)}";

    public static string Machine(string name, string family) => $"{MachinePrefix}{Slug(name)}@{family}";

    public static bool IsMachineKey(string key) => key.StartsWith(MachinePrefix, StringComparison.Ordinal);

    /// <summary>The game family a machine key belongs to; null for an ordinary item.</summary>
    public static string? MachineFamily(string key) =>
        IsMachineKey(key) && key.LastIndexOf('@') is >= 0 and var at ? key[(at + 1)..] : null;

    /// <summary>"TM01", "TR42", "HM 05", "TM100": a machine, whose meaning depends on the game.</summary>
    public static bool IsMachineName(string name)
    {
        var text = name.Trim();
        if (text.Length < 3) return false;
        var prefix = text[..2].ToUpperInvariant();
        if (prefix is not ("TM" or "TR" or "HM")) return false;
        var rest = text[2..].TrimStart();
        return rest.Length > 0 && rest.All(char.IsAsciiDigit);
    }

    public static bool IsHiddenMachineName(string name) =>
        IsMachineName(name) && name.TrimStart().StartsWith("HM", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A name reduced to what survives the games' spelling changes: case, accents, spaces and
    /// punctuation drop ("POKé BALL", "Poké Ball"; "BlackGlasses", "Black Glasses"), and the
    /// few real renames are folded onto today's name ("Parlyz Heal" is "Paralyze Heal").
    /// </summary>
    public static string NameKey(string name)
    {
        var slug = Slug(name);
        return Renames.TryGetValue(slug, out var modern) ? modern : slug;
    }

    private static readonly Dictionary<string, string> Renames = new(StringComparer.Ordinal)
    {
        ["parlyzheal"] = "paralyzeheal",
    };

    private static string Slug(string name)
    {
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var text = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch)) text.Append(char.ToLowerInvariant(ch));
        }
        return text.ToString();
    }
}

/// <summary>
/// The Item Bank: items kept outside every save, to be taken out in any game that has them.
/// Immutable; every change returns a new bank, so a failed write leaves the old one intact.
/// </summary>
public sealed record ItemBank(IReadOnlyList<ItemBankEntry> Entries)
{
    /// <summary>The most one entry holds, far beyond any bag, so the bank never overflows a count.</summary>
    public const int MaxPerEntry = 999_999;

    public static ItemBank Empty { get; } = new([]);

    public ItemBankEntry? Find(string key) => Entries.FirstOrDefault(entry => entry.Key == key);

    public int CountOf(string key) => Find(key)?.Count ?? 0;

    /// <summary>How many more the entry can still take.</summary>
    public int Room(string key) => MaxPerEntry - CountOf(key);

    public ItemBank Deposit(string key, string name, int count, string game, int generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (count > Room(key)) throw new InvalidOperationException($"The Item Bank holds at most {MaxPerEntry} {name}.");
        var entries = Entries.ToList();
        var index = entries.FindIndex(entry => entry.Key == key);
        if (index < 0)
        {
            entries.Add(new ItemBankEntry(key, name, count, [new ItemBankOrigin(game, generation, count)]));
            return new ItemBank(entries);
        }
        var entry = entries[index];
        var origins = entry.Origins.ToList();
        var from = origins.FindIndex(origin => origin.Game == game && origin.Generation == generation);
        if (from < 0) origins.Add(new ItemBankOrigin(game, generation, count));
        else origins[from] = origins[from] with { Count = origins[from].Count + count };
        entries[index] = entry with { Count = entry.Count + count, Origins = origins };
        return new ItemBank(entries);
    }

    /// <summary>
    /// Brings another bank's items in without losing any here: an item this bank lacks is added,
    /// and for an item both hold, each origin (game and generation) keeps the larger of the two
    /// counts. Merging the same bank twice changes nothing the second time. <paramref name="changed"/>
    /// counts the entries added or raised.
    /// </summary>
    public ItemBank Merge(ItemBank incoming, out int changed)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        changed = 0;
        var entries = Entries.ToList();
        foreach (var other in incoming.Entries)
        {
            if (string.IsNullOrWhiteSpace(other.Key) || other.Count <= 0) continue;
            var index = entries.FindIndex(entry => entry.Key == other.Key);
            if (index < 0)
            {
                entries.Add(other with { Count = Math.Min(other.Count, MaxPerEntry), Origins = [.. other.Origins] });
                changed++;
                continue;
            }
            var entry = entries[index];
            var origins = entry.Origins.ToList();
            var raised = false;
            foreach (var origin in other.Origins.Where(o => o.Count > 0))
            {
                var at = origins.FindIndex(o => o.Game == origin.Game && o.Generation == origin.Generation);
                if (at < 0) { origins.Add(origin); raised = true; }
                else if (origin.Count > origins[at].Count) { origins[at] = origin; raised = true; }
            }
            if (!raised) continue;
            var count = (int)Math.Min(MaxPerEntry, Math.Max(entry.Count, origins.Sum(o => (long)o.Count)));
            entries[index] = entry with { Count = count, Origins = origins };
            changed++;
        }
        return changed == 0 ? this : new ItemBank(entries);
    }

    /// <summary>
    /// The three-way merge of two banks that both grew from <paramref name="common"/>: every
    /// deposit and every withdrawal either side made since then is kept, origin by origin
    /// (local + remote − common, never below zero). It also replays one change on a bank that
    /// moved on meanwhile: <c>ThreeWay(before, current, after)</c> is <c>current</c> plus what
    /// went from <c>before</c> to <c>after</c>, and swapping <c>before</c> and <c>after</c>
    /// undoes it. Without a common ancestor (null) nothing is known to have been withdrawn, so
    /// it falls back to <see cref="Merge"/>, which keeps the larger count of every origin:
    /// an item may come back, but none is ever lost.
    /// </summary>
    public static ItemBank ThreeWay(ItemBank? common, ItemBank local, ItemBank remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ArgumentNullException.ThrowIfNull(remote);
        if (common is null) return local.Merge(remote, out _);

        static Dictionary<string, ItemBankEntry> ByKey(ItemBank bank)
        {
            var map = new Dictionary<string, ItemBankEntry>(StringComparer.Ordinal);
            foreach (var entry in bank.Entries) map.TryAdd(entry.Key, entry);
            return map;
        }
        static long OriginCount(ItemBankEntry? entry, (string Game, int Generation) at) =>
            entry?.Origins.Where(o => o.Game == at.Game && o.Generation == at.Generation).Sum(o => (long)o.Count) ?? 0;

        var bases = ByKey(common);
        var mine = ByKey(local);
        var theirs = ByKey(remote);
        var keys = local.Entries.Select(e => e.Key).Concat(remote.Entries.Select(e => e.Key)).Distinct(StringComparer.Ordinal);
        var entries = new List<ItemBankEntry>();
        foreach (var key in keys)
        {
            mine.TryGetValue(key, out var l);
            theirs.TryGetValue(key, out var r);
            bases.TryGetValue(key, out var b);
            var count = (long)(l?.Count ?? 0) + (r?.Count ?? 0) - (b?.Count ?? 0);
            if (count <= 0) continue;
            var places = (l?.Origins ?? []).Concat(r?.Origins ?? []).Concat(b?.Origins ?? [])
                .Select(o => (o.Game, o.Generation)).Distinct().ToList();
            var origins = new List<ItemBankOrigin>();
            foreach (var at in places)
            {
                var n = OriginCount(l, at) + OriginCount(r, at) - OriginCount(b, at);
                if (n > 0) origins.Add(new ItemBankOrigin(at.Game, at.Generation, (int)Math.Min(MaxPerEntry, n)));
            }
            var named = l ?? r!;
            entries.Add(named with { Count = (int)Math.Min(MaxPerEntry, count), Origins = origins });
        }
        return new ItemBank(entries);
    }

    /// <summary>Takes items out, the latest deposits first; an emptied entry leaves the bank.</summary>
    public ItemBank Withdraw(string key, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        var entries = Entries.ToList();
        var index = entries.FindIndex(entry => entry.Key == key);
        if (index < 0 || entries[index].Count < count)
            throw new InvalidOperationException("The Item Bank does not hold that many.");
        var entry = entries[index];
        if (entry.Count == count)
        {
            entries.RemoveAt(index);
            return new ItemBank(entries);
        }
        var origins = entry.Origins.ToList();
        var left = count;
        for (var i = origins.Count - 1; i >= 0 && left > 0; i--)
        {
            var taken = Math.Min(left, origins[i].Count);
            left -= taken;
            if (taken == origins[i].Count) origins.RemoveAt(i);
            else origins[i] = origins[i] with { Count = origins[i].Count - taken };
        }
        entries[index] = entry with { Count = entry.Count - count, Origins = origins };
        return new ItemBank(entries);
    }
}

/// <summary>Where the Item Bank is kept. <see cref="Update"/> reads, changes and writes it as one
/// step, so a change made elsewhere meanwhile (a folder sync, a Pokéwalker find) is never overwritten.</summary>
public interface IItemBankStore
{
    ItemBank Read();

    /// <summary>Applies <paramref name="change"/> to the bank as it is now; returns the bank written.</summary>
    ItemBank Update(Func<ItemBank, ItemBank> change);

    /// <summary>As <see cref="Update"/>, once per <paramref name="key"/> (the key is kept with the bank,
    /// written in the same step); false when that key was already applied.</summary>
    bool UpdateOnce(string key, Func<ItemBank, ItemBank> change);
}

/// <summary>Where a banked item lands in the open game: its pouch and its id there.</summary>
public sealed record ItemBankPlace(string Pouch, int ItemId);

/// <summary>A banked item as the open game sees it: where it would go, or why it cannot.</summary>
public sealed record ItemBankOffer(ItemBankEntry Entry, ItemBankPlace? Place, string? Reason)
{
    public bool CanWithdraw => Reason is null && Place is not null;
}

public static class ItemBankOffers
{
    /// <summary>
    /// What the bank offers one pouch of the open game: the items that go in it, then every item
    /// this game cannot hold at all, each with its reason (never silently dropped). Items that go
    /// in another pouch of this game are offered there instead.
    /// </summary>
    public static IReadOnlyList<ItemBankOffer> ForPouch(ItemBank bank, string pouch, string family,
        Func<string, ItemBankPlace?> locate)
    {
        var fits = new List<ItemBankOffer>();
        var dimmed = new List<ItemBankOffer>();
        foreach (var entry in bank.Entries)
        {
            var place = locate(entry.Key);
            if (place is null) dimmed.Add(new ItemBankOffer(entry, null, Reason(entry, family)));
            else if (place.Pouch == pouch) fits.Add(new ItemBankOffer(entry, place, null));
        }
        return [.. fits.OrderBy(o => o.Entry.Name, StringComparer.CurrentCultureIgnoreCase),
            .. dimmed.OrderBy(o => o.Entry.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    internal static string Reason(ItemBankEntry entry, string? family)
    {
        var from = entry.Origins.Count > 0 ? entry.Origins[^1].Game : "another game";
        if (ItemBankKeys.MachineFamily(entry.Key) is { } machineFamily && machineFamily != family)
            return $"A machine teaches a different move in each game: it goes back to {from}.";
        return "This game has no such item.";
    }
}
