namespace PKForge.Domain;

/// <summary>One exact copy of a Pokémon in one entity format: the bytes it had there (decrypted,
/// as PKHeX holds them), the format they read as, and the game it was in.</summary>
public sealed record HistoryCopy(string Format, string Game, DateTimeOffset At, string Data)
{
    public byte[] Bytes() => Convert.FromBase64String(Data);

    public static HistoryCopy Of(string format, string game, DateTimeOffset at, byte[] bytes) =>
        new(format, game, at, Convert.ToBase64String(bytes));
}

/// <summary>One trip in a Pokémon's life: it left <paramref name="FromGame"/> for
/// <paramref name="ToGame"/>; <paramref name="Restored"/> when it came back to data it had.</summary>
public sealed record HistoryTrip(DateTimeOffset At, string FromGame, string FromFormat, string ToGame, string ToFormat, bool Restored);

/// <summary>
/// What PKForge remembers of one Pokémon across formats (the opt-in "Pokémon History"). It is
/// keyed by every identity the Pokémon has been known by (<see cref="ParkIdentity.Soul"/>): a
/// downgrade may roll a new PID and the move to Gen 6 adds an encryption constant, so one
/// Pokémon can carry several. For each format it has <em>left</em> it keeps the exact bytes it had
/// there (<see cref="Departures"/>), and for each format it <em>arrived</em> in, the bytes it landed
/// with (<see cref="Arrivals"/>): the baseline that tells play since then from the conversion's own
/// compromises. One copy per format: the latest wins.
/// </summary>
public sealed record PokemonHistoryRecord(
    string Id, int Species, IReadOnlyList<string> Souls,
    IReadOnlyList<HistoryCopy> Departures, IReadOnlyList<HistoryCopy> Arrivals, IReadOnlyList<HistoryTrip> Trips)
{
    public HistoryCopy? DepartureFor(string format) => Departures.FirstOrDefault(c => c.Format == format);

    public HistoryCopy? ArrivalFor(string format) => Arrivals.FirstOrDefault(c => c.Format == format);

    /// <summary>The games it has been in, oldest visit first, without repeats.</summary>
    public IReadOnlyList<string> GamesVisited() =>
        [.. Trips.OrderBy(t => t.At).SelectMany(t => new[] { t.FromGame, t.ToGame }).Distinct(StringComparer.Ordinal)];

    /// <summary>A file-safe, stable name for the record (its first identity, hashed).</summary>
    public static string IdFor(string soul) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(soul)))[..20].ToLowerInvariant();

    /// <summary>This record after a trip: the copy it left, the copy it landed as, both identities.</summary>
    public PokemonHistoryRecord WithTrip(IEnumerable<string> souls, int species, HistoryCopy departed, HistoryCopy arrived, bool restored) =>
        this with
        {
            Species = species,
            Souls = [.. Souls.Concat(souls).Distinct(StringComparer.Ordinal)],
            Departures = Replace(Departures, departed),
            Arrivals = Replace(Arrivals, arrived),
            Trips = [.. Trips, new HistoryTrip(arrived.At, departed.Game, departed.Format, arrived.Game, arrived.Format, restored)],
        };

    public static PokemonHistoryRecord Start(IReadOnlyList<string> souls, int species) =>
        new(IdFor(souls[0]), species, souls, [], [], []);

    /// <summary>
    /// Two records of the same Pokémon (another device's, an archive's) as one: every identity,
    /// the newer copy for each format, every trip once. Nothing either side knew is lost.
    /// </summary>
    public static PokemonHistoryRecord Merge(PokemonHistoryRecord mine, PokemonHistoryRecord theirs)
    {
        static IReadOnlyList<HistoryCopy> Newest(IEnumerable<HistoryCopy> copies) =>
            [.. copies.GroupBy(c => c.Format, StringComparer.Ordinal).Select(g => g.OrderByDescending(c => c.At).First()).OrderBy(c => c.Format, StringComparer.Ordinal)];
        var newer = theirs.Trips.Select(t => t.At).DefaultIfEmpty().Max() > mine.Trips.Select(t => t.At).DefaultIfEmpty().Max();
        return mine with
        {
            Species = newer ? theirs.Species : mine.Species,
            Souls = [.. mine.Souls.Concat(theirs.Souls).Distinct(StringComparer.Ordinal)],
            Departures = Newest(mine.Departures.Concat(theirs.Departures)),
            Arrivals = Newest(mine.Arrivals.Concat(theirs.Arrivals)),
            Trips = [.. mine.Trips.Concat(theirs.Trips).Distinct().OrderBy(t => t.At)],
        };
    }

    private static IReadOnlyList<HistoryCopy> Replace(IReadOnlyList<HistoryCopy> copies, HistoryCopy copy) =>
        [.. copies.Where(c => c.Format != copy.Format), copy];
}

/// <summary>Where Pokémon History records live. <see cref="Find"/> matches any identity a record holds.</summary>
public interface IPokemonHistoryStore
{
    PokemonHistoryRecord? Find(IEnumerable<string> souls);

    void Save(PokemonHistoryRecord record);

    IReadOnlyList<PokemonHistoryRecord> All();

    /// <summary>Finds, changes and saves a record as one step, so a record merged in meanwhile (a
    /// folder sync) is not overwritten by a copy read before it.</summary>
    PokemonHistoryRecord Update(IEnumerable<string> souls, Func<PokemonHistoryRecord?, PokemonHistoryRecord> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var updated = change(Find(souls));
        Save(updated);
        return updated;
    }
}

/// <summary>A store held in memory: tests, and the dry runs that must not write anything.</summary>
public sealed class InMemoryPokemonHistoryStore : IPokemonHistoryStore
{
    private readonly Dictionary<string, PokemonHistoryRecord> _records = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public PokemonHistoryRecord? Find(IEnumerable<string> souls)
    {
        var wanted = souls.ToHashSet(StringComparer.Ordinal);
        lock (_gate) return _records.Values.FirstOrDefault(r => r.Souls.Any(wanted.Contains));
    }

    public void Save(PokemonHistoryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate) _records[record.Id] = record;
    }

    public IReadOnlyList<PokemonHistoryRecord> All()
    {
        lock (_gate) return [.. _records.Values];
    }
}
