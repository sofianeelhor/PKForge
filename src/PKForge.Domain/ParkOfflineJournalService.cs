using System.Text.Json;

namespace PKForge.Domain;

public interface IParkOfflineStateStore
{
    string? Read(string key);
    void Write(string key, string value);
}

public sealed record ParkOfflineJournalState(
    DateTimeOffset LastSimulationTime,
    IReadOnlyList<ParkJournalEvent> Journal);

/// <summary>Coordinates safe elapsed-time simulation and bounded journal persistence.</summary>
public sealed class ParkOfflineJournalService(
    IParkOfflineStateStore store,
    TimeProvider? timeProvider = null,
    Func<Random>? randomFactory = null)
{
    private const string StateKey = "pkforge.pokepark.offline-journal.v1";
    public const int MaximumJournalEntries = 80;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Func<Random> _randomFactory = randomFactory ?? (() => Random.Shared);
    private readonly ParkOfflineSimulator _simulator = new();

    public ParkOfflineJournalState Load()
    {
        try
        {
            var json = store.Read(StateKey);
            var state = string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<ParkOfflineJournalState>(json, JsonOptions);
            return state is null
                ? new(_time.GetUtcNow(), [])
                : state with { Journal = state.Journal.TakeLast(MaximumJournalEntries).ToArray() };
        }
        catch (JsonException)
        {
            return new(_time.GetUtcNow(), []);
        }
    }

    public ParkOfflineSimulationResult Resume(IReadOnlyList<ParkOfflineResident> residents)
    {
        var now = _time.GetUtcNow();
        var saved = Load();
        var result = _simulator.Simulate(saved.LastSimulationTime, now, residents, _randomFactory());
        var journal = saved.Journal.Concat(result.Events)
            .OrderBy(e => e.Timestamp)
            .TakeLast(MaximumJournalEntries)
            .ToArray();
        Save(new(now, journal));
        return result;
    }

    public void MarkCurrent() => Save(new(_time.GetUtcNow(), Load().Journal));

    public void ClearJournal() => Save(new(_time.GetUtcNow(), []));

    private void Save(ParkOfflineJournalState state) =>
        store.Write(StateKey, JsonSerializer.Serialize(state, JsonOptions));
}
