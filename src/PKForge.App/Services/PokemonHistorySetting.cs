using PKForge.Domain;
using PKForge.Infrastructure;

namespace PKForge.App.Services;

/// <summary>
/// Pokémon History (Settings › Editing and safety), off by default. When on, every transfer that
/// changes a Pokémon's format remembers the exact copy it had in the format it left, and a Pokémon
/// coming back to a format it has been in gets that copy back with its progress since laid on top
/// (<see cref="PKForge.Engine.PokemonHistoryService"/>). It only ever gives a Pokémon its own data
/// back, so Hardcore mode keeps it. Records live in app data's <c>pokemon-history/</c>, which the
/// PKForge folder mirrors (History/) and the data export carries.
/// </summary>
public static class PokemonHistorySetting
{
    private const string Key = "pokemon_history";

    private static readonly Lazy<FilePokemonHistoryStore> Files = new(() =>
        new FilePokemonHistoryStore(Path.Combine(FileSystem.AppDataDirectory, FilePokemonHistoryStore.Folder)));

    public static bool IsOn => Preferences.Default.Get(Key, false);

    public static void Set(bool on) => Preferences.Default.Set(Key, on);

    /// <summary>The records on disk, readable whether or not the setting is on.</summary>
    public static IPokemonHistoryStore Store => Files.Value;

    /// <summary>The same store, for the PKForge folder mirror (one instance, so one lock).</summary>
    public static FilePokemonHistoryStore FileStore => Files.Value;

    /// <summary>What the engine's sessions use: the store while the setting is on, else nothing.</summary>
    public static IPokemonHistoryStore? Active() => IsOn ? Files.Value : null;
}
