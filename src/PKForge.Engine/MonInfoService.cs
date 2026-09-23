using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// The read-only facts behind the info-rich editors. Engine sessions answer from PKHeX
/// itself (per-context move types and PP, learnability through <see cref="LearnPossible"/>
/// over the mon's own encounter, the game's held-item table, growth-rate EXP tables);
/// the offline <see cref="DexFacts"/> table adds power, accuracy, category and prose.
/// Romhack sessions without a PKHeX entity get the parts their own data can answer
/// (abilities, species cards); move facts need PKHeX's per-game tables and stay off there.
/// Every computation runs on clones: nothing here writes.
/// </summary>
public sealed class MonInfoService : IMonInfoService
{
    private static readonly string[] SlotLabels = ["1", "2", "Hidden"];

    public IReadOnlyList<MoveChoice> GetMoveChoices(ISaveEngineSession session, int box, int slot, int? species = null, int? form = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        // Romhack sessions number moves their own way: callers fall back to the plain list.
        if (session is not SaveEngineSession engine)
            return [];

        var save = engine.SaveFile;
        var context = save.Context;
        PKM? stored = null;
        try { stored = engine.GetEntity(box, slot); }
        catch (ArgumentException) { }
        var learn = stored is { Species: > 0 } ? LearnSources(stored, species, form, save) : new Dictionary<int, MoveLearn>();

        var dummied = MoveInfo.GetDummiedMovesHashSet(context);
        var types = MoveInfo.GetTypeTable(context);
        var pp = MoveInfo.GetPPTable(context);
        var choices = new List<MoveChoice>(save.MaxMoveID);
        for (var move = 1; move <= save.MaxMoveID; move++)
        {
            var id = (ushort)move;
            if (!MoveInfo.IsMoveKnowable(id) || (dummied.Length > 0 && MoveInfo.IsDummiedMove(dummied, id))) continue;
            var fact = DexFacts.Move(move);
            var type = move < types.Length ? types[move] : fact?.Type ?? 0;
            var category = TypeFacts.CategoryIn(engine.Generation, fact?.Category ?? MoveCategory.Status, type);
            choices.Add(new MoveChoice(move, type, category, fact?.Power ?? 0, fact?.Accuracy ?? 0,
                move < pp.Length ? pp[move] : 0, learn.GetValueOrDefault(move), fact?.Effect ?? ""));
        }
        return choices;
    }

    /// <summary>
    /// How each move can be known, from PKHeX's own learnability walk over the mon's
    /// matched encounter and evolution history (every game it passed through). With a
    /// pending species edit the walk runs on a clone wearing the new species.
    /// </summary>
    internal static Dictionary<int, MoveLearn> LearnSources(PKM stored, int? species, int? form, SaveFile save)
    {
        var pk = stored.Clone();
        if (species is > 0 && species != pk.Species)
        {
            pk.Species = (ushort)species.Value;
            pk.Form = (byte)Math.Max(0, form ?? 0);
            // The old species' encounter moves say nothing about the new one.
            pk.RelearnMove1 = pk.RelearnMove2 = pk.RelearnMove3 = pk.RelearnMove4 = 0;
            pk.Move1 = pk.Move2 = pk.Move3 = pk.Move4 = 0;
        }
        else if (form is { } f && f != pk.Form)
            pk.Form = (byte)Math.Max(0, f);

        var result = new Dictionary<int, MoveLearn>();
        try
        {
            var la = new LegalityAnalysis(pk);
            var enc = la.EncounterMatch;
            var history = la.Info.EvoChainsAllGens;
            bool[] Flags(MoveSourceType types) => LearnPossible.Get(pk, enc, history, types);
            var all = Flags(MoveSourceType.All);
            if (Array.IndexOf(all, true) < 0)
                return FallbackLearnset(pk, save);

            var level = Flags(MoveSourceType.LevelUp);
            var evolve = Flags(MoveSourceType.Evolve);
            var machine = Flags(MoveSourceType.AllMachines);
            var tutor = Flags(MoveSourceType.AllTutors);
            var egg = Flags(MoveSourceType.SharedEggMove);
            var learnset = LearnsetFor(save.Version, pk.Species, pk.Form);
            var eggMoves = enc.IsEgg ? GameData.GetLearnSource(enc.Version).GetEggMoves(enc.Species, enc.Form) : [];
            ReadOnlySpan<ushort> relearn = [pk.RelearnMove1, pk.RelearnMove2, pk.RelearnMove3, pk.RelearnMove4];

            for (var move = 1; move < all.Length; move++)
            {
                if (!all[move]) continue;
                var id = (ushort)move;
                MoveLearn source;
                if (level[move])
                    source = new MoveLearn(LearnKind.LevelUp, learnset is not null && learnset.TryGetLevelLearnMove(id, out var lv) ? lv : 0);
                else if (evolve[move]) source = new MoveLearn(LearnKind.Evolution);
                else if (machine[move]) source = new MoveLearn(LearnKind.Machine);
                else if (tutor[move]) source = new MoveLearn(LearnKind.Tutor);
                else if (egg[move] || eggMoves.Contains(id)) source = new MoveLearn(LearnKind.Egg);
                else if (relearn.Contains(id)) source = new MoveLearn(LearnKind.Relearn);
                else source = new MoveLearn(LearnKind.Special);
                result[move] = source;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return FallbackLearnset(pk, save);
        }
        return result;
    }

    /// <summary>An encounter the legality walk cannot place (a fresh species edit): the
    /// game's own level-up list for the species, the least that is certainly learnable.</summary>
    private static Dictionary<int, MoveLearn> FallbackLearnset(PKM pk, SaveFile save)
    {
        var result = new Dictionary<int, MoveLearn>();
        var learnset = LearnsetFor(save.Version, pk.Species, pk.Form);
        if (learnset is null) return result;
        var moves = learnset.GetAllMoves();
        var levels = learnset.GetAllLevels();
        for (var i = 0; i < moves.Length; i++)
            if (!result.ContainsKey(moves[i]))
                result[moves[i]] = new MoveLearn(LearnKind.LevelUp, levels[i]);
        return result;
    }

    private static Learnset? LearnsetFor(GameVersion version, ushort species, byte form)
    {
        try { return GameData.GetLearnSource(version).GetLearnset(species, form); }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException or InvalidOperationException) { return null; }
    }

