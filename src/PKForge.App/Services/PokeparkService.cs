using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PKForge.Domain;

namespace PKForge.App.Services;

public sealed record ParkPokemon(string Id, int Species, int Form, bool Shiny, string Name, string Source);
public enum PokeparkSource { Both, Bank, Save, Disabled }
public sealed record PokeparkSettings
{
    public PokeparkSource Source { get; init; } = PokeparkSource.Both;
    public bool Random { get; init; } = true;
    public int Count { get; init; } = 6;
    public string[] SelectedIds { get; init; } = [];
}

/// <summary>Visual visitors only: never modifies a Pokémon, save, or bank entry.</summary>
public sealed class PokeparkService(IBankService bank, ISaveSessionService saves)
{
    private const string SettingsKey = "pkforge.pokepark.settings.v1";
    private const string ResidentsKey = "pkforge.pokepark.residents.v1";
    private const string RosterKey = "pkforge.pokepark.roster.v1";
    public PokeparkSettings Settings => Normalize(Read<PokeparkSettings>(SettingsKey) ?? new());

    public IReadOnlyList<ParkPokemon> GetCandidates()
    {
        var result = bank.GetAll().Where(e => e.Info.Species > 0).Select(e =>
            new ParkPokemon($"bank:{e.Id:N}", e.Info.Species, e.Info.Form, e.Info.Shiny,
                Name(e.Info.Nickname, e.Info.Species), $"Bank · Box {e.Box + 1} · Slot {e.Slot + 1}")).ToList();
        var session = saves.CurrentSession;
        var document = saves.Current?.Document;
        if (session is null || document is null) return result;
        var documentId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.DocumentId)))[..16];
        foreach (var slot in session.Snapshot.Slots.Where(s => s.Box >= 0 && s.Species is > 0))
        {
            if (session.GetMetInfo(slot.Box, slot.Slot).IsEgg) continue;
            var detail = session.ReadEntity(slot.Box, slot.Slot);
            var data = session.ExportSlot(slot.Box, slot.Slot).Data;
            var fingerprint = Convert.ToHexString(SHA256.HashData(data));
            result.Add(new ParkPokemon($"save:{documentId}:{slot.Box}:{slot.Slot}:{fingerprint}",
                detail.Species, detail.Form, detail.IsShiny,
                string.IsNullOrWhiteSpace(detail.Nickname) ? detail.SpeciesName : detail.Nickname,
                $"{document.DisplayName} · Box {slot.Box + 1} · Slot {slot.Slot + 1}"));
        }
        return result;
    }

    private const string InitializedKey = "pkforge.pokepark.initialized.v2";
    public const int MaximumResidents = 12;

    /// <summary>Called once at app startup. Opening the park never chooses new residents.</summary>
    public void EnsureInitialized()
    {
        if (Read<bool>(InitializedKey)) return;
        var roster = LoadRoster();
        if (roster.Count == 0)
        {
            var settings = Settings;
            if (settings.Source == PokeparkSource.Disabled)
            {
                Write(InitializedKey, true);
                return;
            }
            var candidates = GetCandidates().Where(p => settings.Source == PokeparkSource.Both ||
                p.Id.StartsWith(settings.Source == PokeparkSource.Bank ? "bank:" : "save:"));
            roster = ParkSelection.Select(candidates, p => p.Id, true, settings.Count, []);
        }
        SaveRoster(roster);
    }

    public void SaveSettings(PokeparkSettings settings)
    {
        var normalized = Normalize(settings);
        Write(SettingsKey, normalized);
        if (LoadRoster().Count == 0) Write(InitializedKey, false);
    }

    public IReadOnlyList<ParkPokemon> GetSelectableCandidates() => GetCandidates()
        .Concat(LoadRoster()).DistinctBy(p => p.Id).ToArray();

    /// <summary>Persisted visual snapshots survive save switches and source deletion.</summary>
    public IReadOnlyList<ParkPokemon> LoadRoster()
    {
        var roster = Read<ParkPokemon[]>(RosterKey) ?? [];
        if (!Read<bool>(InitializedKey))
        {
            // Upgrade existing explicitly selected visitors before the old random sample.
            var settings = Settings;
            var selected = (Read<ParkPokemon[]>(ResidentsKey) ?? [])
                .Where(p => settings.SelectedIds.Contains(p.Id)).ToArray();
            if (!settings.Random && selected.Length > 0) roster = selected;
        }
        return roster.Where(p => p.Species > 0).DistinctBy(p => p.Id).Take(MaximumResidents).ToArray();
    }

    public IReadOnlyList<ParkPokemon> GetLastRoster() => LoadRoster();

    public string AddBankVisitor(Guid id)
    {
        var entry = bank.GetAll().FirstOrDefault(e => e.Id == id);
        if (entry is null || entry.Info.Species <= 0) return "This Pokémon is no longer in the bank.";
        return AddVisitor(new ParkPokemon($"bank:{entry.Id:N}", entry.Info.Species, entry.Info.Form,
            entry.Info.Shiny, Name(entry.Info.Nickname, entry.Info.Species),
            $"Bank · Box {entry.Box + 1} · Slot {entry.Slot + 1}"));
    }

    public string AddSaveVisitor(int box, int slot)
    {
        var session = saves.CurrentSession;
        var document = saves.Current?.Document;
        if (session is null || document is null) return "Open a save to send a Pokémon to Poképark.";
        if (!session.Snapshot.Slots.Any(s => s.Box == box && s.Slot == slot && s.Species is > 0))
            return "Select a Pokémon first.";
        if (session.GetMetInfo(box, slot).IsEgg) return "Eggs can visit Poképark after they hatch.";
        var detail = session.ReadEntity(box, slot);
        var documentId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.DocumentId)))[..16];
        var fingerprint = Convert.ToHexString(SHA256.HashData(session.ExportSlot(box, slot).Data));
        return AddVisitor(new ParkPokemon($"save:{documentId}:{box}:{slot}:{fingerprint}", detail.Species,
            detail.Form, detail.IsShiny, string.IsNullOrWhiteSpace(detail.Nickname) ? detail.SpeciesName : detail.Nickname,
            $"{document.DisplayName} · {(box < 0 ? "Party" : $"Box {box + 1}")} · Slot {slot + 1}"));
    }

    public bool RemoveVisitor(string id)
    {
        var roster = LoadRoster();
        if (!roster.Any(p => p.Id == id)) return false;
        SaveRoster(roster.Where(p => p.Id != id).ToArray());
        return true;
    }

    private string AddVisitor(ParkPokemon pokemon)
    {
        var roster = LoadRoster();
        if (roster.Any(p => p.Id == pokemon.Id)) return $"{pokemon.Name} already lives in Poképark.";
        if (roster.Count >= MaximumResidents)
            return $"Poképark has {MaximumResidents} residents. Open the park and remove a resident before adding another.";
        SaveRoster(roster.Append(pokemon).ToArray());
        return $"{pokemon.Name} now lives in Poképark! Your original Pokémon stays in its save or bank.";
    }

    private static void SaveRoster(IReadOnlyList<ParkPokemon> roster)
    {
        Write(RosterKey, roster);
        Write(InitializedKey, true);
    }

    private static string Name(string name, int species) => string.IsNullOrWhiteSpace(name) ? $"Pokémon #{species}" : name;
    private static PokeparkSettings Normalize(PokeparkSettings settings) => settings with
    {
        Source = Enum.IsDefined(settings.Source) ? settings.Source : PokeparkSource.Both,
        Count = Math.Clamp(settings.Count, 1, 12),
        SelectedIds = (settings.SelectedIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(12).ToArray()
    };
    private static T? Read<T>(string key)
    {
        try { return JsonSerializer.Deserialize<T>(Preferences.Default.Get(key, "null")); }
        catch (JsonException) { return default; }
    }
    private static void Write<T>(string key, T value) => Preferences.Default.Set(key, JsonSerializer.Serialize(value));
}
