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
    private readonly GameStrings _strings = GameInfo.GetStrings("en");

    static LegalizerService()
    {
        // Our AutoMod is source-built against the exact same Core revision, so the
        // NuGet-version mismatch gate does not apply.
        APILegality.EnableDevMode = true;
    }

    public GenerationOutcome Generate(ISaveEngineSession session, int box, int slot, GenerationRequest request)
    {
        var text = BuildShowdownText(request);
        return GenerateFromShowdown(session, box, slot, text);
    }

    public GenerationOutcome GenerateFromShowdown(ISaveEngineSession session, int box, int slot, string showdownText)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;

        var set = new ShowdownSet(showdownText);
        if (set.Species == 0)
            return new GenerationOutcome(false, "Could not read the set (no species).");

        var result = save.GetLegalFromSet(set);
        if (result.Status is not LegalizationResult.Regenerated)
            return new GenerationOutcome(false, result.Status switch
            {
                LegalizationResult.Timeout => "The legalizer timed out for this request.",
                LegalizationResult.VersionMismatch => "Engine version mismatch.",
                _ => "No legal combination found for this request in this game.",
            });

        var created = result.Created;
        var analysis = new LegalityAnalysis(created);
        save.SetBoxSlotAtIndex(created, box, slot);
        return new GenerationOutcome(true, analysis.Valid ? "Generated - legal." : "Generated (legality imperfect).");
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

        save.SetBoxSlotAtIndex(repaired, box, slot);
        return new GenerationOutcome(true, "Legalized.");
    }

    public GeneratedEntity? GenerateData(ISaveEngineSession session, GenerationRequest request) =>
        GenerateDataFromShowdown(session, BuildShowdownText(request));

    public GeneratedEntity? GenerateDataFromShowdown(ISaveEngineSession session, string showdownText)
    {
        if (session is not SaveEngineSession engineSession) return null;
        var save = engineSession.SaveFile;

        var set = new ShowdownSet(showdownText);
        if (set.Species == 0) return null;
        var result = save.GetLegalFromSet(set);
        if (result.Status is not LegalizationResult.Regenerated) return null;

        var created = result.Created;
        var data = new byte[created.SIZE_PARTY];
        created.WriteDecryptedDataParty(data);
        var info = new BankEntryInfo(
            created.Species, created.Form, created.IsShiny,
            created.IsNicknamed ? created.Nickname : _strings.specieslist[created.Species],
            created.CurrentLevel, created.Format, "Generated");
        return new GeneratedEntity(data, info);
    }

    public GenerationOutcome FillLivingDex(ISaveEngineSession session, Action<int, int>? onProgress = null, CancellationToken cancellationToken = default)
    {
        if (session is not SaveEngineSession engineSession)
            return new GenerationOutcome(false, "Unsupported session type.");
        var save = engineSession.SaveFile;

        var capacity = save.BoxCount * save.BoxSlotCount;
        var placed = 0;
        foreach (var pk in save.GenerateLivingDex(save.Personal))
        {
            if (cancellationToken.IsCancellationRequested)
                return new GenerationOutcome(placed > 0, $"Living dex stopped at {placed} Pokémon.");
            if (placed >= capacity) break;
            save.SetBoxSlotAtIndex(pk, placed / save.BoxSlotCount, placed % save.BoxSlotCount);
            placed++;
            onProgress?.Invoke(placed, capacity);
        }
        return placed == 0
            ? new GenerationOutcome(false, "The living dex generator produced nothing for this game.")
            : new GenerationOutcome(true, $"Living dex: {placed} Pokémon placed.");
    }

    /// <summary>Builds standard Showdown-format text from the wizard's structured request.</summary>
    private string BuildShowdownText(GenerationRequest request)
    {
        var text = new StringBuilder();
        text.AppendLine(_strings.specieslist[request.Species]);
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
