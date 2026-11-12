using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PKForge.Domain;

namespace PKForge.App.Services;

/// <param name="Soul">Who the Pokémon is whatever slot it came from (<see cref="ParkIdentity.Soul"/>);
/// empty when its data could not be read.</param>
public sealed record ParkPokemon(string Id, int Species, int Form, bool Shiny, string Name, string Source,
    string GameName = "", string TrainerName = "", string Origin = "", string Soul = "")
{
    /// <summary>One per park: the Pokémon's identity, or its visit id when that is unknown.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Identity => Soul.Length > 0 ? Soul : Id;
}
public enum PokeparkSource { Both, Bank, Save, Disabled }
public sealed record PokeparkSettings
{
    public PokeparkSource Source { get; init; } = PokeparkSource.Both;
    public bool Random { get; init; } = true;
    public int Count { get; init; } = 6;
    public string[] SelectedIds { get; init; } = [];
    /// <summary>The daycare writes EXP and eggs back to saves and the Bank, where the game allows it.</summary>
    public bool DaycareWritesBack { get; init; } = true;
}

public sealed class PokeparkService(IBankService bank, ISaveSessionService saves, IGameDataService data)
{
    private readonly object _initializationGate = new();
    private const string SettingsKey = "pkforge.pokepark.settings.v1";
    private const string ResidentsKey = "pkforge.pokepark.residents.v1";
    private const string RosterKey = "pkforge.pokepark.roster.v1";
    public PokeparkSettings Settings => Normalize(Read<PokeparkSettings>(SettingsKey) ?? new());

