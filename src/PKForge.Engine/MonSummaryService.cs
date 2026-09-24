using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Decodes one slot into the read-only <see cref="MonSummary"/> the summary screens draw.
/// It only reads through the session's public surface, so a bank entry (opened in its own
/// throwaway entity session), a live save slot and a romhack session all go through the
/// same path. Every optional section is guarded: a format that refuses a query (romhack
/// met data, Gen 1 abilities) simply leaves that part of the summary empty.
/// </summary>
public sealed class MonSummaryService(IGameDataService data, IMonInfoService info, ILegalityService legality) : IMonSummaryService
{
    public MonSummaryService() : this(new GameDataService(), new MonInfoService(), new LegalityService()) { }

    public MonSummary? Build(ISaveEngineSession session, int box, int slot, bool analyzeLegality = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        var d = session.ReadEntity(box, slot);
        if (d.IsEmpty || d.Species <= 0) return null;
        var generation = session.Generation;

        var entity = session is SaveEngineSession engine ? engine.GetEntity(box, slot) : null;
        var format = entity?.GetType().Name ?? session.Snapshot.Format;

        var forms = Try(() => session.GetFormChoices(d.Species)) ?? [];
        var formName = d.Form > 0 && d.Form < forms.Count ? forms[d.Form] : "";

        var itemNames = Try(session.GetItemNames) ?? data.ItemNames;
        var itemName = d.HeldItem > 0 ? Name(itemNames, d.HeldItem) : null;

        var card = Try(() => info.GetSpeciesCard(session, d.Species, d.Form));
        var baseStats = card?.BaseStats ?? Try(() => session.GetBaseStats(d.Species));
        IReadOnlyList<int> bases = baseStats is { } b ? [b.Hp, b.Atk, b.Def, b.SpA, b.SpD, b.Spe] : [];
        var types = d.Types is { Count: > 0 } t ? t : card?.Types ?? [];

        var hasNature = generation >= 3 && NatureFacts.IsValid(d.Nature);
        var hasAbility = generation >= 3 && d.Ability > 0;

        var met = Try(() => session.GetMetInfo(box, slot));
        var rng = Try(() => session.GetRngInfo(box, slot));
        var personality = rng is null ? (uint?)null : generation >= 6 && rng.EncryptionConstant is { } ec ? ec : rng.Pid;
        var potential = Try(() => session.GetPotential(box, slot));
        var ribbons = Try(() => session.GetRibbons(box, slot)) ?? [];
        var owned = ribbons.Where(r => r.Value > 0).ToList();
        var pokerus = Try(() => session.GetPokerus(box, slot));
        var cosmetics = Try(() => session.GetCosmetics(box, slot));
        var isEgg = met?.IsEgg ?? session.Snapshot.Slots.Any(s => s.Box == box && s.Slot == slot && s.IsEgg);

        var legal = (bool?)null;
        IReadOnlyList<string> lines = [];
        if (analyzeLegality && session.SupportsLegalityAnalysis)
        {
            var report = Try(() => legality.Analyze(session, box, slot));
            if (report is not null) (legal, lines) = (report.Valid, report.Lines);
        }

        return new MonSummary(
            d.Species, d.Form, d.SpeciesName is { Length: > 0 } sn ? sn : Name(data.SpeciesNames, d.Species), formName,
            d.Nickname, isEgg, d.IsShiny, d.Gender, d.Level, types, generation, format,
            d.OriginalTrainer, met?.TID, generation >= 3 ? met?.SID : null,
            hasNature ? d.Nature : null, hasNature ? Name(data.NatureNames, d.Nature) : null,
            hasAbility ? d.Ability : null, hasAbility ? Name(data.AbilityNames, d.Ability) : null,
            hasAbility ? DexFacts.Ability(d.Ability) : null,
            d.HeldItem, itemName, itemName is null ? null : DexFacts.Item(itemName),
            d.Ball, generation >= 3 || d.Ball > 0 ? Name(data.BallNames, d.Ball) : "Poké Ball",
            d.Friendship, pokerus is { Supported: true } ? pokerus : null,
            cosmetics?.Markings ?? [],
            d.Stats is { Count: 6 } stats ? stats : [],
            bases, d.IVs, d.EVs,
            Try(session.GetTrainingCaps) ?? new TrainingCaps(31, 252),
            Try(() => info.GetHiddenPowerType(session, d.IVs)),
            personality is { } p ? Characteristics.Describe(generation, d.IVs, p) : null,
            potential is { SupportsTera: true } ? potential.TeraTypeName : null,
            potential is { SupportsTera: true } ? potential.TeraType : null,
            Moves(session, box, slot, d),
            Relearn(session, box, slot),
            met,
            owned.Count(r => !r.IsMark), owned.Count(r => r.IsMark), owned.Select(r => r.Name).ToList(),
            legal, lines,
            Try(() => MonFieldService.Describe(session, box, slot)),
            SummaryTraits(session, box, slot, d, entity))
        {
            // The MOVES page marks each move legal / not legal with the engine's reason.
            MoveVerdicts = legal is null || entity is null ? null : Try(() => LegalityAssistService.GetMoveLegality(entity)),
        };
    }

    /// <summary>The sprite traits: straight from the entity when it parses, else the snapshot slot's
    /// (romhack sessions), with gender always taken from the fresh detail.</summary>
    private static SpriteTraits SummaryTraits(ISaveEngineSession session, int box, int slot, EntityDetail d, PKM? entity)
    {
        var traits = entity is not null
            ? EntitySprite.Traits(entity)
            : session.Snapshot.Slots.FirstOrDefault(s => s.Box == box && s.Slot == slot)?.Traits ?? default;
        return traits with { Female = d.Gender == 1 };
    }

    private IReadOnlyList<SummaryMove> Moves(ISaveEngineSession session, int box, int slot, EntityDetail d)
    {
        var details = Try(() => session.GetMoveDetails(box, slot));
        var ids = details?.Moves.Select(m => m.Move).ToArray() ?? [d.Move1, d.Move2, d.Move3, d.Move4];
        var moves = new List<SummaryMove>(4);
        for (var i = 0; i < ids.Length; i++)
        {
            var id = ids[i];
            if (id <= 0) continue;
            var move = Try(() => info.GetMove(session, id));
            var fact = DexFacts.Move(id);
            var pp = details is not null && i < details.Moves.Count ? details.Moves[i] : null;
            moves.Add(new SummaryMove(id, Name(data.MoveNames, id),
                move?.Type ?? fact?.Type ?? 0, move?.Category ?? fact?.Category ?? MoveCategory.Status,
                move?.Power ?? fact?.Power ?? 0, move?.Accuracy ?? fact?.Accuracy ?? 0,
                pp?.PP ?? move?.PP ?? 0, pp?.MaxPP ?? move?.PP ?? 0, pp?.PPUps ?? 0,
                move?.Effect ?? fact?.Effect ?? ""));
        }
        return moves;
    }

    private IReadOnlyList<string> Relearn(ISaveEngineSession session, int box, int slot)
    {
        var details = Try(() => session.GetMoveDetails(box, slot));
        if (details is not { SupportsRelearn: true }) return [];
        return details.RelearnMoves.Where(m => m > 0).Select(m => Name(data.MoveNames, m)).ToList();
    }

    private static string Name(IReadOnlyList<string> names, int id) =>
        (uint)id < (uint)names.Count && names[id].Length > 0 ? names[id] : $"#{id}";

    /// <summary>Optional sections: a format that cannot answer leaves the section empty.</summary>
    private static T? Try<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (error is NotSupportedException or NotImplementedException or InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            return default;
        }
    }
}
