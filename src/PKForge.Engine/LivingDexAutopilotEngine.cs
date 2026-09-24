using System.Security.Cryptography;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// The autopilot's game knowledge, read from PKHeX's own tables: which species and
/// collectible forms exist, which plain link trades evolve what, and what a given save's
/// game can store. Built once and cached (the tables are static).
/// </summary>
public static class LivingDexCatalogBuilder
{
    private static readonly Lazy<LivingDexCatalog> Cached = new(BuildCore);

    public static LivingDexCatalog Build() => Cached.Value;

    /// <summary>What the save's game can hold, detached from the session (safe after it is disposed).</summary>
    public static Func<int, int, bool>? StorableIn(ISaveEngineSession session)
    {
        if (session is not SaveEngineSession engine) return null;
        var personal = engine.SaveFile.Personal;
        var max = engine.SaveFile.MaxSpeciesID;
        return (species, form) => species > 0 && species <= max && personal.IsPresentInGame((ushort)species, (byte)form);
    }

    private static LivingDexCatalog BuildCore()
    {
        var names = GameInfo.GetStrings("en").specieslist;
        var species = Enumerable.Range(1, Math.Min(CollectionDex.MaxSpecies, names.Length - 1))
            .Where(id => names[id].Length > 0)
            .ToList();

        // Forms: the union of storable forms over the mainline tables, newest first. Battle-only
        // faces (Mega, Gigantamax, Primal...), fusions and Totems are never a living dex slot.
        (IPersonalTable Table, byte Format)[] tables =
        [
            (PersonalTable.SV, 9), (PersonalTable.SWSH, 8), (PersonalTable.USUM, 7),
            (PersonalTable.AO, 6), (PersonalTable.B2W2, 5), (PersonalTable.HGSS, 4),
        ];
        var forms = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var id in species)
        {
            var set = new SortedSet<int>();
            foreach (var (table, format) in tables)
            {
                if (id > table.MaxSpeciesID || !table.IsSpeciesInGame((ushort)id)) continue;
                var count = table.GetFormEntry((ushort)id, 0).FormCount;
                for (byte form = 0; form < count; form++)
                {
                    if (!table.IsPresentInGame((ushort)id, form)) continue;
                    if (FormInfo.IsBattleOnlyForm((ushort)id, form, format)) continue;
                    if (FormInfo.IsFusedForm((ushort)id, form, format)) continue;
                    if (FormInfo.IsTotemForm((ushort)id, form)) continue;
                    set.Add(form);
                }
            }
            if (set.Count > 1) forms[id] = [.. set];
        }

        // Plain link trades (no held item, no partner): the evolutions the autopilot performs.
        var trades = new HashSet<LivingDexTradeEvolution>();
        foreach (var context in new[] { EntityContext.Gen1, EntityContext.Gen2, EntityContext.Gen3, EntityContext.Gen4,
                     EntityContext.Gen5, EntityContext.Gen6, EntityContext.Gen7, EntityContext.Gen8, EntityContext.Gen9 })
        {
            EvolutionTree tree;
            try { tree = EvolutionTree.GetEvolutionTree(context); }
            catch (ArgumentOutOfRangeException) { continue; }
            foreach (var id in species)
            {
                foreach (var form in forms.GetValueOrDefault(id) ?? [0])
                {
                    foreach (var method in tree.Forward.GetForward((ushort)id, (byte)form).Span)
                    {
                        if (method.Method != EvolutionType.Trade || method.Species == 0) continue;
                        trades.Add(new LivingDexTradeEvolution(id, form, method.Species, method.GetDestinationForm((byte)form)));
                    }
                }
            }
        }
        return new LivingDexCatalog(species, forms, [.. trades.OrderBy(t => t.To).ThenBy(t => t.ToForm).ThenBy(t => t.From)]);
    }
}

