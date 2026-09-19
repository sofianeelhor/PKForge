using System.Text.Json;

namespace PKForge.App.Services;

public sealed class PokeparkNarrativeMemory
{
    private const string Key = "pkforge.pokepark.dialogue-history.v1";
    private const int PerResidentLimit = 18;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public IReadOnlyCollection<string> Recent(string residentId)
    {
        var state = Load();
        return state.TryGetValue(residentId, out var ids) ? ids : [];
    }

    public void Remember(string residentId, string dialogueId)
    {
        var state = Load();
        var ids = state.GetValueOrDefault(residentId) ?? [];
        state[residentId] = ids.Where(id => id != dialogueId).Append(dialogueId)
            .TakeLast(PerResidentLimit).ToArray();
        Preferences.Default.Set(Key, JsonSerializer.Serialize(state, JsonOptions));
    }

    private static Dictionary<string, string[]> Load()
    {
        try
        {
            var json = Preferences.Default.Get(Key, "");
            return string.IsNullOrWhiteSpace(json)
                ? new(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, string[]>>(json, JsonOptions)
                    ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new(StringComparer.Ordinal);
        }
    }
}