    public IReadOnlyList<ParkPokemon> GetCandidates()
    {
        var result = bank.GetAll().Where(e => e.Info.Species > 0).Select(e =>
            new ParkPokemon($"bank:{e.Id:N}", e.Info.Species, e.Info.Form, e.Info.Shiny,
                Name(e.Info.Nickname, e.Info.Species), $"Bank · Box {e.Box + 1} · Slot {e.Slot + 1}",
                GameName: $"Gen {e.Info.Generation}", TrainerName: "", Origin: e.Info.SourceName,
                Soul: BankSoul(e))).ToList();
        var session = saves.CurrentSession;
        var document = saves.Current?.Document;
        if (session is null || document is null) return result;
        var documentId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document.DocumentId)))[..16];
        var trainer = session.GetTrainer().Name;
        var gameName = $"Gen {session.Generation}";
        // Include the party as well as PC boxes. Party entries use Box == -1
        // and are valid save-backed Poképark candidates.
        foreach (var slot in session.Snapshot.Slots.Where(s => s.Species is > 0 && !s.IsEgg))
        {
            var species = slot.Species!.Value;
            result.Add(new ParkPokemon($"save:{documentId}:{slot.Box}:{slot.Slot}",
                species, slot.Form, slot.IsShiny,
                string.IsNullOrWhiteSpace(slot.Nickname) ? SpeciesName(species) : slot.Nickname,
                $"{document.DisplayName} · {(slot.Box < 0 ? "Party" : $"Box {slot.Box + 1}")} · Slot {slot.Slot + 1}",
                GameName: gameName, TrainerName: trainer, Origin: document.DisplayName,
                Soul: SoulOf(() => session.ExportSlot(slot.Box, slot.Slot))));
        }
        return result;
    }

    private readonly Dictionary<Guid, string> _bankSouls = new();

    // A Bank entry's bytes never move under its id, so its identity is read once.
    private string BankSoul(BankEntry entry)
    {
        lock (_bankSouls) if (_bankSouls.TryGetValue(entry.Id, out var known)) return known;
        var soul = SoulOf(() => new SlotExport(bank.GetData(entry.Id), "", entry.Info.Format));
        lock (_bankSouls) _bankSouls[entry.Id] = soul;
        return soul;
    }

    /// <summary>The Pokémon's identity from its own data, empty when it cannot be read.</summary>
    private static string SoulOf(Func<SlotExport> read)
    {
        try
        {
            var data = read();
            return PKForge.Engine.EntityBytes.Parse(data.Data, string.IsNullOrEmpty(data.Format) ? null : data.Format) is { Species: > 0 } pk
                ? ParkIdentity.Soul(pk.PID, pk.Format >= 6 ? pk.EncryptionConstant : null, pk.OriginalTrainerName, pk.TID16)
                : "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or IOException or FormatException)
        {
            return "";
        }
    }

    /// <summary>A resident saved before identities were kept, given its identity when its source can be read.</summary>
    private ParkPokemon WithSoul(ParkPokemon mon)
    {
        if (mon.Soul.Length > 0) return mon;
        var parts = mon.Id.Split(':');
        if (parts is ["bank", var guid, ..] && Guid.TryParse(guid, out var id) && bank.GetAll().FirstOrDefault(e => e.Id == id) is { } entry)
            return mon with { Soul = BankSoul(entry) };
        return SourceOf(mon) is { BankId: null } source ? mon with { Soul = SoulOf(() => new SlotExport(source.Data, "", source.Format)) } : mon;
    }

    /// <summary>The species name, the same table every picker shows.</summary>
    public string SpeciesName(int species) =>
        (uint)species < (uint)data.SpeciesNames.Count ? data.SpeciesNames[species] : $"#{species}";

    private const string InitializedKey = "pkforge.pokepark.initialized.v2";
    private const string AutoFillSuppressedKey = "pkforge.pokepark.autofill-suppressed.v1";
    public const int MaximumResidents = ParkLife.MostResidents;

    /// <summary>Called once at app startup. Opening the park never chooses new residents.</summary>
    public void EnsureInitialized()
    {
        lock (_initializationGate)
        {
            // Home and a deep-linked park can appear nearly together. Only one of
            // them may enumerate/export/hash the open save at a time.
            if (Read<bool>(InitializedKey)) return;
            var roster = LoadRoster();
            if (roster.Count == 0 && Settings.Source != PokeparkSource.Disabled)
            {
                var candidates = GetCandidates().Where(p => Settings.Source == PokeparkSource.Both ||
                    p.Id.StartsWith(Settings.Source == PokeparkSource.Bank ? "bank:" : "save:"));
                roster = ParkSelection.Select(candidates, p => p.Identity, true, Settings.Count, []);
            }
            SaveRoster(roster);
        }
    }

    public int AutoFillIfEmpty()
    {
        if (LoadRoster().Count > 0) return 0;
        return FillAutomatically([]);
    }

    public int RefreshAutoFill()
    {
        if (Read<bool>(AutoFillSuppressedKey)) return 0;
        var previous = LoadRoster();
        return FillAutomatically(previous);
    }

    private int FillAutomatically(IReadOnlyList<ParkPokemon> previous)
    {
        if (Read<bool>(AutoFillSuppressedKey)) return 0;
        var settings = Settings;
        if (settings.Source == PokeparkSource.Disabled) return 0;
        var candidates = GetCandidates().Where(p => settings.Source == PokeparkSource.Both ||
            p.Id.StartsWith(settings.Source == PokeparkSource.Bank ? "bank:" : "save:"));
        var available = candidates.ToArray();
        if (available.Length == 0) return 0;
        var fresh = available.Where(p => !previous.Any(old => old.Identity == p.Identity)).ToArray();
        if (fresh.Length >= Math.Min(settings.Count, MaximumResidents))
            available = fresh;
        // Automatic filling is independent from the old manual-selection
        // list. In combined mode, guarantee representation from both Save and
        // Bank whenever both are available and the requested count allows it.
        var roster = settings.Source == PokeparkSource.Both
            ? ParkSelection.SelectBalanced(available, p => p.Identity,
                p => p.Id.StartsWith("save:", StringComparison.Ordinal) ? "save" : "bank",
                settings.Count)
            : ParkSelection.Select(available, p => p.Identity, true, settings.Count, []);
        if (roster.Count == 0) return 0;
        SaveRoster(roster);
        return roster.Count;
    }

    /// <summary>Brings the park up to the auto-fill size from the auto-fill source, keeping everyone
    /// already there (a smaller size removes nobody). Returns how many arrived.</summary>
    public int TopUpAutoFill()
    {
        var settings = Settings;
        if (settings.Source == PokeparkSource.Disabled) return 0;
        var roster = LoadRoster();
        var candidates = GetCandidates().Where(p => settings.Source == PokeparkSource.Both ||
            p.Id.StartsWith(settings.Source == PokeparkSource.Bank ? "bank:" : "save:"));
        var filled = ParkSelection.TopUp(roster, candidates, p => p.Identity, settings.Count);
        if (filled.Count == roster.Count) return 0;
        Write(AutoFillSuppressedKey, false);
        SaveRoster(filled);
        return filled.Count - roster.Count;
    }

    public void SaveSettings(PokeparkSettings settings)
    {
        var normalized = Normalize(settings);
        Write(SettingsKey, normalized);
        if (normalized.Source != PokeparkSource.Disabled) Write(AutoFillSuppressedKey, false);
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

        // Auto-fill used to name un-nicknamed residents "Pokémon #25". Heal persisted
        // rosters to the species name so every surface (journal, park, speech bubbles)
        // shows the real name without a re-invite.
        var healed = false;
        // Residents saved before identities were kept get theirs when their source can be read,
        // and a Pokémon that ended up in the park twice (once auto-filled, once invited) stays once.
        roster = roster.Select(p =>
        {
            var named = IsLegacyFallbackName(p.Name, p.Species) ? p with { Name = SpeciesName(p.Species) } : p;
            var known = named.Soul.Length > 0 ? named : WithSoul(named);
            if (!ReferenceEquals(known, p)) healed = true;
            return known;
        }).ToArray();
        var unique = roster.Where(p => p.Species > 0).DistinctBy(p => p.Id).DistinctBy(p => p.Identity).Take(MaximumResidents).ToArray();
        if (healed || unique.Length != roster.Length) SaveRoster(unique);
        return unique;
    }

    /// <summary>Matches exactly the old fallback pattern "Pokémon #N" (and its ASCII
    /// spelling), never a real nickname the user could have typed.</summary>
    private static bool IsLegacyFallbackName(string name, int species) =>
        name.Equals($"Pokémon #{species}", StringComparison.OrdinalIgnoreCase) ||
        name.Equals($"Pokemon #{species}", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<ParkPokemon> GetLastRoster() => LoadRoster();

    public string AddBankVisitor(Guid id)
    {
        var entry = bank.GetAll().FirstOrDefault(e => e.Id == id);
        if (entry is null || entry.Info.Species <= 0) return "This Pokémon is no longer in the bank.";
        return AddVisitor(new ParkPokemon($"bank:{entry.Id:N}", entry.Info.Species, entry.Info.Form,
            entry.Info.Shiny, Name(entry.Info.Nickname, entry.Info.Species),
            $"Bank · Box {entry.Box + 1} · Slot {entry.Slot + 1}",
            GameName: $"Gen {entry.Info.Generation}", TrainerName: "", Origin: entry.Info.SourceName, Soul: BankSoul(entry)));
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
        var export = session.ExportSlot(box, slot);
        var fingerprint = Convert.ToHexString(SHA256.HashData(export.Data));
        return AddVisitor(new ParkPokemon($"save:{documentId}:{box}:{slot}:{fingerprint}", detail.Species,
            detail.Form, detail.IsShiny, string.IsNullOrWhiteSpace(detail.Nickname) ? detail.SpeciesName : detail.Nickname,
            $"{document.DisplayName} · {(box < 0 ? "Party" : $"Box {box + 1}")} · Slot {slot + 1}",
            GameName: $"Gen {session.Generation}", TrainerName: session.GetTrainer().Name, Origin: document.DisplayName,
            Soul: SoulOf(() => export)));
    }

    private readonly Dictionary<string, int> _natures = new();

    private readonly Dictionary<string, DateOnly> _metDates = new();

    /// <summary>The day the player met the resident (Gen 4 and later), from the Bank or the open save.</summary>
    public DateOnly? MetDateOf(ParkPokemon mon)
    {
        lock (_metDates) if (_metDates.TryGetValue(mon.Id, out var known)) return known;
        DateOnly? met = null;
        try
        {
            var parts = mon.Id.Split(':');
            if (parts is ["bank", var guid, ..] && Guid.TryParse(guid, out var id) && bank.GetAll().FirstOrDefault(e => e.Id == id) is { } entry)
                met = PKForge.Engine.EntityBytes.Parse(entry, bank.GetData(id))?.MetDate;
            else if (parts is ["save", var document, var boxText, var slotText, ..] && saves.CurrentSession is { } session &&
                     saves.Current?.Document is { } open &&
                     Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(open.DocumentId)))[..16] == document &&
                     int.TryParse(boxText, out var box) && int.TryParse(slotText, out var slot) &&
                     session.GetMetInfo(box, slot) is { SupportsMetDate: true } info &&
                     DateOnly.TryParse(info.MetDate, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                met = parsed;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or IOException or FormatException)
        {
            // No date to celebrate.
        }
        if (met is { } found) lock (_metDates) _metDates[mon.Id] = found;
        return met;
    }

    /// <summary>A nature's name, in the game data's language.</summary>
    public string NatureName(int nature) => (uint)nature < (uint)data.NatureNames.Count ? data.NatureNames[nature] : $"#{nature}";

    /// <summary>The resident's nature, read from the Bank or from the open save (for its berry
    /// tastes); null when its save is not open.</summary>
    public int? NatureOf(ParkPokemon mon)
    {
        lock (_natures) if (_natures.TryGetValue(mon.Id, out var known)) return known;
        int? nature = null;
        try
        {
            var parts = mon.Id.Split(':');
            if (parts is ["bank", var guid, ..] && Guid.TryParse(guid, out var id) && bank.GetAll().FirstOrDefault(e => e.Id == id) is { } entry)
                nature = PKForge.Engine.EntityBytes.Parse(entry, bank.GetData(id)) is { } pk ? (int)pk.Nature : null;
            else if (parts is ["save", var document, var boxText, var slotText, ..] && saves.CurrentSession is { } session &&
                     saves.Current?.Document is { } open &&
                     Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(open.DocumentId)))[..16] == document &&
                     int.TryParse(boxText, out var box) && int.TryParse(slotText, out var slot) &&
                     session.ReadEntity(box, slot) is { } detail && detail.Species == mon.Species)
                nature = detail.Nature;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or IOException or FormatException)
        {
            // An unreadable entry keeps the taste the park gives it from its id.
        }
        if (nature is { } found) lock (_natures) _natures[mon.Id] = found;
        return nature;
    }

    /// <summary>Where a resident's real data lives: a Bank entry, or a slot of the open save.</summary>
    public sealed record ParkSource(byte[] Data, string? Format, Guid? BankId, int Box, int Slot, string SourceName);

    /// <summary>The resident's real data, from the Bank or from the open save (null when its save is not open).</summary>
    public ParkSource? SourceOf(ParkPokemon mon)
    {
        try
        {
            var parts = mon.Id.Split(':');
            if (parts is ["bank", var guid, ..] && Guid.TryParse(guid, out var id) && bank.GetAll().FirstOrDefault(e => e.Id == id) is { } entry)
                return new(bank.GetData(id), entry.Info.Format, id, entry.Box, entry.Slot, entry.Info.SourceName);
            if (parts is ["save", var document, var boxText, var slotText, ..] && saves.CurrentSession is { } session &&
                saves.Current?.Document is { } open &&
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(open.DocumentId)))[..16] == document &&
                int.TryParse(boxText, out var box) && int.TryParse(slotText, out var slot) &&
                session.ReadEntity(box, slot) is { } detail && detail.Species == mon.Species)
            {
                var export = session.ExportSlot(box, slot);
                return new(export.Data, export.Format, null, box, slot, open.DisplayName);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException or IOException or FormatException)
        {
            // Unreadable: the park keeps it as it is.
        }
        return null;
    }

    public bool RemoveVisitor(string id)
    {
        var roster = LoadRoster();
        if (!roster.Any(p => p.Id == id)) return false;
        SaveRoster(roster.Where(p => p.Id != id).ToArray());
        Write(AutoFillSuppressedKey, true);
        return true;
    }

    public int RemoveAllVisitors()
    {
        var count = LoadRoster().Count;
        if (count > 0) SaveRoster([]);
        Write(AutoFillSuppressedKey, true);
        return count;
    }

    private string AddVisitor(ParkPokemon pokemon)
    {
        var roster = LoadRoster();
        if (roster.Any(p => p.Id == pokemon.Id || p.Identity == pokemon.Identity)) return $"{pokemon.Name} already lives in Poképark.";
        if (roster.Count >= MaximumResidents)
            return $"Poképark has {MaximumResidents} residents. Open the park and remove a resident before adding another.";
        Write(AutoFillSuppressedKey, false);
        SaveRoster(roster.Append(pokemon).ToArray());
        return $"{pokemon.Name} now lives in Poképark! Your original Pokémon stays in its save or bank.";
    }

    private static void SaveRoster(IReadOnlyList<ParkPokemon> roster)
    {
        Write(RosterKey, roster);
        Write(InitializedKey, true);
    }

    private string Name(string name, int species) => string.IsNullOrWhiteSpace(name) ? SpeciesName(species) : name;
    private static PokeparkSettings Normalize(PokeparkSettings settings) => settings with
    {
        Source = Enum.IsDefined(settings.Source) ? settings.Source : PokeparkSource.Both,
        Count = Math.Clamp(settings.Count, 1, MaximumResidents),
        SelectedIds = (settings.SelectedIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().Take(MaximumResidents).ToArray()
    };
    private static T? Read<T>(string key)
    {
        try { return JsonSerializer.Deserialize<T>(Preferences.Default.Get(key, "null")); }
        catch (JsonException) { return default; }
    }
    private static void Write<T>(string key, T value) => Preferences.Default.Set(key, JsonSerializer.Serialize(value));
}