    public MoveChoice? GetMove(ISaveEngineSession session, int move)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (move <= 0 || session is not SaveEngineSession engine || move > engine.SaveFile.MaxMoveID) return null;
        var context = engine.SaveFile.Context;
        var fact = DexFacts.Move(move);
        var types = MoveInfo.GetTypeTable(context);
        var pp = MoveInfo.GetPPTable(context);
        var type = move < types.Length ? types[move] : fact?.Type ?? 0;
        return new MoveChoice(move, type, TypeFacts.CategoryIn(session.Generation, fact?.Category ?? MoveCategory.Status, type),
            fact?.Power ?? 0, fact?.Accuracy ?? 0, move < pp.Length ? pp[move] : 0, default, fact?.Effect ?? "");
    }

    public IReadOnlyList<int> GetMaxPPByUps(ISaveEngineSession session, int box, int slot, int move)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not SaveEngineSession engine || move <= 0) return [];
        var pk = engine.GetEntity(box, slot);
        return pk.Species == 0 ? [] : Enumerable.Range(0, 4).Select(ups => pk.GetMovePP((ushort)move, ups)).ToArray();
    }

    public IReadOnlyList<AbilityChoice> GetAbilityChoices(ISaveEngineSession session, int species, int form)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not SaveEngineSession engine)
            return session.GetAbilityChoices(species, form)
                .Select((id, i) => new AbilityChoice(id, SlotLabels[Math.Min(i, 2)], DexFacts.Ability(id) ?? "")).ToList();

        var personal = engine.SaveFile.Personal.GetFormEntry((ushort)species, (byte)Math.Max(0, form));
        var list = new List<AbilityChoice>(personal.AbilityCount);
        for (var i = 0; i < personal.AbilityCount; i++)
        {
            var ability = personal.GetAbilityAtIndex(i);
            if (ability == 0 || list.Any(a => a.Id == ability)) continue;
            list.Add(new AbilityChoice(ability, SlotLabels[Math.Min(i, 2)], DexFacts.Ability(ability) ?? ""));
        }
        return list;
    }

    public IReadOnlyList<int> GetHeldItems(ISaveEngineSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not SaveEngineSession engine) return [];
        var held = engine.SaveFile.HeldItems;
        var list = new List<int>(held.Length);
        foreach (var item in held)
            if (item != 0 && !list.Contains(item))
                list.Add(item);
        return list;
    }

    public int? GetHiddenPowerType(ISaveEngineSession session, IReadOnlyList<int> ivs)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!HasHiddenPower(session) || ivs.Count != 6) return null;
        Span<int> pkhex = stackalloc int[6];
        ToPkhexOrder(ivs, pkhex);
        return HiddenPower.GetType(pkhex, ContextOf(session)) + 1; // HP types start at Fighting
    }

    public IReadOnlyList<int>? GetIVsForHiddenPower(ISaveEngineSession session, IReadOnlyList<int> ivs, int type)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!HasHiddenPower(session) || ivs.Count != 6 || type is < 1 or > 16) return null;
        var context = ContextOf(session);
        Span<int> pkhex = stackalloc int[6];
        ToPkhexOrder(ivs, pkhex);
        // Keep flawless IVs where PKHeX can (it lowers the fewest 31s); otherwise force the
        // low bits, which moves each IV by at most one point.
        if (!HiddenPower.SetIVsForType(type - 1, pkhex, context))
            HiddenPower.SetIVs(type - 1, pkhex, context);
        if (HiddenPower.GetType(pkhex, context) != type - 1) return null;
        // PKHeX H/A/B/S/C/D -> display H/A/B/C/D/S.
        return [pkhex[0], pkhex[1], pkhex[2], pkhex[4], pkhex[5], pkhex[3]];
    }

    /// <summary>Hidden Power exists from Gen 2 through Gen 7 (and Let's Go); Gen 8+ removed it.</summary>
    private static bool HasHiddenPower(ISaveEngineSession session) => session.Generation is >= 2 and <= 7;

    private static EntityContext ContextOf(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile.Context : session.Generation == 2 ? EntityContext.Gen2 : EntityContext.Gen3;

    private static void ToPkhexOrder(IReadOnlyList<int> display, Span<int> pkhex)
    {
        pkhex[0] = display[0]; pkhex[1] = display[1]; pkhex[2] = display[2];
        pkhex[3] = display[5]; pkhex[4] = display[3]; pkhex[5] = display[4];
    }

    public LevelInfo? GetLevelInfo(ISaveEngineSession session, int box, int slot, int level, StatPreviewOverrides? pending = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not SaveEngineSession engine) return null;
        PKM stored;
        try { stored = engine.GetEntity(box, slot); }
        catch (ArgumentException) { return null; }
        if (stored.Species == 0) return null;

        level = Math.Clamp(level, 1, 100);
        var species = pending?.Species is > 0 ? (ushort)pending.Species.Value : stored.Species;
        var form = species == stored.Species ? stored.Form : (byte)0;
        var growth = engine.SaveFile.Personal.GetFormEntry(species, form).EXPGrowth;
        long expAt = Experience.GetEXP((byte)level, growth);
        long expNext = level >= 100 ? expAt : Experience.GetEXP((byte)(level + 1), growth);

        IReadOnlyList<int> now = [], atLevel = [];
        if (stored.Format >= 3)
        {
            var baseline = pending ?? new StatPreviewOverrides();
            now = StatPreviewService.PreviewEntity(stored, baseline with { Level = null })?.Current ?? [];
            atLevel = StatPreviewService.PreviewEntity(stored, baseline with { Level = level })?.Current ?? [];
        }
        return new LevelInfo(level, expAt, expNext, stored.MetLevel, now, atLevel);
    }

    public SpeciesCard? GetSpeciesCard(ISaveEngineSession session, int species, int form)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (species <= 0 || species > session.MaxSpeciesId) return null;
        var abilities = GetAbilityChoices(session, species, form);
        if (session is not SaveEngineSession engine)
            return new SpeciesCard(species, form, session.GetSpeciesTypes(species), session.GetBaseStats(species), abilities, null);

        var p = engine.SaveFile.Personal.GetFormEntry((ushort)species, (byte)Math.Max(0, form));
        IReadOnlyList<int> types = p.Type1 == p.Type2 ? [p.Type1] : [p.Type1, p.Type2];
        return new SpeciesCard(species, form, types, new BaseStats(p.HP, p.ATK, p.DEF, p.SPA, p.SPD, p.SPE),
            abilities, engine.Generation >= 2 ? new GenderRatio(p.Gender) : null);
    }

    public BatchDryRun DryRunBatch(ISaveEngineSession session, IReadOnlyList<string> instructions,
        IReadOnlyList<int>? boxes = null, IReadOnlyList<(int Box, int Slot)>? slots = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(instructions);
        if (session is not SaveEngineSession engine) return new BatchDryRun(0, 0, 0, 0);
        var save = engine.SaveFile;

        IEnumerable<(int Box, int Slot)> Targets()
        {
            if (slots is not null)
            {
                foreach (var s in slots)
                    if ((s.Box == -1 || (uint)s.Box < (uint)save.BoxCount) && s.Slot >= 0 && s.Slot < (s.Box == -1 ? 6 : save.BoxSlotCount))
                        yield return s;
                yield break;
            }
            foreach (var box in boxes ?? Enumerable.Range(0, save.BoxCount))
            {
                if (box != -1 && (uint)box >= (uint)save.BoxCount) continue;
                for (var slot = 0; slot < (box == -1 ? 6 : save.BoxSlotCount); slot++)
                    yield return (box, slot);
            }
        }

        int targeted = 0, affected = 0, becomeIllegal = 0, alreadyIllegal = 0;
        foreach (var (box, slot) in Targets())
        {
            var original = engine.GetEntity(box, slot);
            if (original.Species == 0) continue;
            targeted++;
            var work = original.Clone();
            SaveEngineSession.ApplyInstructions(work, instructions);
            work.RefreshChecksum();
            // Box slots persist only the stored block: party-only fields (current HP, the
            // cached level) never reach the save, so they do not count as a change there.
            var persisted = Math.Min(original.Data.Length, box == -1 ? original.SIZE_PARTY : original.SIZE_STORED);
            if (work.Data[..persisted].SequenceEqual(original.Data[..persisted])) continue;
            affected++;
            var legalBefore = new LegalityAnalysis(original).Valid;
            if (!legalBefore) { alreadyIllegal++; continue; }
            if (!new LegalityAnalysis(work).Valid) becomeIllegal++;
        }
        return new BatchDryRun(targeted, affected, becomeIllegal, alreadyIllegal);
    }
}
