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
        var text = BuildShowdownText(request, ((SaveEngineSession)session).SaveFile.Context);
        return GenerateFromShowdown(session, box, slot, text, request.AllowUnsupportedSpecies);
    }

    public GenerationOutcome GenerateFromShowdown(ISaveEngineSession session, int box, int slot, string showdownText,
        bool allowUnsupportedSpecies = false)
    {
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

        var result = GenerateLegal(engineSession, set);
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
        return new GenerationOutcome(true, analysis.Valid
            ? save.IsFromTrainer(created) ? "Generated - legal." : "Generated - legal (event OT)."
            : "Generated (legality imperfect).");
    }

    /// <summary>Places a generated mon: boxes overwrite the slot; the party appends
    /// (capped at six, compact like the games). Null on success, else the failure.</summary>
    private static string? PlaceGenerated(SaveFile save, PKM created, int box, int slot)
    {
        if (box == -1)
        {
            if (save.PartyCount >= 6)
                return "The party is full.";
            // Surgical append: the PartyData setter would also dex-mark, bump records,
            // and rewrite handler data for every party member.
            save.SetPartySlotAtIndex(created, Math.Min(save.PartyCount, 5), EntityImportSettings.None);
        }
        else
        {
            save.SetBoxSlotAtIndex(created, box, slot, EntityImportSettings.None);
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

        var current = save.GetBoxSlotAtIndex(box, slot);
        if (current.Species == 0)
            return new GenerationOutcome(false, "Empty slot.");
        if (new LegalityAnalysis(current).Valid)
            return new GenerationOutcome(true, "Already legal.");

        var repaired = save.Legalize(current);
        if (!new LegalityAnalysis(repaired).Valid)
            return new GenerationOutcome(false, "Could not find a legal repair for this mon.");

        save.SetBoxSlotAtIndex(repaired, box, slot, EntityImportSettings.None);
        return new GenerationOutcome(true, "Legalized.");
    }

    public GeneratedEntity? GenerateData(ISaveEngineSession session, GenerationRequest request) =>
        GenerateDataFromShowdown(session, BuildShowdownText(request, ((SaveEngineSession)session).SaveFile.Context),
            request.AllowUnsupportedSpecies);

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
        var result = GenerateLegal(engineSession, set);
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
            created.CurrentLevel, created.Format, "Generated");
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
        foreach (var id in species)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var slot = FindEmptySlot(save);
            if (slot is null)
                return placed > 0
                    ? new GenerationOutcome(true, $"Generated {placed} eggs; storage is now full.")
                    : new GenerationOutcome(false, "No empty PC slots.");

            var name = _strings.specieslist[Math.Clamp(id, 1, _strings.specieslist.Length - 1)];
            var result = GenerateLegal(engineSession, new ShowdownSet(name));
            if (result.Status is not LegalizationResult.Regenerated)
                continue;
            var egg = result.Created;
            if (options.MaxIv)
            {
                egg.SetIVs(0x7FFF_FFFF); // six 31s in the 30-bit packed representation
            }
            if (options.Shiny && !egg.IsShiny)
                egg.SetShiny();

            egg.Nickname = "Egg";
            egg.IsNicknamed = true;
            egg.OriginalTrainerFriendship = (byte)EggStateLegality.GetMinimumEggHatchCycles(egg);
            egg.MetLocation = 0;
            if (save.Generation == 4)
            {
                egg.IsNicknamed = false;
                egg.Version = save.Context.GetSingleGameVersion();
                egg.EggLocation = 2000; // Daycare
            }
            egg.IsEgg = true;
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
            var useOwner = _ownershipSettings?.UseCurrentTrainerForGeneration ?? true;
            try
            {
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
        var placed = engineSession.PlaceLivingDex(compressedBundle);
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
