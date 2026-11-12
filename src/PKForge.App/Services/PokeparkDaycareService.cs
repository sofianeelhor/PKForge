using System.Security.Cryptography;
using System.Text.Json;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Services;

/// <summary>A resident left at the Poképark daycare: since when, and a fingerprint of its data at
/// check-in, so the daycare only ever writes back to the very Pokémon it was given.</summary>
public sealed record ParkStay(string ResidentId, DateTimeOffset Since, string? Fingerprint);

/// <summary>
/// The Poképark daycare against the player's real Pokémon. A stay earns EXP by the hour, as far as
/// the Pokémon's own game allows (<see cref="ParkDaycareService"/>); on pick-up the EXP goes back
/// to its save slot or Bank entry when the player allows it and the Pokémon has not changed since.
/// Eggs from two guests go to the Bank. With write-back off, everything stays in the park.
/// </summary>
public sealed class PokeparkDaycareService(PokeparkService park, IBankService bank, ISaveEngine engine, BoxBrowserViewModel boxes)
{
    private const string Key = "pkforge.pokepark.daycare.v1";
    public const int Capacity = 4;

    /// <summary>EXP per hour of stay: about a walk's worth of steps.</summary>
    public const uint ExpPerHour = 500;

    private sealed record State(ParkStay[] Stays, string[]? Egg);

    private State Load()
    {
        try { return JsonSerializer.Deserialize<State>(Preferences.Default.Get(Key, "")) ?? new([], null); }
        catch (JsonException) { return new([], null); }
    }

    private static void Save(State state) => Preferences.Default.Set(Key, JsonSerializer.Serialize(state));

    public IReadOnlyList<ParkStay> Stays => Load().Stays;

    public bool IsGuest(string id) => Load().Stays.Any(s => s.ResidentId == id);

    /// <summary>The egg Blissey is keeping, if any (the two parents' ids).</summary>
    public (string First, string Second)? Egg => Load().Egg is [var a, var b] ? (a, b) : null;

    public void KeepEgg((string First, string Second)? egg) => Save(Load() with { Egg = egg is { } e ? [e.First, e.Second] : null });

    public static bool WritesBack(PokeparkSettings settings) => settings.DaycareWritesBack;

    /// <summary>Leaves a resident at the daycare.</summary>
    public string CheckIn(ParkPokemon mon)
    {
        var state = Load();
        if (state.Stays.Any(s => s.ResidentId == mon.Id)) return $"{mon.Name} is already at the daycare.";
        if (state.Stays.Length >= Capacity) return $"The daycare is full: Blissey can look after {Capacity} at a time.";
        var source = park.SourceOf(mon);
        Save(state with { Stays = [.. state.Stays, new ParkStay(mon.Id, DateTimeOffset.Now, source is null ? null : Fingerprint(source.Data))] });
        var rules = source is null ? null : ParkDaycareService.RulesFor(source.Data, source.Format);
        var note = rules switch
        {
            null => "Its save is not open, so whatever it learns here stays in the park.",
            { Grows: true } => "It will grow a little for every hour it stays.",
            { Breeds: true } => "Its game has a Nursery, not a daycare: it won't gain EXP, but it may find a partner.",
            _ => "Its game has no daycare, so this is a holiday: no EXP, no eggs."
        };
        return $"Blissey takes {mon.Name} by the paw and shows it around. {note}";
    }

    /// <summary>What a stay has been worth so far, for the card: hours and the level it would reach.</summary>
    public (TimeSpan Time, ParkDaycareGrowth? Growth) Progress(ParkPokemon mon)
    {
        if (Load().Stays.FirstOrDefault(s => s.ResidentId == mon.Id) is not { } stay) return (TimeSpan.Zero, null);
        var time = DateTimeOffset.Now - stay.Since;
        var source = park.SourceOf(mon);
        return (time, source is null ? null : ParkDaycareService.Preview(source.Data, source.Format, Exp(time)));
    }

    /// <summary>Picks a resident up. The EXP goes back to the real Pokémon when allowed.</summary>
    public async Task<string> PickUpAsync(ParkPokemon mon)
    {
        var state = Load();
        if (state.Stays.FirstOrDefault(s => s.ResidentId == mon.Id) is not { } stay) return $"{mon.Name} is not at the daycare.";
        var time = DateTimeOffset.Now - stay.Since;
        var stayed = Duration(time);
        var message = await GrowAsync(mon, stay, Exp(time), stayed);
        var left = Load();
        Save(left with { Stays = left.Stays.Where(s => s.ResidentId != mon.Id).ToArray(), Egg = left.Egg is { } egg && egg.Contains(mon.Id) ? null : left.Egg });
        return message;
    }

    /// <summary>Called when a resident leaves the park: its stay ends with it, nothing written.</summary>
    public void Forget(string id)
    {
        var state = Load();
        Save(state with { Stays = state.Stays.Where(s => s.ResidentId != id).ToArray(), Egg = state.Egg is { } egg && egg.Contains(id) ? null : state.Egg });
    }

