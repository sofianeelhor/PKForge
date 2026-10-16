using System.Globalization;
using System.Text.Json;

namespace PKForge.Sync;

/// <summary>A copy of the Bank taken before sync applies a large change.</summary>
public interface IBankSnapshots
{
    void Take(string reason);
}

/// <summary>One copy of the Bank: its folder name, when it was taken, why, and how many Pokémon it holds (null when unreadable).</summary>
public sealed record BankSnapshotInfo(string Id, DateTimeOffset CreatedUtc, string Reason, int? PokemonCount, long SizeBytes);

/// <summary>Copies the Bank folder's files into a timestamped folder; only the newest <c>keep</c> copies stay.</summary>
public sealed class FileBankSnapshots(string bankRoot, string snapshotRoot, TimeProvider? time = null, int keep = 5) : IBankSnapshots
{
    private const string StampFormat = "yyyyMMdd-HHmmss-fff";
    private const string ReasonFile = "reason.txt";
    private const string IndexFile = "index.json";

    public void Take(string reason) => Take(reason, spare: null);

    /// <summary>Every copy, newest first.</summary>
    public IReadOnlyList<BankSnapshotInfo> List()
    {
        if (!Directory.Exists(snapshotRoot)) return [];
        var list = new List<BankSnapshotInfo>();
        foreach (var folder in Directory.GetDirectories(snapshotRoot))
        {
            var id = Path.GetFileName(folder);
            if (!DateTime.TryParseExact(id, StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var stamp))
                continue;
            var reasonPath = Path.Combine(folder, ReasonFile);
            var reason = File.Exists(reasonPath) ? File.ReadAllText(reasonPath).Trim() : "";
            var size = Directory.EnumerateFiles(folder).Sum(f => new FileInfo(f).Length);
            list.Add(new BankSnapshotInfo(id, new DateTimeOffset(stamp, TimeSpan.Zero), reason, CountPokemon(Path.Combine(folder, IndexFile)), size));
        }
        return [.. list.OrderByDescending(s => s.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Puts the Bank folder back as it was in copy <paramref name="id"/>, after copying the current Bank first
    /// (<paramref name="reason"/>). The index goes last, so a failure part way leaves the current index in charge.
    /// The caller reloads whatever reads the Bank folder.
    /// </summary>
    public void Restore(string id, string reason)
    {
        if (string.IsNullOrWhiteSpace(id) || id != Path.GetFileName(id) || id.StartsWith('.'))
            throw new ArgumentException("Not a Bank copy.", nameof(id));
        var source = Path.Combine(snapshotRoot, id);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("That Bank copy no longer exists.");

        Take(reason, spare: id);
        Directory.CreateDirectory(bankRoot);
        var files = Directory.EnumerateFiles(source).Select(Path.GetFileName).OfType<string>().Where(f => f != ReasonFile).ToHashSet();
        foreach (var name in files.Where(f => f != IndexFile).Append(IndexFile).Where(files.Contains))
        {
            var target = Path.Combine(bankRoot, name);
            File.Copy(Path.Combine(source, name), target + ".restore", overwrite: true);
            File.Move(target + ".restore", target, overwrite: true);
        }
        foreach (var extra in Directory.EnumerateFiles(bankRoot).Where(f => !files.Contains(Path.GetFileName(f))).ToList())
        {
            try { File.Delete(extra); }
            catch (IOException) { /* a leftover file the restored index doesn't list is harmless */ }
        }
    }

    private void Take(string reason, string? spare)
    {
        var stamp = (time ?? TimeProvider.System).GetUtcNow().ToString(StampFormat, CultureInfo.InvariantCulture);
        var target = Path.Combine(snapshotRoot, stamp);
        Directory.CreateDirectory(target);
        if (Directory.Exists(bankRoot))
        {
            foreach (var file in Directory.EnumerateFiles(bankRoot))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        File.WriteAllText(Path.Combine(target, ReasonFile), reason);
        // The copy about to be restored is never the one pushed out.
        foreach (var old in Directory.GetDirectories(snapshotRoot).Where(d => Path.GetFileName(d) != spare).Order(StringComparer.Ordinal).SkipLast(keep))
            Directory.Delete(old, recursive: true);
    }

    private static int? CountPokemon(string indexPath)
    {
        try
        {
            using var index = JsonDocument.Parse(File.ReadAllText(indexPath));
            return index.RootElement.TryGetProperty("Entries", out var entries) && entries.ValueKind == JsonValueKind.Array ? entries.GetArrayLength() : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
