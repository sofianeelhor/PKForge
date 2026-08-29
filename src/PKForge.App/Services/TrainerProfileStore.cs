using System.Text.Json;
using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>Named trainer identities and the generation ownership preference.</summary>
public sealed class TrainerProfileStore : IGenerationOwnershipSettings
{
    private const string ProfilesKey = "trainer_profiles_v1";
    private const string GenerationKey = "generated_pokemon_use_current_trainer";

    public bool UseCurrentTrainerForGeneration => Preferences.Default.Get(GenerationKey, true);

    public IReadOnlyList<TrainerProfile> Profiles
    {
        get
        {
            try
            {
                var json = Preferences.Default.Get(ProfilesKey, "");
                return string.IsNullOrWhiteSpace(json)
                    ? []
                    : JsonSerializer.Deserialize<List<TrainerProfile>>(json) ?? [];
            }
            catch { return []; }
        }
    }

    public void SetUseCurrentTrainerForGeneration(bool enabled) => Preferences.Default.Set(GenerationKey, enabled);

    public TrainerProfile Save(string displayName, TrainerInfo trainer)
    {
        var profiles = Profiles.ToList();
        var name = displayName.Trim();
        var existing = profiles.FirstOrDefault(p => string.Equals(p.DisplayName, name, StringComparison.OrdinalIgnoreCase));
        var profile = new TrainerProfile(existing?.Id ?? Guid.NewGuid().ToString("N"), name, trainer.Name,
            Math.Clamp(trainer.TID, 0, ushort.MaxValue), Math.Clamp(trainer.SID, 0, ushort.MaxValue),
            Math.Clamp(trainer.Gender, 0, 1));
        if (existing is null)
            profiles.Add(profile);
        else
            profiles[profiles.IndexOf(existing)] = profile;
        Write(profiles);
        return profile;
    }

    public void Delete(string id)
    {
        var profiles = Profiles.Where(p => p.Id != id).ToList();
        Write(profiles);
    }

    private static void Write(IReadOnlyList<TrainerProfile> profiles) =>
        Preferences.Default.Set(ProfilesKey, JsonSerializer.Serialize(profiles));
}
