using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Services;

/// <summary>One cartridge on the autopilot's route map.</summary>
/// <param name="Out">Pokémon the plan takes out of it.</param>
/// <param name="In">Pokémon the plan puts in (the destination).</param>
/// <param name="Excluded">Left out of the plan (Hardcore, ROM hack, read-only...).</param>
/// <param name="ArtLabel">The label its bundled cartridge art is looked up by (null: the cartridge mark).</param>
public sealed record RouteCart(string Id, string Label, int Generation, string? ColorKey, int Out, int In, bool Excluded, bool IsBank, string? ArtLabel = null);

/// <summary>One Pokémon travelling on the map right now.</summary>
public sealed record RouteTraveler(int Species, int Form, bool Shiny, string FromId, string ToId, bool Evolving);

/// <summary>
/// The lower screen's payload while the autopilot owns it: the cartridges, the destination,
/// and the latest traveler (a new <paramref name="Sequence"/> launches it).
/// </summary>
public sealed record LivingDexRoute(
    IReadOnlyList<RouteCart> Carts,
    string DestinationId,
    string Caption,
    string Progress,
    IReadOnlyList<RouteTraveler> Travelers,
    long Sequence,
    bool Running,
    bool Done);

/// <summary>What the autopilot read from the shelf: planner input plus the files behind it.</summary>
public sealed record LivingDexShelf(
    LivingDexCatalog Catalog,
    IReadOnlyList<LivingDexSource> Sources,
    IReadOnlyDictionary<string, DetectedSave> Saves,
    IReadOnlyDictionary<string, string?> ColorKeys)
{
    /// <summary>The label a source's bundled game art is looked up by (the Bank and unnamed hacks have none).</summary>
    public string? ArtLabel(string id) => Saves.GetValueOrDefault(id)?.ArtLabel;
}

/// <summary>
/// App side of the Living Dex Autopilot: reads every detected save and the Bank off the UI
/// thread into planner sources (deciding which saves are off limits and why), and builds the
/// engine executor over the app's own safe writer, backups, Bank and session service.
/// </summary>
public static class LivingDexAutopilot
{
    private const string BankStartKey = "livingdex_bank_start_box";

    /// <summary>The Bank's living dex region, once a run created it (kept so later runs fill the same boxes).</summary>
    public static int? BankStartBox
    {
        get => Preferences.Default.Get(BankStartKey, -1) is var box and >= 0 ? box : null;
        set
        {
            if (value is { } box) Preferences.Default.Set(BankStartKey, box);
            else Preferences.Default.Remove(BankStartKey);
        }
    }

    public static async Task<LivingDexShelf> ReadShelfAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var services = IPlatformApplication.Current?.Services ?? throw new InvalidOperationException("No services.");
        var picker = services.GetRequiredService<SavePickerViewModel>();
        var access = services.GetRequiredService<ISaveFileAccess>();
        var engine = services.GetRequiredService<ISaveEngine>();
        var writer = services.GetRequiredService<ISafeSaveWriter>();
        var bank = services.GetService<IBankService>();
        var sessions = services.GetService<ISaveSessionService>();

        // Snapshot the shelf on the caller's (UI) thread; everything else runs on the pool.
        var visible = picker.Saves.ToList();
        var hidden = picker.HiddenSaves.ToList();
        var catalog = await Task.Run(LivingDexCatalogBuilder.Build, cancellationToken).ConfigureAwait(false);
        var sources = new List<LivingDexSource>();
        var saves = new Dictionary<string, DetectedSave>(StringComparer.Ordinal);
        var colors = new Dictionary<string, string?>(StringComparer.Ordinal);
        var hardcore = HardcoreMode.IsOn;
        var openId = sessions?.Current?.Document.DocumentId;
        // The live session is UI-owned: take its bytes here, never from the pool thread.
        var openBytes = openId is null ? (ReadOnlyMemory<byte>?)null : sessions?.CurrentSession?.Serialize();

