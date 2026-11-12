using System.Security.Cryptography;
using System.Text.Json;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;
using PKForge.Infrastructure;

namespace PKForge.App.Services;

/// <summary>
/// The PKForge Pokéwalker: one Poképark resident at a time on a stroll, its steps from the phone's
/// pedometer (or stroll mode), the watts, the Poké Radar and Dowsing (<see cref="WalkerRules"/>),
/// and coming home: legal EXP and friendship written back as the Poképark daycare does (same
/// write-back setting, fingerprint check, restore point), catches to the Bank, finds to the Item
/// Bank. Kept in <c>walker/walk.json</c>, mirrored as <c>Walker/walk.json</c> in the PKForge folder.
/// </summary>
public sealed class PokewalkerStrolls(PokeparkService park, IBankService bank, ISaveEngine engine, ISaveSessionService saves,
    BoxBrowserViewModel boxes, ItemBankStore items)
{
    public static string Folder => Path.Combine(FileSystem.AppDataDirectory, "walker");
    public static string FilePath => Path.Combine(Folder, "walk.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly Lock _gate = new();

    public WalkerState Load()
    {
        lock (_gate)
        {
            try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<WalkerState>(File.ReadAllText(FilePath)) ?? new() : new(); }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException) { return new(); }
        }
    }

    public void Save(WalkerState state)
    {
        lock (_gate)
        {
            AtomicFile.WriteText(FilePath, JsonSerializer.Serialize(state, Json));
        }
        IPlatformApplication.Current?.Services.GetService<PkforgeFolder>()?.RequestSync(TimeSpan.FromSeconds(3));
    }

    /// <summary>Whether steps come from the pedometer (true) or stroll mode.</summary>
    public static bool Pedometer(WalkerState state) => state.SensorLast is not null;

    /// <summary>
    /// <paramref name="state"/> with the steps since its last look, from the pedometer or the stroll
    /// clock; the same instance when there is nothing new. Nothing is saved: the caller saves it
    /// with its own writes, in order. Steps are counted from the anchors the state keeps (the last
    /// pedometer reading, the last stroll), so a count the caller drops is counted again next time.
    /// </summary>
    public static async Task<WalkerState> CountAsync(WalkerState state)
    {
        if (state.Walk is null) return state;
        if (Pedometer(state))
        {
            if (!await WalkerSteps.IsAllowedAsync().ConfigureAwait(false)
                || await WalkerSteps.ReadAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false) is not { } now)
                return state;
            var delta = WalkerRules.SensorDelta(state.SensorLast, now);
            return WalkerRules.AddSteps(state, (int)Math.Min(int.MaxValue, delta)) with { SensorLast = now };
        }
        return WalkerRules.Stroll(state, DateTimeOffset.Now);
    }

    /// <summary>The walk file with its steps counted, saved: what coming home and a sync start from.</summary>
    private async Task<WalkerState> CountAndSaveAsync()
    {
        var state = await CountAsync(await Task.Run(Load).ConfigureAwait(false)).ConfigureAwait(false);
        await Task.Run(() => Save(state)).ConfigureAwait(false);
        return state;
    }

    /// <summary>Sends a resident on a walk on a course. The pedometer is used when the phone has
    /// one and the player allowed it; otherwise stroll mode.</summary>
    public async Task<WalkerState> StartAsync(ParkPokemon mon, WalkerCourse course, bool pedometer)
    {
        await ResumeAsync();
        var (state, source) = await Task.Run(() => (Load(), park.SourceOf(mon))).ConfigureAwait(false);
        var types = HabitatCatalog.TypesFor(mon.Species, mon.Form);
        var reading = pedometer ? await WalkerSteps.ReadAsync(TimeSpan.FromSeconds(2)) : null;
        state = WalkerRules.Start(state, course, mon.Id, mon.Name, mon.Species, mon.Form, mon.Shiny,
            source is null ? null : Fingerprint(source.Data), types, (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue), DateTimeOffset.Now)
            with { SensorLast = reading, StrollLast = DateTimeOffset.Now };
        var started = state;
        await Task.Run(() => Save(started)).ConfigureAwait(false);
        return state;
    }

    /// <summary>The walk ends: catches to the Bank, finds to the Item Bank, EXP and friendship to
    /// the Pokémon when allowed. Returns what happened, in a few sentences. The walk is cleared in
    /// the same write that records the homecoming, before anything is banked, and every step is
    /// saved as it lands (<see cref="WalkerHomecomings"/>), so a homecoming is never applied twice.</summary>
    public async Task<string> ComeHomeAsync(ParkPokemon? mon)
    {
        var counted = await CountAndSaveAsync();
        if (counted.Homecoming is null)
        {
            if (counted.Walk is null) throw new InvalidOperationException("Nobody is on a walk.");
            var source = mon is null ? null : park.SourceOf(mon);
            var trainer = WalkerService.TrainerFor(saves.CurrentSession, source?.Data, source?.Format);
            counted = await Task.Run(() => WalkerHomecomings.Begin(counted, trainer));
            var begun = counted;
            await Task.Run(() => Save(begun));
        }
        return await FinishAsync(counted, mon);
    }

    /// <summary>Finishes a homecoming cut short (the app was closed while it was banking); null when there is none.</summary>
    public async Task<string?> ResumeAsync()
    {
        var state = await Task.Run(Load);
        if (state.Homecoming is not { } pending) return null;
        var mon = await Task.Run(() => park.LoadRoster().FirstOrDefault(r => r.Id == pending.Walk.ResidentId));
        return await FinishAsync(state, mon);
    }

    private async Task<string> FinishAsync(WalkerState state, ParkPokemon? mon)
    {
        // Legality work and file writes, off the UI thread.
        var (banked, lines) = await Task.Run(() => WalkerHomecomings.Bank(state, bank,
            (data, format) => engine.TryDescribeEntity(data, WalkerHomecomings.SourceName, format), items, Save));
        var pending = banked.Homecoming!;
        // Growing is applied once by itself: the Pokémon's fingerprint changes when it grows.
        lines.Insert(0, await GrowAsync(mon, pending.Walk, pending.Home));
        await Task.Run(() => Save(banked with { Homecoming = null })).ConfigureAwait(false);
        return string.Join(" ", lines);
    }

    private async Task<string> GrowAsync(ParkPokemon? mon, WalkerWalk walk, WalkerHomecoming home)
    {
        var hello = $"{walk.Name} is back after {home.Steps:N0} steps.";
        if (mon is null || park.SourceOf(mon) is not { } source) return $"{hello} Its save is not open, so the walk stays a memory.";
        if (!park.Settings.DaycareWritesBack) return $"{hello} Writing back to saves is off (Poképark daycare), so the walk stays a memory.";
        if (walk.Fingerprint is null || walk.Fingerprint != Fingerprint(source.Data))
            return $"{hello} It was changed in its save since it left, so the Pokéwalker leaves it alone.";
        if (source.BankId is { } id)
        {
            var entry = bank.GetAll().FirstOrDefault(e => e.Id == id);
            if (entry is null) return $"{hello} Its Bank entry is gone.";
            var why = "";
            if (await Task.Run(() => WalkerService.ComeHome(source.Data, source.Format, home, out why)) is not { } grown) return $"{hello} {why}";
            bank.Replace(id, grown.Data, engine.TryDescribeEntity(grown.Data, entry.Info.SourceName, source.Format) ?? entry.Info);
            return $"{hello} {Grew(grown.Growth)} Its Bank entry is updated.";
        }
        var message = "";
        var written = await boxes.RunMutationAsync(session =>
        {
            var growth = WalkerService.ComeHomeSlot(session, source.Box, source.Slot, home, out var why);
            message = growth is null ? why : Grew(growth);
            return new GenerationOutcome(growth is not null, message);
        }, source.Slot, refreshSlot: false, changeDescription: $"Pokéwalker: {walk.Name} came home", action: SaveAction.EditMon);
        return written ? $"{hello} {message} Its save is updated." : $"{hello} {message}";
    }

    private static string Grew(WalkerGrowth growth)
    {
        var grew = growth.LevelAfter > growth.LevelBefore ? $"It grew to level {growth.LevelAfter}"
            : growth.ExpGained > 0 ? $"It gained {growth.ExpGained:N0} EXP" : null;
        return grew is null ? "It feels closer to you." : grew + (growth.FriendshipGained > 0 ? " and feels closer to you." : ".");
    }

    /// <summary>Syncs with the open HeartGold/SoulSilver save (steps, watts, one course), and learns its courses and National Dex.</summary>
    public async Task<string> SyncAsync()
    {
        var state = await CountAndSaveAsync();
        WalkerSync? sync = null;
        var written = await boxes.RunMutationAsync(session =>
        {
            sync = WalkerService.Sync(session, state);
            return new GenerationOutcome(true, "Synced with the Pokéwalker.");
        }, 0, refreshSlot: false, changeDescription: "Pokéwalker: sync", action: SaveAction.EditWorld);
        if (!written || sync is null) return $"The save could not be written: {boxes.Status}";
        Save(Load() with { UnsyncedSteps = 0, SyncedCourses = sync.SaveCourses, NationalDex = sync.NationalDex || state.NationalDex });
        var course = sync.CourseOpened is { } opened ? $" {WalkerCourses.Find(opened)?.Name} is now open in the game too." : "";
        return $"Synced: {sync.StepsAdded:N0} steps added to the game's Pokéwalker, {sync.Watts:N0} watts.{course}";
    }

    public static bool CanSync(ISaveEngineSession? session) => session is not null && PokewalkerService.IsSupported(session);

    private static string Fingerprint(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
