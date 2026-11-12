using System.Text.Json;
using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>Persists Poképark relationships and produces bounded offline social history.
/// Relationships are kept by each Pokémon's identity (<see cref="ParkIdentity"/>), not by its
/// visit, so a resident that leaves and comes back finds its friends again.</summary>
public sealed class PokeparkSocialService
{
    private IReadOnlyDictionary<string, string> _souls = new Dictionary<string, string>();

    /// <summary>Today's residents: their id in the park to who they are. The park sets it on every reload.</summary>
    public IReadOnlyDictionary<string, string> Souls { get => _souls; set => _souls = value ?? new Dictionary<string, string>(); }

    private string SoulOf(string id) => _souls.GetValueOrDefault(id, id);

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

    /// <summary>Records a moment seen live: the journal gains a line and the pair's relationship moves.</summary>
    public void Witness(ParkMoment moment, string first, string second, DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.Now;
        var known = moment with { FirstId = SoulOf(moment.FirstId), SecondId = SoulOf(moment.SecondId) };
        Save(ParkSocialSimulation.Witness(Load(current), known, first, second, current).State);
    }

    /// <summary>How every pair gets on, for the park's life to pick friends and rivals.</summary>
    public IReadOnlyDictionary<string, (int Friendship, int Rivalry)> Relations(DateTimeOffset? now = null) =>
        ParkIdentity.Relations(Load(now).Relationships, _souls);

    public ParkRelationship Relationship(string firstId, string secondId, DateTimeOffset? now = null)
    {
        var key = ParkRelationship.KeyFor(SoulOf(firstId), SoulOf(secondId));
        return Load(now).Relationships.FirstOrDefault(r => r.Key == key)
            ?? new ParkRelationship(SoulOf(firstId), SoulOf(secondId));
    }

    public IReadOnlyList<ParkSocialEvent> Journal(int count = 30, DateTimeOffset? now = null) =>
        // Lines saved before the journal stopped repeating itself show once.
        Load(now).Journal.OrderByDescending(e => e.OccurredAt).DistinctBy(e => e.Text).Take(Math.Clamp(count, 0, 100)).ToArray();

    public void RemoveResident(string id, DateTimeOffset? now = null)
    {
        var state = Load(now);
        Save(state with
        {
            Relationships = state.Relationships.Where(r => r.FirstId != SoulOf(id) && r.SecondId != SoulOf(id)).ToArray()
        });
    }

    public void Reset(DateTimeOffset? now = null)
    {
        Preferences.Default.Remove(StateKey);
        if (now is not null) Save(ParkSocialState.Empty(now.Value));
    }

    // Relationships saved by visit id move onto the identity of today's residents.
    private ParkSocialState Normalize(ParkSocialState state, DateTimeOffset now)
    {
        var last = state.LastSimulatedAt == default || state.LastSimulatedAt > now ? now : state.LastSimulatedAt;
        return state with
        {
            Relationships = ParkIdentity.Rekey((state.Relationships ?? []).Where(r => !string.IsNullOrWhiteSpace(r.FirstId) &&
                !string.IsNullOrWhiteSpace(r.SecondId) && r.FirstId != r.SecondId)
                .GroupBy(r => r.Key).Select(g => g.Last().Normalize()), SoulOf),
            Journal = (state.Journal ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Text))
                .OrderBy(e => e.OccurredAt).TakeLast(100).ToArray(),
            LastSimulatedAt = last
        };
    }

    private static void Save(ParkSocialState state) =>
        Preferences.Default.Set(StateKey, JsonSerializer.Serialize(state, JsonOptions));
}