        await Task.Run(async () =>
        {
            foreach (var save in visible.Concat(hidden))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!saves.TryAdd(save.DocumentId, save)) continue;
                colors[save.DocumentId] = save.Identity?.ColorKey;
                // "HeartGold", not "Pokémon HeartGold": every label on the plan and the map is a cartridge name.
                var label = CartridgeName(save.Identity?.DisplayName ?? save.GameLabel);
                progress?.Report($"Reading {label}…");
                if (save.Identity?.IsHidden == true)
                {
                    sources.Add(Excluded(save, label, LivingDexExclusion.Hidden));
                    continue;
                }
                try
                {
                    var bytes = await access.ReadAsync(save.DocumentId, cancellationToken).ConfigureAwait(false);
                    using var session = engine.OpenSession(bytes, save.EngineHint, save.Format);
                    var (exclusion, reason) = Judge(save, session, bytes, writer, hardcore, openId, openBytes);
                    sources.Add(LivingDexSources.FromSession(save.DocumentId, label, session, bytes.Span, exclusion, reason, Caution(save)));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    sources.Add(Excluded(save, label, LivingDexExclusion.Unreadable, $"Could not be read: {error.Message}"));
                }
            }
            if (bank is not null) sources.Add(LivingDexSources.FromBank(bank));
        }, cancellationToken).ConfigureAwait(false);
        return new LivingDexShelf(catalog, sources, saves, colors);
    }

    /// <summary>"Pokémon HeartGold" → "HeartGold" (any spelling of the é, composed or not).</summary>
    public static string CartridgeName(string label)
    {
        var trimmed = System.Text.RegularExpressions.Regex.Replace(label.Normalize(System.Text.NormalizationForm.FormC), @"(?i)^pok[eé]mon\s+", "").Trim();
        return trimmed.Length > 0 ? trimmed : label;
    }

    private static LivingDexSource Excluded(DetectedSave save, string label, LivingDexExclusion exclusion, string? reason = null) =>
        new(save.DocumentId, label, LivingDexSourceKind.Save, save.Generation, 0, [], exclusion, reason ?? LivingDexPlanner.Describe(exclusion));

    /// <summary>The save-identity rules, in the order the player would want to hear them.</summary>
    private static (LivingDexExclusion, string?) Judge(DetectedSave save, ISaveEngineSession session, ReadOnlyMemory<byte> bytes,
        ISafeSaveWriter writer, bool hardcore, string? openId, ReadOnlyMemory<byte>? openBytes)
    {
        if (hardcore) return (LivingDexExclusion.Hardcore, null);
        var hack = save.Format is SaveFormat.Unbound or SaveFormat.RadicalRed or SaveFormat.GsChronicles
            || save.Identity?.GameChoiceId?.StartsWith("hack", StringComparison.Ordinal) == true
            || session is not SaveEngineSession
            || writer.LayoutRiskOf(save.DocumentId, session.Snapshot) is not null
            || session.GameNames.Any(n => n.Contains("Luminescent", StringComparison.OrdinalIgnoreCase) || n.Contains("Compass", StringComparison.OrdinalIgnoreCase));
        if (hack) return (LivingDexExclusion.RomHack, null);
        if (writer.WhyWritesAreRefused(save.DocumentId, session.Snapshot) is { } refusal) return (LivingDexExclusion.ReadOnly, refusal);
        if (!session.SupportsBoxTools) return (LivingDexExclusion.Unsupported, null);
        // The open save may hold edits PKForge has not written: the file is not the truth then.
        if (save.DocumentId == openId && openBytes is { } live && !live.Span.SequenceEqual(bytes.Span))
            return (LivingDexExclusion.UnsavedChanges, null);
        return (LivingDexExclusion.None, null);
    }

    /// <summary>No API tells whether an emulator holds the file; a fresh write is the tell.</summary>
    private static string? Caution(DetectedSave save)
    {
        if (save.RequiresExtraCare) return $"{save.Emulator} console storage: close the emulator before applying.";
        if (save.LastModified is { } modified && DateTimeOffset.UtcNow - modified < TimeSpan.FromMinutes(10))
            return $"Saved {Math.Max(1, (int)(DateTimeOffset.UtcNow - modified).TotalMinutes)} min ago: close the game in the emulator first.";
        return null;
    }

    public static LivingDexExecutor CreateExecutor()
    {
        var services = IPlatformApplication.Current?.Services ?? throw new InvalidOperationException("No services.");
        return new LivingDexExecutor(
            services.GetRequiredService<ISaveEngine>(),
            services.GetRequiredService<ISafeSaveWriter>(),
            services.GetRequiredService<ISaveFileAccess>(),
            services.GetService<IBankService>(),
            services.GetRequiredService<IEvolutionService>(),
            services.GetService<ILegalityService>(),
            services.GetService<ISaveSessionService>());
    }

    /// <summary>The map's cartridges for a plan: destination, every source it takes from, then the rest.</summary>
    public static IReadOnlyList<RouteCart> Carts(LivingDexPlan plan, LivingDexShelf shelf)
    {
        var outgoing = plan.OutgoingBySource;
        var incoming = plan.Steps.Count(s => s.Fills);
        return [.. plan.Sources
            .Select(s => new RouteCart(s.Id, s.Label, s.Generation, shelf.ColorKeys.GetValueOrDefault(s.Id),
                outgoing.GetValueOrDefault(s.Id), s.Id == plan.Options.DestinationId ? incoming : 0, s.IsExcluded,
                s.Kind == LivingDexSourceKind.Bank, shelf.Saves.GetValueOrDefault(s.Id)?.ArtLabel))
            .OrderBy(c => c.Id == plan.Options.DestinationId ? 0 : c.Out > 0 ? 1 : c.Excluded ? 3 : 2)
            .ThenByDescending(c => c.Out)];
    }
}