/// <summary>Turns saves and the Bank into what the planner reads.</summary>
public static class LivingDexSources
{
    public static string Fingerprint(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>Every Pokémon in the save (party included, flagged), its free box slots and storable set.</summary>
    public static LivingDexSource FromSession(string id, string label, ISaveEngineSession session, ReadOnlySpan<byte> bytes,
        LivingDexExclusion exclusion = LivingDexExclusion.None, string? reason = null, string? caution = null)
    {
        var snapshot = session.Snapshot;
        var holdings = new List<LivingDexHolding>();
        var free = new List<SlotRef>();
        foreach (var slot in snapshot.Slots)
        {
            if (slot.Species is not > 0)
            {
                if (slot.Box >= 0) free.Add(new SlotRef(slot.Box, slot.Slot));
                continue;
            }
            holdings.Add(new LivingDexHolding(slot.Species.Value, slot.Form, slot.IsShiny, slot.Box, slot.Slot,
                session.Generation, slot.IsEgg, slot.IsLegal, Nickname: slot.Nickname));
        }
        free.Sort((a, b) => a.Box != b.Box ? a.Box.CompareTo(b.Box) : a.Slot.CompareTo(b.Slot));
        return new LivingDexSource(id, label, LivingDexSourceKind.Save, session.Generation, session.MaxSpeciesId, holdings,
            exclusion, reason, free, LivingDexCatalogBuilder.StorableIn(session), Fingerprint: Fingerprint(bytes), Caution: caution);
    }

    public static LivingDexSource FromBank(IBankService bank) => new(
        LivingDexPlanner.BankId, "Bank", LivingDexSourceKind.Bank, 0, CollectionDex.MaxSpecies,
        [.. bank.GetAll().Where(e => e.Info.Species > 0).Select(e => new LivingDexHolding(
            e.Info.Species, e.Info.Form, e.Info.Shiny, e.Box, e.Slot, e.Info.Generation, BankId: e.Id, Nickname: e.Info.Nickname))],
        BoxCount: bank.BoxCount);
}

public enum LivingDexPhase
{
    Reading,
    Staging,
    Writing,
    RollingBack,
    Finished,
}

public enum LivingDexStepStatus
{
    /// <summary>Converted in memory; nothing written yet.</summary>
    Staged,
    /// <summary>Written.</summary>
    Done,
    /// <summary>Could not be done; the Pokémon stays where it was.</summary>
    Failed,
}

/// <summary>What happened to one step, with the conversion's own report.</summary>
/// <param name="Legal">Legality of the landed Pokémon; null when unknown (Bank, no analyzer).</param>
public sealed record LivingDexStepOutcome(
    LivingDexStep Step, LivingDexStepStatus Status, string Message, IReadOnlyList<string> Warnings, bool? Legal);

/// <summary>Live progress for the route map: which Pokémon travels from where to where.</summary>
public sealed record LivingDexProgress(LivingDexPhase Phase, int Done, int Total, string Message,
    LivingDexStep? Step = null, string? SaveId = null);

/// <summary>The end of a run.</summary>
/// <param name="BackupIds">Restore point per written save (document id → backup id).</param>
public sealed record LivingDexRunResult(
    bool Committed,
    bool RolledBack,
    string Summary,
    IReadOnlyList<LivingDexStepOutcome> Outcomes,
    IReadOnlyDictionary<string, string> BackupIds,
    int CoveredBefore,
    int CoveredAfter,
    string? Error = null);

/// <summary>A plan the executor refuses to start or continue (nothing, or everything, was undone).</summary>
public sealed class LivingDexAbortException(string message) : InvalidOperationException(message);

/// <summary>
/// A plan staged in memory: every touched save open as a session with the moves applied,
/// nothing written. The dry run is exactly this (then disposed); Apply commits it.
/// </summary>
public sealed class LivingDexStaging : IDisposable
{
    internal sealed class SaveState(DetectedSave save, ISaveEngineSession session, byte[] original)
    {
        public DetectedSave Save { get; } = save;
        public ISaveEngineSession Session { get; } = session;
        public byte[] Original { get; } = original;
        public SaveSnapshot Snapshot { get; } = session.Snapshot;
        public HashSet<SlotRef> Scope { get; } = [];
        public List<int> PartyReleases { get; } = [];
        public int Incoming { get; set; }
        public int Outgoing { get; set; }
    }

    internal sealed record BankAdd(byte[] Data, BankEntryInfo Info, SlotRef Slot, LivingDexStep Step);

    internal LivingDexStaging(LivingDexPlan plan) => Plan = plan;

