using System.Text.Json;
using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>Persists Poképark relationships and produces bounded offline social history.</summary>
public sealed class PokeparkSocialService
{
    private const string StateKey = "pkforge.pokepark.social.v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ParkSocialState Load(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        try
        {
            var json = Preferences.Default.Get(StateKey, "");
            var state = string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ParkSocialState>(json, JsonOptions);
            return Normalize(state ?? ParkSocialState.Empty(current), current);
        }
        catch (JsonException)
        {
            return ParkSocialState.Empty(current);
        }
    }

    public IReadOnlyList<ParkSocialEvent> SimulateOffline(IReadOnlyList<ParkSocialPokemon> residents,
        DateTimeOffset? now = null, Random? random = null, int maximumEvents = 8)
    {
        var current = now ?? DateTimeOffset.Now;
        var (state, events) = ParkSocialSimulation.Simulate(Load(current), residents, current, random, maximumEvents);
        Save(state);
        return events;
    }

    public ParkRelationship Relationship(string firstId, string secondId, DateTimeOffset? now = null)
    {
        var key = ParkRelationship.KeyFor(firstId, secondId);
        return Load(now).Relationships.FirstOrDefault(r => r.Key == key)
            ?? new ParkRelationship(firstId, secondId);
    }

    public IReadOnlyList<ParkSocialEvent> Journal(int count = 30, DateTimeOffset? now = null) =>
        Load(now).Journal.OrderByDescending(e => e.OccurredAt).Take(Math.Clamp(count, 0, 100)).ToArray();

    public void RemoveResident(string id, DateTimeOffset? now = null)
    {
        var state = Load(now);
        Save(state with
        {
            Relationships = state.Relationships.Where(r => r.FirstId != id && r.SecondId != id).ToArray()
        });
    }

    public void Reset(DateTimeOffset? now = null)
    {
        Preferences.Default.Remove(StateKey);
        if (now is not null) Save(ParkSocialState.Empty(now.Value));
    }

    private static ParkSocialState Normalize(ParkSocialState state, DateTimeOffset now)
    {
        var last = state.LastSimulatedAt == default || state.LastSimulatedAt > now ? now : state.LastSimulatedAt;
        return state with
        {
            Relationships = (state.Relationships ?? []).Where(r => !string.IsNullOrWhiteSpace(r.FirstId) &&
                !string.IsNullOrWhiteSpace(r.SecondId) && r.FirstId != r.SecondId)
                .GroupBy(r => r.Key).Select(g => g.Last().Normalize()).ToArray(),
            Journal = (state.Journal ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Text))
                .OrderBy(e => e.OccurredAt).TakeLast(100).ToArray(),
            LastSimulatedAt = last
        };
    }

    private static void Save(ParkSocialState state) =>
        Preferences.Default.Set(StateKey, JsonSerializer.Serialize(state, JsonOptions));
}
