using System.Text;
using PKForge.Domain;
using PKHeX.Core;
using PKHeX.Core.AutoMod;

namespace PKForge.Engine;

/// <summary>
/// Adapts the pinned Auto Legality Mod. Everything runs fully offline, in-process.
/// A generated/repaired mon is placed into the session's slot; callers then serialize
/// and write through the usual safe path (validate → backup → atomic write).
/// </summary>
public sealed class LegalizerService : ILegalizerService
{
    private static readonly object TrainerGenerationLock = new();
    private readonly GameStrings _strings = GameInfo.GetStrings("en");
    private readonly IGenerationOwnershipSettings? _ownershipSettings;

    public LegalizerService(IGenerationOwnershipSettings? ownershipSettings = null) =>
        _ownershipSettings = ownershipSettings;

    static LegalizerService()
    {
        // Our AutoMod is source-built against the exact same Core revision, so the
        // NuGet-version mismatch gate does not apply.
        APILegality.EnableDevMode = true;
    }

    public GenerationOutcome Generate(ISaveEngineSession session, int box, int slot, GenerationRequest request)
    {
        if (session is Unbound.UnboundEngineSession unbound)
            return unbound.GenerateInto(box, slot, request);
        if (session is RadicalRed.CfruEngineSession radicalRed)
            return radicalRed.GenerateInto(box, slot, request);
        if (session is not SaveEngineSession engine)
            return new GenerationOutcome(false, "Pokémon cannot be generated into this save.");
        var text = BuildShowdownText(request, engine.SaveFile.Context);
        return GenerateFromShowdown(session, box, slot, text, request.AllowUnsupportedSpecies);
    }

    public GenerationOutcome GenerateFromShowdown(ISaveEngineSession session, int box, int slot, string showdownText,
        bool allowUnsupportedSpecies = false)
    {
        if (session is Unbound.UnboundEngineSession unbound)
            return unbound.GenerateFromShowdownText(box, slot, showdownText);
        if (session is RadicalRed.CfruEngineSession radicalRed)
            return radicalRed.GenerateFromShowdownText(box, slot, showdownText);
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;

        var set = new ShowdownSet(showdownText);
        if (set.Species == 0)
            return new GenerationOutcome(false, "Could not read the set (no species).");
        // No ROM hack can extend a save format's species table; reject with the real
        // reason instead of a misleading legalizer failure.
        if (set.Species > save.MaxSpeciesID)
        {
            if (!allowUnsupportedSpecies)
                return new GenerationOutcome(false,
                    $"{GameName(save)} cannot store this Pokémon; its species does not exist in this generation.");

            var forced = BuildUnsupportedMon(save, set);
            var forcedPlaced = PlaceGenerated(save, forced, box, slot);
            return forcedPlaced is not null
                ? new GenerationOutcome(false, forcedPlaced)
                : new GenerationOutcome(true,
                    "Generated (HaX): this species is unsupported in this game; no guarantee it works.");
        }

        var result = GenerateLegal(engineSession, set, out var evsLowered);
        if (result.Status is not LegalizationResult.Regenerated)
            return new GenerationOutcome(false,
                result.Status switch
                {
                    LegalizationResult.Timeout => "The legalizer timed out for this request.",
                    LegalizationResult.VersionMismatch => "Engine version mismatch.",
                    _ => "No legal combination found for this request in this game.",
                });

        var created = ConvertForSave(save, result.Created);
        var analysis = new LegalityAnalysis(created);
        var placed = PlaceGenerated(save, created, box, slot);
        if (placed is not null)
            return new GenerationOutcome(false, placed);
        return new GenerationOutcome(true, (analysis.Valid
            ? save.IsFromTrainer(created) ? "Generated - legal." : "Generated - legal (event OT)."
            : "Generated (legality imperfect).") + (evsLowered ? EvsLoweredNote : ""));
    }

    /// <summary>Places a generated mon: boxes overwrite the slot; the party appends
    /// (capped at six, compact like the games). Null on success, else the failure.</summary>
    private static string? PlaceGenerated(SaveFile save, PKM created, int box, int slot)
    {
        if (box == -1)
        {
            if (save.PartyCount >= 6)
                return "The party is full.";
            // Surgical append: the PartyData setter would also bump records and rewrite
            // handler data for every party member. Only the newcomer's Pokédex entry is set.
            save.SetPartySlotAtIndex(created, Math.Min(save.PartyCount, 5), NewcomerImport.For(save, created));
        }
        else
        {
            save.SetBoxSlotAtIndex(created, box, slot, NewcomerImport.For(save, created));
        }
        return null;
    }