    public LivingDexPlan Plan { get; }
    internal Dictionary<string, SaveState> Saves { get; } = new(StringComparer.Ordinal);
    internal List<BankAdd> BankAdds { get; } = [];
    internal List<(Guid Id, SlotRef From, SlotRef To)> BankArranges { get; } = [];
    internal List<Guid> BankRemovals { get; } = [];
    internal List<LivingDexStepOutcome> Outcomes { get; } = [];

    public IReadOnlyList<LivingDexStepOutcome> StepOutcomes => Outcomes;
    public int Staged => Outcomes.Count(o => o.Status == LivingDexStepStatus.Staged && o.Step.Fills);
    public int Failed => Outcomes.Count(o => o.Status == LivingDexStepStatus.Failed);
    public int IllegalResults => Outcomes.Count(o => o.Legal == false);
    public bool Committed { get; internal set; }

    public void Dispose()
    {
        foreach (var state in Saves.Values) state.Session.Dispose();
        Saves.Clear();
    }
}

/// <summary>
/// Executes a <see cref="LivingDexPlan"/> safely.
/// <list type="number">
/// <item>Stage: re-read every touched save, refuse any whose bytes changed since the plan
/// (an emulator wrote it) or whose writes the safe writer refuses; perform every move in
/// memory (trade evolutions on a scratch copy of the source, conversions with their warnings,
/// a legality verdict for each landed Pokémon). A failing step is skipped and the Pokémon
/// stays home.</item>
/// <item>Commit: each save written exactly once through <see cref="ISafeSaveWriter"/> with a
/// scope of exactly the slots the plan touched (which creates that save's restore point
/// first); destination before sources, so a crash can duplicate but never lose a Pokémon;
/// Bank changes after the saves they depend on.</item>
/// <item>Any failure or a cancel once writing started rolls every written save back to its
/// original bytes (through the writer's unrestricted restore path) and undoes the Bank.</item>
/// </list>
/// </summary>
public sealed class LivingDexExecutor(
    ISaveEngine engine,
    ISafeSaveWriter writer,
    ISaveFileAccess access,
    IBankService? bank,
    IEvolutionService evolutions,
    ILegalityService? legality = null,
    ISaveSessionService? sessions = null)
{
    private const string Description = "Living Dex Autopilot";

    public async Task<LivingDexStaging> StageAsync(LivingDexPlan plan, IReadOnlyDictionary<string, DetectedSave> saves,
        IProgress<LivingDexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(saves);
        var staging = new LivingDexStaging(plan);
        try
        {
            await StageCoreAsync(staging, saves, progress, cancellationToken).ConfigureAwait(false);
            return staging;
        }
        catch
        {
            staging.Dispose();
            throw;
        }
    }

    /// <summary>Stage and commit in one go (the path tests and one-tap apply use).</summary>
    public async Task<LivingDexRunResult> ApplyAsync(LivingDexPlan plan, IReadOnlyDictionary<string, DetectedSave> saves,
        IProgress<LivingDexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var staging = await StageAsync(plan, saves, progress, cancellationToken).ConfigureAwait(false);
        return await CommitAsync(staging, progress, cancellationToken).ConfigureAwait(false);
    }

    private async Task StageCoreAsync(LivingDexStaging staging, IReadOnlyDictionary<string, DetectedSave> saves,
        IProgress<LivingDexProgress>? progress, CancellationToken cancellationToken)
    {
        var plan = staging.Plan;
        var touched = plan.TouchedSaves;
        var fingerprints = plan.Sources.ToDictionary(s => s.Id, s => s.Fingerprint, StringComparer.Ordinal);
        for (var i = 0; i < touched.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = touched[i];
            if (!saves.TryGetValue(id, out var save))
                throw new LivingDexAbortException($"A save in the plan is no longer on the shelf ({id}).");
            progress?.Report(new LivingDexProgress(LivingDexPhase.Reading, i, touched.Count, $"Reading {save.GameLabel}…", SaveId: id));
            var bytes = (await access.ReadAsync(save.DocumentId, cancellationToken).ConfigureAwait(false)).ToArray();
            if (fingerprints.GetValueOrDefault(id) is { } expected && !string.Equals(expected, LivingDexSources.Fingerprint(bytes), StringComparison.OrdinalIgnoreCase))
                throw new LivingDexAbortException(
                    $"{save.GameLabel} changed since the plan was made - is it open in an emulator? Close the game and plan again.");
            var session = engine.OpenSession(bytes, save.EngineHint, save.Format);
            var state = new LivingDexStaging.SaveState(save, session, bytes);
            staging.Saves[id] = state;
            if (writer.WhyWritesAreRefused(save.DocumentId, state.Snapshot) is { } refusal)
                throw new LivingDexAbortException($"{save.GameLabel}: {refusal}");
        }

        var dest = plan.DestinationKind == LivingDexSourceKind.Save ? staging.Saves.GetValueOrDefault(plan.Options.DestinationId) : null;
        var slotsByKey = staging.Saves.ToDictionary(p => p.Key,
            p => p.Value.Snapshot.Slots.ToDictionary(s => new SlotRef(s.Box, s.Slot)), StringComparer.Ordinal);
        var bankFacts = bank?.GetAll().ToDictionary(e => e.Id) ?? [];
        var labels = plan.Sources.ToDictionary(s => s.Id, s => s.Label, StringComparer.Ordinal);

        var runnable = plan.Steps.Where(s => s.IsRunnable).ToList();
        for (var i = 0; i < runnable.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var step = runnable[i];
            progress?.Report(new LivingDexProgress(LivingDexPhase.Staging, i, runnable.Count, StepLine(step, labels), step, step.SourceId));

            if (step.Kind == LivingDexStepKind.Arrange)
            {
                var holding = step.Holding!;
                if (holding.BankId is not { } arrangeId || !bankFacts.ContainsKey(arrangeId))
                {
                    staging.Outcomes.Add(Fail(step, "That Bank entry is gone."));
                    continue;
                }
                staging.BankArranges.Add((arrangeId, new SlotRef(holding.Box, holding.Slot), step.Destination!.Value));
                staging.Outcomes.Add(new LivingDexStepOutcome(step, LivingDexStepStatus.Staged, "Sorted into the living dex boxes.", [], null));
                continue;
            }

            try
            {
                staging.Outcomes.Add(StageFill(staging, step, dest, slotsByKey, bankFacts, labels));
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                staging.Outcomes.Add(Fail(step, error.Message));
            }
        }
        foreach (var state in staging.Saves.Values)
            foreach (var slot in state.PartyReleases.OrderByDescending(s => s))
                state.Session.ReleaseSlot(-1, slot);
        progress?.Report(new LivingDexProgress(LivingDexPhase.Staging, runnable.Count, runnable.Count, "Dry run complete."));
    }

    private LivingDexStepOutcome StageFill(LivingDexStaging staging, LivingDexStep step, LivingDexStaging.SaveState? dest,
        Dictionary<string, Dictionary<SlotRef, SlotSummary>> slotsByKey, Dictionary<Guid, BankEntry> bankFacts,
        Dictionary<string, string> labels)
    {
        var holding = step.Holding!;
        var sourceLabel = labels.GetValueOrDefault(step.SourceId!) ?? "?";
        byte[] bytes;
        string? format; // the exact entity format: same-size formats cannot be told apart from bytes
        LivingDexStaging.SaveState? source = null;
        Guid? bankId = null;

        // ── Take the Pokémon (verify it is still exactly the one planned) ──
        if (step.SourceId == LivingDexPlanner.BankId)
        {
            if (bank is null || holding.BankId is not { } id || !bankFacts.TryGetValue(id, out var fact)
                || fact.Info.Species != holding.Species || fact.Info.Shiny != holding.Shiny)
                return Fail(step, "That Bank entry changed since the plan.");
            bytes = bank.GetData(id);
            format = fact.Info.Format;
            bankId = id;
        }
        else
        {
            source = staging.Saves[step.SourceId!];
            var at = new SlotRef(holding.Box, holding.Slot);
            if (!slotsByKey[step.SourceId!].TryGetValue(at, out var slot) || slot.Species != holding.Species
                || slot.IsShiny != holding.Shiny || slot.Form != holding.Form || slot.IsEgg)
                return Fail(step, $"{sourceLabel} no longer has that Pokémon there.");
            var export = source.Session.ExportSlot(holding.Box, holding.Slot);
            (bytes, format) = (export.Data, export.Format);
        }

        // ── Trade evolution, on a scratch copy so a failure leaves the source untouched ──
        if (step.Kind == LivingDexStepKind.EvolveAndMove)
        {
            var evolved = Evolve(source, bytes, format, step, staging.Plan.Options.Forms, out var why);
            if (evolved is null) return Fail(step, why ?? "The trade evolution is not possible in its game.");
            (bytes, format) = (evolved.Data, evolved.Format ?? format);
        }

        // ── Land it ──
        IReadOnlyList<string> warnings = [];
        bool? legal = null;
        if (dest is not null)
        {
            var landing = step.Destination!.Value;
            if (!slotsByKey[staging.Plan.Options.DestinationId].TryGetValue(landing, out var target)
                || target.Species is > 0 || !dest.Scope.Add(landing))
                return Fail(step, $"Box {landing.Box + 1} slot {landing.Slot + 1} of {dest.Save.GameLabel} is not free any more.");
            string? refusal = null;
            bool landed;
            if (dest.Session is SaveEngineSession engineSession)
            {
                // The reporting path: downgrades allowed (the plan already flagged them), every compromise listed.
                var conversion = engineSession.ImportSlotWithReport(landing.Box, landing.Slot, bytes, out refusal, format);
                landed = conversion is not null;
                if (conversion is not null) warnings = conversion.Warnings;
            }
            else
            {
                landed = dest.Session.ImportSlot(landing.Box, landing.Slot, bytes, format);
            }
            if (!landed)
            {
                dest.Scope.Remove(landing);
                return Fail(step, refusal ?? TransferCompatibility.ExplainRefusal(bytes, holding.Nickname ?? "It", dest.Snapshot.Format, dest.Snapshot.Generation, dest.Save.GameLabel, format)
                    ?? $"It cannot enter {dest.Save.GameLabel}.");
            }
            if (legality is not null && dest.Session.SupportsLegalityAnalysis)
            {
                try { legal = legality.Analyze(dest.Session, landing.Box, landing.Slot).Valid; }
                catch (Exception) { legal = null; }
            }
            dest.Incoming++;
        }
        else
        {
            if (bank is null) return Fail(step, "The Bank is not available.");
            var info = engine.TryDescribeEntity(bytes, $"{Description} · {sourceLabel}", format);
            if (info is null) return Fail(step, "Its data could not be read.");
            staging.BankAdds.Add(new LivingDexStaging.BankAdd(bytes, info, step.Destination!.Value, step));
        }

        // ── Release the original: a move, never a copy ──
        if (source is not null)
        {
            // The party compacts on release, which would shift every later party index:
            // party releases wait until every Pokémon of this run has been read.
            if (holding.IsParty) source.PartyReleases.Add(holding.Slot);
            else source.Session.ReleaseSlot(holding.Box, holding.Slot);
            source.Scope.Add(new SlotRef(holding.Box, holding.Slot));
            source.Outgoing++;
        }
        else if (bankId is { } removal)
        {
            staging.BankRemovals.Add(removal);
        }

        var message = step.Kind == LivingDexStepKind.EvolveAndMove ? "Evolved by trade and moved." : "Moved.";
        return new LivingDexStepOutcome(step, LivingDexStepStatus.Staged, message, warnings, legal);
    }

    /// <summary>Evolves by trade in a scratch copy of the Pokémon's own save (its trainer, its
    /// game's rules), or an entity session for a Bank entry; returns the evolved bytes.</summary>
    private SlotExport? Evolve(LivingDexStaging.SaveState? source, byte[] bytes, string? format, LivingDexStep step, bool forms, out string? why)
    {
        why = null;
        ISaveEngineSession? scratch;
        int box, slot;
        if (source is not null)
        {
            scratch = engine.OpenSession(source.Session.Serialize().ToArray(), source.Save.EngineHint, source.Save.Format);
            (box, slot) = (step.Holding!.Box, step.Holding.Slot);
        }
        else
        {
            scratch = engine.OpenEntitySession(bytes, format: format);
            (box, slot) = (0, 0);
        }
        if (scratch is null) { why = "Its data could not be read."; return null; }
        using (scratch)
        {
            var card = evolutions.Plan(scratch, box, slot, hax: false);
            var option = card.Options.FirstOrDefault(o => o.IsTrade && o.Available && o.Species == step.Species
                && (!forms || o.Form == step.Form));
            if (option is null)
            {
                why = card.Unavailable ?? $"It cannot evolve into species #{step.Species} by a plain trade in its game.";
                return null;
            }
            var outcome = evolutions.Evolve(scratch, box, slot, new EvolutionRequest(option.Id));
            if (!outcome.Success) { why = outcome.Message; return null; }
            return scratch.ExportSlot(box, slot);
        }
    }

    public async Task<LivingDexRunResult> CommitAsync(LivingDexStaging staging, IProgress<LivingDexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staging);
        if (staging.Committed) throw new InvalidOperationException("This plan was already applied.");
        var plan = staging.Plan;
        var written = new List<(LivingDexStaging.SaveState State, byte[] Candidate)>();
        var backups = new Dictionary<string, string>(StringComparer.Ordinal);
        var bankAdded = new List<Guid>();
        var bankArranged = false;

        // Destination first: from here on a crash can leave a duplicate, never a lost Pokémon.
        var order = staging.Saves.Values
            .OrderBy(s => s.Save.DocumentId == plan.Options.DestinationId ? 0 : 1)
            .Where(s => s.Scope.Count > 0)
            .ToList();
        var total = order.Count + (staging.BankAdds.Count + staging.BankRemovals.Count + staging.BankArranges.Count > 0 ? 1 : 0);
        var done = 0;
        try
        {
            if (plan.DestinationKind == LivingDexSourceKind.Bank)
            {
                // The Bank is the destination: it receives before any save lets go.
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new LivingDexProgress(LivingDexPhase.Writing, done, total, "Filling the Bank's living dex boxes…", SaveId: LivingDexPlanner.BankId));
                ArrangeBank(staging);
                bankArranged = staging.BankArranges.Count > 0;
                foreach (var add in staging.BankAdds)
                {
                    var entry = bank!.Add(add.Data, add.Info);
                    bankAdded.Add(entry.Id);
                    bank.Move(entry.Id, add.Slot.Box, add.Slot.Slot);
                }
                done++;
            }

            foreach (var state in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new LivingDexProgress(LivingDexPhase.Writing, done, total, $"Writing {state.Save.GameLabel}…", SaveId: state.Save.DocumentId));
                var current = await access.ReadAsync(state.Save.DocumentId, cancellationToken).ConfigureAwait(false);
                if (!current.Span.SequenceEqual(state.Original))
                    throw new LivingDexAbortException($"{state.Save.GameLabel} changed on disk during the run - is it open in an emulator?");
                var candidate = state.Session.Serialize().ToArray();
                var receipt = await writer.WriteScopedAsync(state.Save.DocumentId, state.Snapshot, candidate,
                    new WriteScope([.. state.Scope]), ChangeLine(state), CancellationToken.None).ConfigureAwait(false);
                if (receipt.Changed)
                {
                    written.Add((state, candidate));
                    backups[state.Save.DocumentId] = receipt.BackupId;
                    sessions?.MarkWritten(state.Save.DocumentId, candidate);
                }
                done++;
            }