    private async Task<string> GrowAsync(ParkPokemon mon, ParkStay stay, uint exp, string stayed)
    {
        var settings = park.Settings;
        var source = park.SourceOf(mon);
        var hello = $"Blissey brings {mon.Name} back. It stayed {stayed}.";
        if (source is null) return $"{hello} Its save is not open, so the time it spent here stays a park memory.";
        var rules = ParkDaycareService.RulesFor(source.Data, source.Format);
        if (!rules.Grows) return $"{hello} {(rules.Breeds ? "Its game has a Nursery, which doesn't train" : "Its game has no daycare")}, so it comes back as it was, only happier.";
        var preview = ParkDaycareService.Preview(source.Data, source.Format, exp);
        var grew = preview.LevelAfter > preview.LevelBefore ? $"from level {preview.LevelBefore} to {preview.LevelAfter}" : $"by {preview.ExpGained} EXP";
        if (preview.ExpGained == 0) return $"{hello} It is already as strong as it can get.";
        if (!WritesBack(settings)) return $"{hello} It grew {grew} here. Writing back is off, so that stays in the park.";
        if (stay.Fingerprint is null || stay.Fingerprint != Fingerprint(source.Data))
            return $"{hello} It grew {grew} here, but it was changed in its save since you left it, so the daycare leaves it alone.";
        if (source.BankId is { } id)
        {
            var entry = bank.GetAll().FirstOrDefault(e => e.Id == id);
            if (entry is null || ParkDaycareService.GrowBytes(source.Data, source.Format, exp) is not { } grown) return $"{hello} It grew {grew}, but its Bank entry could not be updated.";
            bank.Replace(id, grown.Data, engine.TryDescribeEntity(grown.Data, entry.Info.SourceName, source.Format) ?? entry.Info);
            return $"{hello} It grew {grew}, and its Bank entry is updated.";
        }
        var written = await boxes.RunMutationAsync(session =>
        {
            var growth = ParkDaycareService.GrowSlot(session, source.Box, source.Slot, exp);
            return new GenerationOutcome(true, $"{mon.Name} grew to level {growth.LevelAfter} at the Poképark daycare.");
        }, source.Slot, refreshSlot: false, changeDescription: $"Poképark daycare: {mon.Name} grew {grew}", action: SaveAction.EditMon);
        return written
            ? $"{hello} It grew {grew}, and its save is updated."
            : $"{hello} It grew {grew} here, but the save could not be written: {boxes.Status}";
    }

    /// <summary>Whether two guests could have an egg, by their real data (false when either can't be read).</summary>
    public bool CanBreed(ParkPokemon a, ParkPokemon b) =>
        park.SourceOf(a) is { } x && park.SourceOf(b) is { } y && ParkDaycareService.CanBreed(x.Data, x.Format, y.Data, y.Format);

    /// <summary>The player takes the egg from Blissey: into the Bank when allowed, otherwise a keepsake.</summary>
    public string TakeEgg(ParkPokemon first, ParkPokemon second, Action<ParkTreasure> keepsake)
    {
        KeepEgg(null);
        var settings = park.Settings;
        if (!WritesBack(settings) || park.SourceOf(first) is not { } a || park.SourceOf(second) is not { } b)
        {
            keepsake(new ParkTreasure($"a daycare egg from {first.Name} and {second.Name}", 3));
            return WritesBack(settings)
                ? $"Blissey hands you the egg. Their saves are not open, so it goes in your bag as a keepsake."
                : $"Blissey hands you the egg. Writing back is off, so it goes in your bag as a keepsake.";
        }
        if (ParkDaycareService.MakeEgg(a.Data, a.Format, b.Data, b.Format, out var why) is not { } egg)
        {
            keepsake(new ParkTreasure($"a daycare egg from {first.Name} and {second.Name}", 3));
            return $"Blissey hands you the egg, but no legal egg fits in their game ({why}). It goes in your bag as a keepsake.";
        }
        var info = engine.TryDescribeEntity(egg.Data, "Poképark daycare", egg.Format);
        if (info is null) return "Blissey hands you the egg, but it could not be read. Nothing was added.";
        var entry = bank.Add(egg.Data, info);
        return $"Blissey hands you the egg, warm from the sun. It's in your Bank, box {entry.Box + 1}, slot {entry.Slot + 1}.";
    }

    private static uint Exp(TimeSpan time) => (uint)Math.Min(uint.MaxValue / 4, Math.Max(0, time.TotalHours) * ExpPerHour);

    private static string Duration(TimeSpan time) => time.TotalHours switch
    {
        < 1 => "less than an hour",
        < 2 => "about an hour",
        < 24 => $"{(int)time.TotalHours} hours",
        < 48 => "a day",
        _ => $"{(int)time.TotalDays} days"
    };

    private static string Fingerprint(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