    /// <summary>HaX generation for species beyond the save's table: a plain mon of the
    /// save's own format carrying the set details, no encounter, no legality. The games
    /// have no data for the species, so behavior is explicitly not guaranteed.</summary>
    private static PKM BuildUnsupportedMon(SaveFile save, ShowdownSet set)
    {
        var template = EntityBlank.GetBlank(save);
        if (template.Version == 0)
            template.Version = save.Version;
        // ApplySetDetails clamps the species to the format maximum; it still fills
        // level, moves, IVs, EVs, nature (PID-aware on Gen 3/4), shiny and EC.
        template.ApplySetDetails(set);
        template.Species = (ushort)set.Species; // the actual override
        if (template.Format >= 3)
            template.Ball = (byte)Ball.Poke;
        template.OriginalTrainerName = save.OT;
        template.TID16 = save.TID16;
        if (template is not GBPKM)
            template.SID16 = save.SID16;
        template.OriginalTrainerGender = (byte)Math.Clamp((int)save.Gender, 0, 1);
        template.RefreshChecksum();
        return template;
    }

    public GenerationOutcome LegalizeSlot(ISaveEngineSession session, int box, int slot)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;

        // Box -1 is the party: it has its own accessors, and the box path would go out
        // of range and abort the whole mutation behind the loading overlay.
        var current = box == -1 ? save.GetPartySlotAtIndex(slot) : save.GetBoxSlotAtIndex(box, slot);
        if (current.Species == 0)
            return new GenerationOutcome(false, "Empty slot.");
        if (new LegalityAnalysis(current).Valid)
            return new GenerationOutcome(true, "Already legal.");

        var repaired = LegalizeKeepingOrigin(save, current);
        if (repaired.Species != current.Species || !new LegalityAnalysis(repaired).Valid)
            return new GenerationOutcome(false, "Could not find a legal repair for this mon.");