            if (plan.DestinationKind == LivingDexSourceKind.Save && staging.BankRemovals.Count > 0)
            {
                // Bank → save: the Bank lets go only once the destination holds the Pokémon.
                progress?.Report(new LivingDexProgress(LivingDexPhase.Writing, done, total, "Releasing moved Bank entries…", SaveId: LivingDexPlanner.BankId));
                bank!.RemoveMany(staging.BankRemovals);
                done++;
            }
        }
        catch (Exception error)
        {
            var cancelled = error is OperationCanceledException;
            var rolledBack = written.Count > 0 || bankAdded.Count > 0 || bankArranged;
            progress?.Report(new LivingDexProgress(LivingDexPhase.RollingBack, done, total, "Undoing every change…"));
            var rollbackError = await RollbackAsync(written, bankAdded, bankArranged ? staging.BankArranges : []).ConfigureAwait(false);
            staging.Committed = true;
            var outcomes = staging.Outcomes.Select(o => o.Status == LivingDexStepStatus.Staged
                ? o with { Status = LivingDexStepStatus.Failed, Message = cancelled ? "Cancelled." : "Undone." } : o).ToList();
            var summary = cancelled
                ? rolledBack ? "Cancelled: every save was put back exactly as it was." : "Cancelled before anything was written."
                : $"Stopped: {error.Message} " + (rolledBack ? "Every save was put back exactly as it was." : "Nothing was written.");
            if (rollbackError is not null) summary += $" Rollback problem: {rollbackError} Use Restore points to recover.";
            progress?.Report(new LivingDexProgress(LivingDexPhase.Finished, total, total, summary));
            return new LivingDexRunResult(false, rolledBack, summary, outcomes, backups, plan.CoveredBefore, plan.CoveredBefore, error.Message);
        }

        staging.Committed = true;
        var final = staging.Outcomes.Select(o => o.Status == LivingDexStepStatus.Staged ? o with { Status = LivingDexStepStatus.Done } : o).ToList();
        var filled = final.Count(o => o.Status == LivingDexStepStatus.Done && o.Step.Fills);
        var after = plan.CoveredBefore + filled;
        var failed = final.Count(o => o.Status == LivingDexStepStatus.Failed);
        var line = $"{plan.DestinationLabel}: {plan.CoveredBefore} → {after} of {plan.TargetCount}."
            + (failed > 0 ? $" {failed} step{(failed == 1 ? "" : "s")} skipped." : "")
            + (plan.Guides > 0 ? $" {plan.TargetCount - after} left to catch." : "");
        progress?.Report(new LivingDexProgress(LivingDexPhase.Finished, total, total, line));
        return new LivingDexRunResult(true, false, line, final, backups, plan.CoveredBefore, after);
    }

    private void ArrangeBank(LivingDexStaging staging)
    {
        if (staging.BankArranges.Count == 0) return;
        bank!.Place([.. staging.BankArranges.Select(a => (a.Id, a.To.Box, a.To.Slot))]);
    }

    /// <summary>Puts every written save back to its original bytes (newest first) and undoes the Bank.
    /// Returns the first problem, or null when everything was restored.</summary>
    private async Task<string?> RollbackAsync(List<(LivingDexStaging.SaveState State, byte[] Candidate)> written,
        List<Guid> bankAdded, IReadOnlyList<(Guid Id, SlotRef From, SlotRef To)> arranged)
    {
        string? problem = null;
        for (var i = written.Count - 1; i >= 0; i--)
        {
            var (state, candidate) = written[i];
            try
            {
                var now = new SaveSnapshot(state.Snapshot.Format, state.Snapshot.Generation, candidate, [], state.Snapshot.DisplayName);
                await writer.WriteScopedAsync(state.Save.DocumentId, now, state.Original, WriteScope.Everything,
                    $"{Description} rollback", CancellationToken.None).ConfigureAwait(false);
                sessions?.MarkWritten(state.Save.DocumentId, state.Original);
            }
            catch (Exception error)
            {
                problem ??= $"{state.Save.GameLabel}: {error.Message}";
            }
        }
        try
        {
            if (bankAdded.Count > 0) bank?.RemoveMany(bankAdded);
            if (arranged.Count > 0) bank?.Place([.. arranged.Select(a => (a.Id, a.From.Box, a.From.Slot))]);
        }
        catch (Exception error)
        {
            problem ??= $"Bank: {error.Message}";
        }
        return problem;
    }

    private static string ChangeLine(LivingDexStaging.SaveState state) =>
        state.Incoming > 0 && state.Outgoing > 0 ? $"{Description}: {state.Incoming} in, {state.Outgoing} out"
        : state.Incoming > 0 ? $"{Description}: {state.Incoming} Pokémon arrived"
        : $"{Description}: {state.Outgoing} Pokémon moved out";

    private static LivingDexStepOutcome Fail(LivingDexStep step, string message) =>
        new(step, LivingDexStepStatus.Failed, message, [], null);

    private static string StepLine(LivingDexStep step, Dictionary<string, string> labels) =>
        $"#{step.Species:000} from {labels.GetValueOrDefault(step.SourceId ?? "") ?? "?"}";
}