        if (box == -1)
            save.SetPartySlotAtIndex(repaired, slot, EntityImportSettings.None);
        else
            save.SetBoxSlotAtIndex(repaired, box, slot, EntityImportSettings.None);
        return new GenerationOutcome(true, OriginNote(current, repaired) is { } note ? $"Legalized. {note}" : "Legalized.");
    }

    /// <summary>
    /// Auto-Legality, told to keep where the Pokémon came from. It was handed no analysis of
    /// the current Pokémon, so its own "try the original encounter first" step never ran, and
    /// it tries eggs before every other encounter: eggs allow any PID, so a caught shiny came
    /// back hatched. A Pokémon that was not an egg now tries its original catch encounter,
    /// then wild, static, trade and event ones, and eggs last; its ball stays when still legal.
    /// </summary>
    /// <summary>
    /// Encounter order for making or repairing a Pokémon: a catch (wild or static) first,
    /// then an egg, and only then events and in-game trades, which carry another trainer.
    /// Auto-Legality's default puts eggs first, and an egg fits almost any request.
    /// </summary>
    private static readonly EncounterTypeGroup[] CatchesFirst =
        [EncounterTypeGroup.Slot, EncounterTypeGroup.Static, EncounterTypeGroup.Egg, EncounterTypeGroup.Mystery, EncounterTypeGroup.Trade];

    internal static PKM LegalizeKeepingOrigin(SaveFile save, PKM current, Shiny shinyKind = Shiny.Always)
    {
        var analysis = new LegalityAnalysis(current);
        if (current.IsEgg || current.WasEgg)
        {
            lock (TrainerGenerationLock)
            using (new AutoModTrainerScope(save))
                return save.Legalize(current.Clone(), analysis);
        }
        if (RegenerateAtOrigin(save, current, analysis, shinyKind) is { } atOrigin)
            return atOrigin;
        // The analysis's best match for a broken caught Pokémon is often an egg (eggs fit any
        // PID): handing it over would put the egg first. Only a real catch leads the search.
        var lead = analysis.EncounterOriginal is IEncounterEgg ? null : analysis;

        PKM repaired;
        lock (TrainerGenerationLock)
        using (new AutoModTrainerScope(save))
        {
            var previous = EncounterMovesetGenerator.PriorityList;
            try
            {
                EncounterMovesetGenerator.PriorityList =
                    CatchesFirst;
                repaired = save.Legalize(current.Clone(), lead); // Legalize rewrites the mon it is given
            }
            finally { EncounterMovesetGenerator.PriorityList = previous; }
        }

        if (repaired.Ball != current.Ball)
        {
            var withBall = repaired.Clone();
            withBall.Ball = current.Ball;
            withBall.RefreshChecksum();
            if (new LegalityAnalysis(withBall).Valid) repaired = withBall;
        }
        return repaired;
    }

    /// <summary>
    /// Rebuilds the Pokémon from an encounter at its own met location and game, keeping what
    /// the player asked for (shiny, gender, nature) and, where still legal, its ball, level,
    /// moves and nickname. Auto-Legality walks every encounter of the species first and ran
    /// out of time before reaching the right place; this goes straight there. Null when no
    /// encounter there yields a legal Pokémon.
    /// </summary>
    private static PKM? RegenerateAtOrigin(SaveFile save, PKM current, LegalityAnalysis analysis, Shiny shinyKind)
    {
        if (current.MetLocation == 0) return null;
        // Only the moves it can really know: an unlearnable one would rule out every encounter.
        var moves = new[] { current.Move1, current.Move2, current.Move3, current.Move4 }
            .Where((m, i) => m != 0 && analysis.Info.Moves[i].Valid).ToArray();
        return RegenerateAtOrigin(save, current, moves, shinyKind)
            ?? (moves.Length > 0 ? RegenerateAtOrigin(save, current, [], shinyKind) : null);
    }

    private static PKM? RegenerateAtOrigin(SaveFile save, PKM current, ushort[] moves, Shiny shinyKind)
    {
        var template = current.Clone();
        var here = EncounterMovesetGenerator.GenerateEncounters(template, save, moves, current.Version)
            .Where(e => e is not IEncounterEgg && e is ILocation l && l.Location == current.MetLocation)
            .Take(32);
        var criteria = new EncounterCriteria
        {
            Shiny = current.IsShiny ? shinyKind : Shiny.Never,
            Gender = current.Gender is 0 or 1 ? (Gender)current.Gender : Gender.Random,
            Nature = current.Nature,
        };
        foreach (var encounter in here)
        {
            PKM made;
            try { made = encounter.ConvertToPKM(save, criteria); }
            catch (ArgumentException) { continue; }
            // Sword/Shield overworld catches derive the PID from a seed and the generator
            // ignores the shiny request: search for a seed that gives the asked shininess.
            if (!IsShinyAsAsked(made, current.IsShiny, shinyKind) && made is PK8 pk8
                && encounter is EncounterSlot8 slot8 && slot8.GetRequirement(pk8) == OverworldCorrelation8Requirement.MustHave)
                Overworld8RNG.ApplyDetails(pk8, criteria, current.IsShiny ? shinyKind : Shiny.Never);

            // Some generators (Gen 5 wild slots) ignore the shiny request; set it afterwards.
            // Each try rolls a new PID, and only some meet the origin's PID rules.
            for (var tries = 0; tries < 64 && !IsShinyAsAsked(made, current.IsShiny, shinyKind); tries++)
                made = TryKeep(made, pk => { if (current.IsShiny) pk.SetShiny(shinyKind); else pk.SetUnshiny(); });
            if (!IsShinyAsAsked(made, current.IsShiny, shinyKind) || !new LegalityAnalysis(made).Valid) continue;

            // Put back what the player chose wherever the result stays legal.
            made = TryKeep(made, pk => pk.Ball = current.Ball);
            if (current.CurrentLevel > made.CurrentLevel) made = TryKeep(made, pk => { pk.CurrentLevel = current.CurrentLevel; pk.ResetPartyStats(); });
            if (moves.Length > 0) made = TryKeep(made, pk => { pk.SetMoves(moves); pk.HealPP(); });
            if (current.IsNicknamed) made = TryKeep(made, pk => pk.SetNickname(current.Nickname));
            return made;
        }
        return null;
    }

    private static bool IsShinyAsAsked(PKM pk, bool shiny, Shiny kind) => !shiny ? !pk.IsShiny : kind switch
    {
        Shiny.AlwaysSquare => pk.ShinyXor == 0,
        Shiny.AlwaysStar => pk.IsShiny && pk.ShinyXor != 0,
        _ => pk.IsShiny,
    };

    /// <summary>
    /// <paramref name="change"/> applied to a copy, returned only if it stays legal; otherwise
    /// the Pokémon as it was. The copy itself is kept, so a random change (a new PID) is the
    /// one that was checked.
    /// </summary>
    private static PKM TryKeep(PKM pk, Action<PKM> change)
    {
        var trial = pk.Clone();
        change(trial);
        trial.RefreshChecksum();
        return new LegalityAnalysis(trial).Valid ? trial : pk;
    }

    /// <summary>Says so when the only legal repair had to change the origin, so it is never a surprise.</summary>
    private static string? OriginNote(PKM before, PKM after)
    {
        if (!before.IsEgg && !before.WasEgg && (after.WasEgg || after.IsEgg))
            return "No legal version keeps its catch origin, so it is now hatched from an egg.";
        if (before.MetLocation != after.MetLocation && !after.WasEgg)
            return "Its met location changed to one where it can legally appear.";
        return null;
    }

    public GenerationOutcome LegalizeSlots(ISaveEngineSession session, IReadOnlyList<(int Box, int Slot)> slots,
        Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;

        var repaired = 0;
        var stuck = 0;
        for (var i = 0; i < slots.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (box, slot) = slots[i];
            // Party (-1) and boxes have different accessors; the box path would go out
            // of range and abort the whole batch behind the loading overlay.
            var current = box == -1 ? save.GetPartySlotAtIndex(slot) : save.GetBoxSlotAtIndex(box, slot);
            if (current.Species != 0 && !new LegalityAnalysis(current).Valid)
            {
                var candidate = LegalizeKeepingOrigin(save, current);
                if (candidate.Species == current.Species && new LegalityAnalysis(candidate).Valid)
                {
                    if (box == -1)
                        save.SetPartySlotAtIndex(candidate, slot, EntityImportSettings.None);
                    else
                        save.SetBoxSlotAtIndex(candidate, box, slot, EntityImportSettings.None);
                    repaired++;
                }
                else
                {
                    stuck++;
                }
            }
            onProgress?.Invoke(i + 1, slots.Count);
        }

        return repaired switch
        {
            > 0 => new GenerationOutcome(true, $"Legalized {repaired} Pokémon." +
                (stuck > 0 ? $" {stuck} had no legal repair and were left untouched." : string.Empty)),
            0 when stuck > 0 => new GenerationOutcome(false,
                $"None of the {stuck} flagged Pokémon had a legal repair; nothing was written."),
            _ => new GenerationOutcome(false, "Nothing to legalize."),
        };
    }

    public GeneratedEntity? GenerateData(ISaveEngineSession session, GenerationRequest request) =>
        session is SaveEngineSession engine
            ? GenerateDataFromShowdown(session, BuildShowdownText(request, engine.SaveFile.Context), request.AllowUnsupportedSpecies)
            : null;

    public GeneratedEntity? GenerateDataFromShowdown(ISaveEngineSession session, string showdownText,
        bool allowUnsupportedSpecies = false)
    {
        if (session is not SaveEngineSession engineSession) return null;
        var save = engineSession.SaveFile;

        var set = new ShowdownSet(showdownText);
        if (set.Species == 0) return null;
        if (set.Species > save.MaxSpeciesID)
        {
            if (!allowUnsupportedSpecies) return null;
            return BuildGeneratedEntity(BuildUnsupportedMon(save, set));
        }
        var result = GenerateLegal(engineSession, set, out _);
        if (result.Status is not LegalizationResult.Regenerated) return null;

        var created = ConvertForSave(save, result.Created);
        return BuildGeneratedEntity(created);
    }

    private GeneratedEntity BuildGeneratedEntity(PKM created)
    {
        var data = new byte[created.SIZE_PARTY];
        created.WriteDecryptedDataParty(data);
        var info = new BankEntryInfo(
            created.Species, created.Form, created.IsShiny,
            created.IsNicknamed ? created.Nickname : _strings.specieslist[created.Species],
            created.CurrentLevel, created.Format, "Generated", EntityBytes.FormatOf(created), created.HeldItem, EntitySprite.Traits(created));
        return new GeneratedEntity(data, info);
    }

    public GenerationOutcome FillSpecies(ISaveEngineSession session, IReadOnlyList<int> species, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;

        var placed = 0;
        foreach (var id in species)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = FindEmptySlot(save);
            if (slot is null)
                return placed > 0
                    ? new GenerationOutcome(true, $"Generated {placed}; storage is now full.")
                    : new GenerationOutcome(false, "No empty PC slots.");

            var name = _strings.specieslist[Math.Clamp(id, 1, _strings.specieslist.Length - 1)];
            var outcome = GenerateFromShowdown(session, slot.Value.Box, slot.Value.Slot, name);
            if (outcome.Success) placed++;
            onProgress?.Invoke(placed, species.Count);
        }
        return placed > 0
            ? new GenerationOutcome(true, $"Generated {placed} legal Pokémon into empty slots.")
            : new GenerationOutcome(false, "The legalizer could not generate any of those species in this game.");
    }

    /// <summary>Mass egg factory in the spirit of CDNRae's PKHeX bulk egg generator:
    /// a legal template per species, converted to an authentic egg state for the
    /// target generation, then placed into the first empty PC slots.</summary>
    public GenerationOutcome GenerateEggs(ISaveEngineSession session, IReadOnlyList<int> species, EggOptions options, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;
        if (save.Generation is < 3)
            return new GenerationOutcome(false, "Eggs are only supported from Gen 3 onward here.");

        var placed = 0;
        var lines = new HashSet<(ushort, byte)>();
        foreach (var id in species)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // An evolved species hatches as its line's first stage: one egg per line.
            var (baseSpecies, baseForm) = EvolutionTree.GetEvolutionTree(save.Context).GetBaseSpeciesForm((ushort)Math.Clamp(id, 1, ushort.MaxValue), 0);
            if (!lines.Add((baseSpecies, baseForm)))
                continue;
            var slot = FindEmptySlot(save);
            if (slot is null)
                return placed > 0
                    ? new GenerationOutcome(true, $"Generated {placed} eggs; storage is now full.")
                    : new GenerationOutcome(false, "No empty PC slots.");

            // A real egg, as the Day Care hands it over (the game's egg encounter, unhatched,
            // with a full hatch counter), never a legalized level-100 Pokémon marked as an egg.
            var criteria = EncounterCriteria.Unrestricted with
            {
                Shiny = options.Shiny ? Shiny.Always : Shiny.Random,
            };
            if (options.MaxIv)
                criteria = criteria with { IV_HP = 31, IV_ATK = 31, IV_DEF = 31, IV_SPA = 31, IV_SPD = 31, IV_SPE = 31 };
            if (EggService.CreateEgg(save, baseSpecies, baseForm, criteria, out var kept) is not { } egg)
                continue;
            if (!kept)
            {
                // The criteria could not be met by a generated egg: set them, as the factory always did.
                if (options.MaxIv) egg.SetIVs(0x7FFF_FFFF);
                if (options.Shiny && !egg.IsShiny) egg.SetShiny();
            }
            egg.RefreshChecksum();
            save.SetBoxSlotAtIndex(egg, slot.Value.Box, slot.Value.Slot, EntityImportSettings.None);
            placed++;
            onProgress?.Invoke(placed, species.Count);
        }
        return placed > 0
            ? new GenerationOutcome(true, $"Generated {placed} eggs into empty slots.")
            : new GenerationOutcome(false, "The legalizer could not generate any of those species in this game.");
    }

    private static (int Box, int Slot)? FindEmptySlot(SaveFile save)
    {
        for (var box = 0; box < save.BoxCount; box++)
        for (var slot = 0; slot < save.BoxSlotCount; slot++)
            if (save.GetBoxSlotAtIndex(box, slot).Species == 0)
                return (box, slot);
        return null;
    }

    /// <summary>
    /// <see cref="GenerateLegal"/>, then, for a Gen 3/4 set Auto-Legality could not make legal,
    /// one more try with each EV capped at 100. PKHeX checks Gen 3/4 EVs against the experience
    /// gained since the catch (EffortExpLegality): a fresh low-level Pokémon can only hold what
    /// vitamins give, 100 per stat. <paramref name="evsLowered"/> tells the caller to say so.
    /// </summary>
    private APILegality.AsyncLegalizationResult GenerateLegal(SaveEngineSession session, ShowdownSet set, out bool evsLowered)
    {
        evsLowered = false;
        var result = GenerateLegal(session, set);
        if (result.Status is LegalizationResult.Regenerated || session.SaveFile.Generation is not (3 or 4)
            || !set.EVs.Any(ev => ev > EffortValues.MaxVitamins34))
            return result;

        var requested = set.EVs.ToArray();
        for (var i = 0; i < set.EVs.Length; i++)
            set.EVs[i] = Math.Min(set.EVs[i], EffortValues.MaxVitamins34);
        var capped = GenerateLegal(session, set);
        if (capped.Status is LegalizationResult.Regenerated)
        {
            evsLowered = true;
            return capped;
        }
        requested.CopyTo(set.EVs, 0);
        return result;
    }

    private const string EvsLoweredNote = " EVs lowered to 100 per stat: a Pokémon this new can only have vitamin EVs in Gen 3/4.";

    private APILegality.AsyncLegalizationResult GenerateLegal(SaveEngineSession session, ShowdownSet set)
    {
        lock (TrainerGenerationLock)
        {
            var save = session.SaveFile;
            // Auto-Legality has no encounter/evolution tables for Luminescent's
            // distinct context yet. Its save layout and trainer identity are BDSP,
            // so generate through an isolated retail-BDSP view and place the result
            // back into the real Luminescent session. This keeps the generator usable
            // without teaching AutoMod that mod-specific encounters are official.
            var generationSave = save is SAV8BSLuminescent
                ? new SAV8BS(save.Data.ToArray())
                : save;
            var previousPriority = APILegality.GameVersionPriority;
            var previousOrder = APILegality.PriorityOrder;
            var previousGroups = EncounterMovesetGenerator.PriorityList;
            var useOwner = _ownershipSettings?.UseCurrentTrainerForGeneration ?? true;
            // "Generated Pokémon obey trainer: OFF" asks for Auto-Legality's own trainers.
            using var trainer = useOwner ? new AutoModTrainerScope(generationSave) : default;
            try
            {
                // Auto-Legality tries eggs first, and an egg fits any request (any PID, any
                // shiny), so every generated Pokémon came out hatched. A created Pokémon is
                // looked for as a catch, a gift or a trade first; eggs only when nothing else fits.
                EncounterMovesetGenerator.PriorityList =
                    CatchesFirst;

                if (save is SAV8BSLuminescent)
                    return generationSave.GetLegalFromSet(set);

                if (!useOwner)
                    return save.GetLegalFromSet(set);

                APILegality.GameVersionPriority = GameVersionPriorityType.PriorityOrder;

                var eligible = GameUtil.GameVersions
                    .Where(z => generationSave.Generation < 3 || z.Generation >= 3)
                    .ToList();

                // Try the open game first: common species get a native encounter and an
                // exact trainer/version match. Some species are transfer-only, so keep a
                // legal fallback that still carries the full modern trainer identity.
                APILegality.PriorityOrder = [generationSave.Version, .. eligible.Where(z => z != generationSave.Version)];
                var native = generationSave.GetLegalFromSet(set);
                var nativeOwned = TryOwn(session, native, out var ownedNative);
                if (nativeOwned && save.IsFromTrainer(ownedNative.Created))
                    return ownedNative;

                // A Gen 1/2 origin cannot carry a modern SID, and oldest-first search
                // steers transfer species toward ordinary catchable encounters instead
                // of fixed-OT distributions.
                APILegality.PriorityOrder = [.. eligible.OrderBy(z => z)];
                var transfer = generationSave.GetLegalFromSet(set);
                if (TryOwn(session, transfer, out var ownedTransfer))
                    return ownedTransfer;

                // Event-only species (Marshadow, Zeraora, Diancie...) have no
                // player-OT origin in any version: their only legal form is the
                // distribution itself. Keep the authentic event OT rather than
                // failing a request the legalizer actually satisfied.
                if (nativeOwned)
                    return ownedNative;
                if (native.Status is LegalizationResult.Regenerated)
                    return native;
                if (transfer.Status is LegalizationResult.Regenerated)
                    return transfer;
                return native with { Status = LegalizationResult.Failed };
            }
            finally
            {
                APILegality.GameVersionPriority = previousPriority;
                APILegality.PriorityOrder = previousOrder;
                EncounterMovesetGenerator.PriorityList = previousGroups;
            }
        }
    }

    private static bool TryOwn(SaveEngineSession session, APILegality.AsyncLegalizationResult result,
        out APILegality.AsyncLegalizationResult owned)
    {
        // The stamp mutates in place; work on a clone so a rejected stamp (fixed-OT
        // event mon, or a rewrite that turns out illegal) leaves the pristine
        // legal result usable for the event-OT fallback above.
        owned = result with { Created = result.Created.Clone() };
        return owned.Status is LegalizationResult.Regenerated && session.MakeOwned(owned.Created, null, out _);
    }

    private static string GameName(SaveFile save)
    {
        var index = (int)save.Version;
        var names = GameInfo.GetStrings("en").gamelist;
        return index > 0 && index < names.Length && names[index].Length > 0 ? names[index] : save.Version.ToString();
    }

    private static PKM ConvertForSave(SaveFile save, PKM created)
    {
        if (save is not SAV8BSLuminescent || created is PB8LUMI)
            return created;

        var data = new byte[created.SIZE_PARTY];
        created.WriteDecryptedDataParty(data);
        return new PB8LUMI(data);
    }


    public GenerationOutcome FillLivingDex(ISaveEngineSession session, byte[] compressedBundle, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        if (compressedBundle is not { Length: > 0 })
            return new GenerationOutcome(false, "No living dex bundle for this game - nothing written.");

        var save = engineSession.SaveFile;
        var capacity = save.BoxCount * save.BoxSlotCount;
        int placed;
        try
        {
            placed = engineSession.PlaceLivingDex(compressedBundle);
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidDataException or EndOfStreamException)
        {
            return new GenerationOutcome(false, error.Message);
        }
        onProgress?.Invoke(placed, capacity);
        return placed == 0
            ? new GenerationOutcome(false, "The bundle held no compatible Pokémon; nothing was written.")
            : new GenerationOutcome(true, $"Living dex: {placed} Pokémon placed.");
    }

    /// <summary>Builds standard Showdown-format text from the wizard's structured request.</summary>
    private string BuildShowdownText(GenerationRequest request, EntityContext context)
    {
        var text = new StringBuilder();
        var speciesName = _strings.specieslist[request.Species];
        // Showdown names spell the Nidoran pair with a suffix; the gender sign alone
        // misparses as the wrong sibling.
        if (request.Species is (int)PKHeX.Core.Species.NidoranM) speciesName = "Nidoran-M";
        else if (request.Species is (int)PKHeX.Core.Species.NidoranF) speciesName = "Nidoran-F";
        if (request.Form > 0)
        {
            // "Rotom" + "-" + "Wash" => "Rotom-Wash"; the showdown parser matches form
            // names ignoring case and dash/space differences, so the display name works.
            var forms = FormConverter.GetFormList((ushort)request.Species, _strings.Types, _strings.forms, context);
            if (request.Form < forms.Length && forms[request.Form].Length > 0)
                speciesName = $"{speciesName}-{ShowdownParsing.GetShowdownFormName((ushort)request.Species, forms[request.Form])}";
        }
        text.AppendLine(speciesName);
        if (request.Level is { } level)
            text.AppendLine($"Level: {Math.Clamp(level, 1, 100)}");
        if (request.Shiny)
            text.AppendLine("Shiny: Yes");
        if (request.Nature is { } nature && nature < _strings.natures.Length)
            text.AppendLine($"{_strings.natures[nature]} Nature");
        if (request.Ability is { } ability && ability < _strings.abilitylist.Length)
            text.AppendLine($"Ability: {_strings.abilitylist[ability]}");
        if (request.Ball is { } ball && ball < _strings.balllist.Length)
            text.AppendLine($"Ball: {_strings.balllist[ball]}");
        foreach (var move in request.Moves ?? [])
        {
            if (move > 0 && move < _strings.movelist.Length)
                text.AppendLine($"- {_strings.movelist[move]}");
        }
        return text.ToString();
    }
}
