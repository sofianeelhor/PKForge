using System.Text.RegularExpressions;
using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Manual evolution driven by PKHeX's own per-game evolution tables. Every option is read
/// from <see cref="EvolutionTree"/> for the Pokémon's format, so the list matches what its
/// game allows. Evolving mirrors the game: species/form change, the trade or held item
/// consumed, a default nickname follows the new species, stats recomputed at the same EXP,
/// the ability slot kept, evolution moves offered, the new species registered in the dex, and
/// a trade evolution leaving the mark a real link trade leaves (Gen 6+ handling trainer).
/// <para>Policy: trade evolutions are always offered (that is the gap this fills - the
/// player has no second console); every other method only when its condition is met, or
/// anything in HaX mode.</para>
/// </summary>
public sealed class EvolutionService : IEvolutionService
{
    private static readonly Lazy<GameStrings> Strings = new(() => GameInfo.GetStrings("en"));

    /// <summary>The link partner recorded on an untraded Gen 6+ Pokémon when none is given.</summary>
    public const string DefaultTradePartner = "Link Pal";

    private const ushort Karrablast = 588;

    /// <summary>Evolving writes through PKHeX's dex routine only (no handler or record rewrite).</summary>
    private static readonly EntityImportSettings DexOnly =
        new(EntityImportOption.Disable, EntityImportOption.Enable, EntityImportOption.Disable);

    public EvolutionPlan Plan(ISaveEngineSession session, int box, int slot, bool hax)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session is not SaveEngineSession engine)
            return Empty(session.ReadEntity(box, slot), "Evolving is not available for this game yet.");

        var pk = engine.GetEntity(box, slot);
        var detail = session.ReadEntity(box, slot);
        if (pk.Species == 0) return Empty(detail, "This slot is empty.");
        if (pk.IsEgg) return Empty(detail, "Eggs have to hatch before they can evolve.");

        var options = Candidates(engine.SaveFile, pk)
            .Select((c, i) => Describe(engine.SaveFile, pk, c, i, hax))
            .ToList();
        var moves = new ushort[4];
        pk.GetMoves(moves);
        return new EvolutionPlan(pk.Species, pk.Form, SpeciesName(pk.Species), detail.Nickname, pk.IsShiny, pk.CurrentLevel,
            pk.Format, moves.Where(m => m != 0).Select(MoveName).ToList(), options,
            options.Count == 0 ? $"{SpeciesName(pk.Species)} does not evolve in this game." : null);
    }

    public GenerationOutcome Evolve(ISaveEngineSession session, int box, int slot, EvolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        if (session is not SaveEngineSession engine)
            return new GenerationOutcome(false, "Evolving is not available for this game yet.");
        var sav = engine.SaveFile;
        var original = engine.GetEntity(box, slot);
        if (original.Species == 0 || original.IsEgg) return new GenerationOutcome(false, "Nothing here can evolve.");

        var candidates = Candidates(sav, original);
        if ((uint)request.OptionId >= (uint)candidates.Count)
            return new GenerationOutcome(false, "That evolution is no longer available.");
        var candidate = candidates[request.OptionId];
        var option = Describe(sav, original, candidate, request.OptionId, request.Hax);
        if (!option.Available)
            return new GenerationOutcome(false, option.BlockedReason ?? "That evolution is not possible right now.");

        var fromName = SpeciesName(original.Species);
        var pk = original.Clone();
        Apply(sav, pk, candidate, box == -1 || pk.PartyStatsPresent);
        if (candidate.Method.Method.IsTrade)
            RecordTrade(sav, pk, request.TradePartnerName);
        LearnMoves(pk, option, request);

        // Items: trade/held-item evolutions eat the held item; stones come out of the bag.
        if (IsHeldItemConsumed(candidate.Method.Method)) pk.HeldItem = 0;
        if (candidate.Method.Method is EvolutionType.UseItem or EvolutionType.UseItemMale or EvolutionType.UseItemFemale
            or EvolutionType.UseItemWormhole or EvolutionType.UseItemFullMoon)
            TryConsumeBagItem(sav, candidate.Method.Argument);

        pk.RefreshChecksum();
        if (box == -1) sav.SetPartySlotAtIndex(pk, slot, DexOnly);
        else sav.SetBoxSlotAtIndex(pk, box, slot, DexOnly);

        var name = pk.IsNicknamed ? pk.Nickname : option.SpeciesName;
        return new GenerationOutcome(true, $"{(original.IsNicknamed ? original.Nickname : fromName)} evolved into {option.SpeciesName}!"
            + (option.ConsumedItem is { } used ? $" {used} was used up." : string.Empty)
            + (name != option.SpeciesName ? $" It is still called {name}." : string.Empty));
    }

    // ── Candidates ───────────────────────────────────────────────────────────

    internal readonly record struct Candidate(EvolutionMethod Method, ushort Species, byte Form);

    /// <summary>Forward evolutions from PKHeX's table for the entity's context, filtered to the
    /// ones its game actually contains, de-duplicated (tables repeat entries per version).</summary>
    internal static List<Candidate> Candidates(SaveFile sav, PKM pk)
    {
        EvolutionTree tree;
        try { tree = EvolutionTree.GetEvolutionTree(pk.Context); }
        catch (ArgumentOutOfRangeException) { return []; }

        var result = new List<Candidate>();
        foreach (var method in tree.Forward.GetForward(pk.Species, pk.Form).Span)
        {
            if (method.Method is EvolutionType.None or EvolutionType.Invalid or EvolutionType.LevelUpShedinja) continue;
            var form = method.GetDestinationForm(pk.Form);
            if (method.Species == 0 || method.Species > sav.MaxSpeciesID || method.Species > pk.MaxSpeciesID) continue;
            if (!sav.Personal.IsPresentInGame(method.Species, form)) continue;
            if (result.Any(c => c.Species == method.Species && c.Form == form && c.Method.Method == method.Method
                && c.Method.Argument == method.Argument && c.Method.Level == method.Level)) continue;
            result.Add(new Candidate(method, method.Species, form));
        }
        return result;
    }

    // ── Describe (the card) ──────────────────────────────────────────────────

    private static EvolutionOption Describe(SaveFile sav, PKM pk, Candidate c, int id, bool hax)
    {
        var method = c.Method;
        var trigger = TriggerOf(method.Method);
        var items = Strings.Value.GetItemStrings(pk.Context, pk.Version);
        var itemName = ItemName(items, method.Argument);
        var (met, unmetReason) = Condition(sav, pk, c, items);
        var available = met || hax;

        var preview = pk.Clone();
        Apply(sav, preview, c, recomputeStats: true);
        var nickname = pk.IsNicknamed ? null : SpeciesName(c.Species);
        var consumed = IsHeldItemConsumed(method.Method) ? itemName
            : method.Method is EvolutionType.UseItem or EvolutionType.UseItemMale or EvolutionType.UseItemFemale
                or EvolutionType.UseItemWormhole or EvolutionType.UseItemFullMoon ? itemName : null;
        var handler = method.Method.IsTrade && pk is IHandlerUpdate && pk.IsUntraded
            ? $"Recorded as link-traded: {DefaultTradePartner} becomes its handling trainer, then it comes home to you."
            : method.Method.IsTrade && pk.Format >= 6
                ? "Already traded once: its trainer memories stay as they are."
                : null;

        return new EvolutionOption(id, c.Species, c.Form, SpeciesName(c.Species), trigger, Requirement(pk, method, items),
            met, available, available ? null : unmetReason, consumed, handler, nickname,
            AbilityName(pk), AbilityName(preview), StatsOf(pk), StatsOf(preview), MoveOffers(sav, pk, c));
    }

    private static EvolutionTrigger TriggerOf(EvolutionType type) => type switch
    {
        EvolutionType.Trade => EvolutionTrigger.Trade,
        EvolutionType.TradeHeldItem => EvolutionTrigger.TradeHoldingItem,
        EvolutionType.TradeShelmetKarrablast => EvolutionTrigger.TradeForPartner,
        EvolutionType.LevelUp or EvolutionType.LevelUpATK or EvolutionType.LevelUpAeqD or EvolutionType.LevelUpDEF
            or EvolutionType.LevelUpECl5 or EvolutionType.LevelUpECgeq5 or EvolutionType.LevelUpNinjask
            or EvolutionType.LevelUpMale or EvolutionType.LevelUpFemale or EvolutionType.LevelUpFormFemale1
            or EvolutionType.LevelUpNatureAmped or EvolutionType.LevelUpNatureLowKey => EvolutionTrigger.LevelUp,
        EvolutionType.LevelUpFriendship or EvolutionType.LevelUpFriendshipMorning or EvolutionType.LevelUpFriendshipNight
            => EvolutionTrigger.Friendship,
        EvolutionType.UseItem or EvolutionType.UseItemMale or EvolutionType.UseItemFemale
            or EvolutionType.UseItemWormhole or EvolutionType.UseItemFullMoon => EvolutionTrigger.UseItem,
        EvolutionType.LevelUpHeldItemDay or EvolutionType.LevelUpHeldItemNight => EvolutionTrigger.LevelUpHoldingItem,
        EvolutionType.LevelUpKnowMove => EvolutionTrigger.LevelUpKnowingMove,
        _ => EvolutionTrigger.Special,
    };

    /// <summary>The requirement in the games' own words.</summary>
    internal static string Requirement(PKM pk, EvolutionMethod m, string[] items)
    {
        var item = ItemName(items, m.Argument);
        var level = m.Level > 1 ? $"Level {m.Level}" : "Level up";
        return m.Method switch
        {
            EvolutionType.Trade => "Trade",
            EvolutionType.TradeHeldItem => $"Trade holding {item}",
            EvolutionType.TradeShelmetKarrablast => pk.Species == Karrablast ? "Trade with Shelmet" : "Trade with Karrablast",
            EvolutionType.LevelUp => level,
            EvolutionType.LevelUpFriendship => "Level up with high friendship",
            EvolutionType.LevelUpFriendshipMorning => "Level up with high friendship by day",
            EvolutionType.LevelUpFriendshipNight => "Level up with high friendship at night",
            EvolutionType.UseItem or EvolutionType.UseItemWormhole or EvolutionType.UseItemFullMoon => $"Use {item}",
            EvolutionType.UseItemMale => $"Use {item} (male)",
            EvolutionType.UseItemFemale => $"Use {item} (female)",
            EvolutionType.LevelUpHeldItemDay => $"Level up holding {item} by day",
            EvolutionType.LevelUpHeldItemNight => $"Level up holding {item} at night",
            EvolutionType.LevelUpKnowMove => $"Level up knowing {MoveName(m.Argument)}",
            EvolutionType.LevelUpATK => $"{level} with Attack > Defense",
            EvolutionType.LevelUpAeqD => $"{level} with Attack = Defense",
            EvolutionType.LevelUpDEF => $"{level} with Defense > Attack",
            EvolutionType.LevelUpECl5 or EvolutionType.LevelUpECgeq5 => $"{level} (personality decides which)",
            EvolutionType.LevelUpNinjask => level,
            EvolutionType.LevelUpBeauty => "Level up with high Beauty",
            EvolutionType.LevelUpMale => $"{level} (male)",
            EvolutionType.LevelUpFemale or EvolutionType.LevelUpFormFemale1 => $"{level} (female)",
            EvolutionType.LevelUpElectric => "Level up at a magnetic field",
            EvolutionType.LevelUpForest => "Level up near a Moss Rock",
            EvolutionType.LevelUpCold => "Level up near an Ice Rock",
            EvolutionType.LevelUpInverted => $"{level} holding the console upside down",
            EvolutionType.LevelUpAffection50MoveType => "Level up with high affection knowing a Fairy move",
            EvolutionType.LevelUpMoveType => $"{level} with a Dark-type in the party",
            EvolutionType.LevelUpWeather => $"{level} in rain or fog",
            EvolutionType.LevelUpMorning => $"{level} by day",
            EvolutionType.LevelUpNight => $"{level} at night",
            EvolutionType.LevelUpDusk => $"{level} at dusk",
            EvolutionType.LevelUpWithTeammate => "Level up with a specific teammate",
            EvolutionType.LevelUpSummit => "Level up at Mount Lanakila",
            EvolutionType.LevelUpVersion or EvolutionType.LevelUpVersionDay or EvolutionType.LevelUpVersionNight
                => $"{level} in the right game version",
            EvolutionType.LevelUpNatureAmped or EvolutionType.LevelUpNatureLowKey => $"{level} (nature decides the form)",
            EvolutionType.CriticalHitsInBattle => "Land 3 critical hits in one battle",
            EvolutionType.HitPointsLostInBattle => "Lose 49+ HP, then pass the Dusty Bowl",
            EvolutionType.Spin => "Spin while holding a Sweet",
            EvolutionType.TowerOfDarkness => "Train in the Tower of Darkness",
            EvolutionType.TowerOfWaters => "Train in the Tower of Waters",
            EvolutionType.LevelUpWalkStepsWith => "Level up after walking 1,000 steps with it",
            EvolutionType.LevelUpUnionCircle => "Level up in a Union Circle",
            EvolutionType.LevelUpCollect999 => "Collect 999 Gimmighoul Coins",
            EvolutionType.LevelUpDefeatEquals => "Defeat 3 Bisharp leading Pawniard",
            EvolutionType.LevelUpUseMoveSpecial => "Use Rage Fist 20 times",
            EvolutionType.LevelUpInBattleEC100 or EvolutionType.LevelUpInBattleECElse => $"{level} in battle",
            EvolutionType.LevelUpKnowMoveEC100 or EvolutionType.LevelUpKnowMoveECElse => $"Level up knowing {MoveName(m.Argument)}",
            EvolutionType.LevelUpRecoilDamageMale or EvolutionType.LevelUpRecoilDamageFemale => "Lose 294 HP from recoil",
            EvolutionType.Hisui => "Evolve in Hisui",
            EvolutionType.UseMoveAgileStyle => "Use its signature move in Agile Style 20 times",
            EvolutionType.UseMoveStrongStyle => "Use its signature move in Strong Style 20 times",
            EvolutionType.UseMoveBarbBarrage => "Use Barb Barrage 20 times",
            EvolutionType.LevelUpWormhole => "Level up in Ultra Space",
            _ => Humanize(m.Method.ToString()),
        };
    }

    /// <summary>Is the requirement satisfied right now? Trades always are (the partner is simulated);
    /// methods PKForge cannot observe (places, weather, counters) are never "met" - HaX only.</summary>
    private static (bool Met, string? Reason) Condition(SaveFile sav, PKM pk, Candidate c, string[] items)
    {
        var m = c.Method;
        var held = ItemName(items, pk.HeldItem);
        if (m.Method.IsTrade)
        {
            if (held == "Everstone") return (false, "It's holding an Everstone, which stops trade evolution. Take it off first.");
            if (m.Method == EvolutionType.TradeHeldItem && pk.HeldItem != m.Argument)
                return (false, $"Give it {ItemName(items, m.Argument)} to hold first.");
            return (true, null);
        }

        const string needHax = "PKForge can't confirm this condition. Evolve it in-game, or turn on HaX mode.";
        switch (m.Method)
        {
            case EvolutionType.UseItem or EvolutionType.UseItemMale or EvolutionType.UseItemFemale:
                if (m.Check(pk, pk.CurrentLevel, pk.CurrentLevel, false, EvolutionRuleTweak.Default) != EvolutionCheckResult.Valid)
                    return (false, "This one's gender can't evolve that way.");
                return BagCount(sav, m.Argument) > 0
                    ? (true, null)
                    : (false, $"You need {ItemName(items, m.Argument)} in your bag, or turn on HaX mode.");
            case EvolutionType.LevelUpFriendship or EvolutionType.LevelUpFriendshipMorning or EvolutionType.LevelUpFriendshipNight:
            {
                var threshold = pk.Format >= 8 ? 160 : 220;
                if (pk.CurrentFriendship < threshold) return (false, $"Friendship is {pk.CurrentFriendship}; it needs {threshold}+ and a level up.");
                return LevelCheck(pk, m);
            }
            case EvolutionType.LevelUpHeldItemDay or EvolutionType.LevelUpHeldItemNight:
                if (pk.HeldItem != m.Argument) return (false, $"Give it {ItemName(items, m.Argument)} to hold first.");
                return LevelCheck(pk, m);
            case EvolutionType.LevelUpKnowMove:
                if (!pk.HasMove(m.Argument)) return (false, $"It has to know {MoveName(m.Argument)} first.");
                return LevelCheck(pk, m);
            case EvolutionType.LevelUpATK when pk.Stat_ATK <= pk.Stat_DEF && pk.PartyStatsPresent:
                return (false, "Its Attack has to be higher than its Defense.");
            case EvolutionType.LevelUpDEF when pk.Stat_DEF <= pk.Stat_ATK && pk.PartyStatsPresent:
                return (false, "Its Defense has to be higher than its Attack.");
            case EvolutionType.LevelUpAeqD when pk.Stat_ATK != pk.Stat_DEF && pk.PartyStatsPresent:
                return (false, "Its Attack and Defense have to be equal.");
            case EvolutionType.LevelUp or EvolutionType.LevelUpATK or EvolutionType.LevelUpDEF or EvolutionType.LevelUpAeqD
                or EvolutionType.LevelUpECl5 or EvolutionType.LevelUpECgeq5 or EvolutionType.LevelUpNinjask
                or EvolutionType.LevelUpMale or EvolutionType.LevelUpFemale or EvolutionType.LevelUpFormFemale1
                or EvolutionType.LevelUpNatureAmped or EvolutionType.LevelUpNatureLowKey:
                return LevelCheck(pk, m);
            default:
                return (false, needHax);
        }
    }

    /// <summary>PKHeX's own level/gender/form/personality check. The game evolves on a level-up,
    /// so a mon still at its met level must be leveled once first.</summary>
    private static (bool, string?) LevelCheck(PKM pk, EvolutionMethod m)
    {
        var levelMin = pk.Format <= 2 ? (byte)1 : pk.MetLevel;
        return m.Check(pk, pk.CurrentLevel, levelMin, false, EvolutionRuleTweak.Default) switch
        {
            EvolutionCheckResult.Valid => (true, null),
            EvolutionCheckResult.InsufficientLevel when m.Level > pk.CurrentLevel => (false, $"It needs to reach level {m.Level}."),
            EvolutionCheckResult.InsufficientLevel => (false, "It evolves on its next level-up; level it once in-game (or use HaX)."),
            EvolutionCheckResult.BadGender => (false, "This one's gender can't evolve that way."),
            EvolutionCheckResult.WrongEC or EvolutionCheckResult.BadForm => (false, "Its personality decides a different evolution."),
            _ => (false, "The game's condition isn't met."),
        };
    }

    // ── Apply ────────────────────────────────────────────────────────────────

    private static bool IsHeldItemConsumed(EvolutionType type) =>
        type is EvolutionType.TradeHeldItem or EvolutionType.LevelUpHeldItemDay or EvolutionType.LevelUpHeldItemNight;

    /// <summary>The evolution itself, as the game performs it at the same EXP.</summary>
    private static void Apply(SaveFile sav, PKM pk, Candidate c, bool recomputeStats)
    {
        var nicknamed = pk.IsNicknamed;
        var abilitySlot = pk.AbilityNumber switch { 2 => 1, 4 => 2, _ => 0 };
        var oldMax = pk.Stat_HPMax;
        var oldHp = pk.Stat_HPCurrent;
        var exp = pk.EXP;

        pk.Species = c.Species;
        pk.Form = c.Form;
        pk.EXP = exp;
        if (!nicknamed) pk.ClearNickname();

        if (pk.Format >= 3)
        {
            var pi = pk.PersonalInfo;
            var index = abilitySlot < pi.AbilityCount && pi.GetAbilityAtIndex(abilitySlot) != 0 ? abilitySlot : 0;
            var ability = pi.GetAbilityAtIndex(index);
            // Gen 3-4 keep the PID's ability bit even when the evolution has a single ability.
            if (pk.Format <= 4) pk.Ability = ability;
            else pk.RefreshAbility(index);
        }
        if (pk is ICombatPower cp) cp.ResetCP();
        if (pk is PB7 lgpe) lgpe.ResetCalculatedValues();

        if (recomputeStats && (pk.PartyStatsPresent || pk.Format <= 2))
        {
            Span<ushort> stats = stackalloc ushort[6];
            pk.LoadStats(pk.PersonalInfo, stats);
            var status = pk.Status_Condition;
            pk.SetStats(stats);
            pk.Stat_Level = pk.CurrentLevel;
            // The game adds the HP gained to the current HP; damage taken stays taken.
            pk.Stat_HPCurrent = oldMax == 0 ? stats[0] : Math.Clamp(oldHp + (stats[0] - oldMax), 0, stats[0]);
            pk.Status_Condition = status;
        }
    }

    /// <summary>A link trade leaves a trace from Gen 6 on: the Pokémon remembers the partner who
    /// held it. An untraded mon gets that partner recorded, then returns to its OT
    /// (CurrentHandler 0), which is exactly what PKHeX expects from a trade-evolved mon.</summary>
    private static void RecordTrade(SaveFile sav, PKM pk, string? partnerName)
    {
        if (pk is not IHandlerUpdate handler || !pk.IsUntraded) return;
        var name = string.IsNullOrWhiteSpace(partnerName) ? DefaultTradePartner : partnerName.Trim();
        if (name.Length > pk.MaxStringLengthTrainer) name = name[..pk.MaxStringLengthTrainer];
        var partner = new SimpleTrainerInfo(pk.Version.IsValidSavedVersion() ? pk.Version : sav.Version)
        {
            OT = name,
            Gender = 0,
            Language = pk.Language == 0 ? (int)LanguageID.English : pk.Language,
            TID16 = (ushort)(pk.TID16 ^ 0x5A5A),
            SID16 = (ushort)(pk.SID16 ^ 0xA5A5),
        };
        handler.UpdateHandler(partner);
        pk.CurrentHandler = 0; // traded back home right after evolving
    }

    private static void LearnMoves(PKM pk, EvolutionOption option, EvolutionRequest request)
    {
        var choices = new List<EvolutionMoveChoice>(request.Moves ?? []);
        if (request.LearnIntoFreeSlots)
        {
            var now = new ushort[4];
            foreach (var offer in option.Moves)
            {
                if (choices.Any(ch => ch.Move == offer.Move)) continue;
                pk.GetMoves(now);
                for (var i = 0; i < 4; i++)
                    if (now[i] == 0 && !choices.Any(ch => ch.Slot == i)) { choices.Add(new EvolutionMoveChoice(offer.Move, i)); break; }
            }
        }
        foreach (var choice in choices)
        {
            if (choice.Move <= 0 || choice.Slot is < 0 or > 3 || !option.Moves.Any(o => o.Move == choice.Move)) continue;
            if (pk.HasMove((ushort)choice.Move)) continue;
            var move = (ushort)choice.Move;
            var pp = pk.GetMovePP(move, 0);
            switch (choice.Slot)
            {
                case 0: pk.Move1 = move; pk.Move1_PPUps = 0; pk.Move1_PP = pp; break;
                case 1: pk.Move2 = move; pk.Move2_PPUps = 0; pk.Move2_PP = pp; break;
                case 2: pk.Move3 = move; pk.Move3_PPUps = 0; pk.Move3_PP = pp; break;
                default: pk.Move4 = move; pk.Move4_PPUps = 0; pk.Move4_PP = pp; break;
            }
        }
        pk.FixMoves(); // no gaps before filled slots, like the games
    }

    /// <summary>Evolution moves (learnset level 0) and moves the new species learns at the current level.</summary>
    private static List<EvolutionMoveOffer> MoveOffers(SaveFile sav, PKM pk, Candidate c)
    {
        Learnset learnset;
        try { learnset = GameData.GetLearnSource(sav.Version).GetLearnset(c.Species, c.Form); }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidOperationException or IndexOutOfRangeException) { return []; }

        var moves = learnset.GetAllMoves().ToArray();
        var levels = learnset.GetAllLevels();
        var offers = new List<EvolutionMoveOffer>();
        for (var i = 0; i < moves.Length && i < levels.Length; i++)
        {
            var evolutionMove = levels[i] == 0 && pk.Format >= 7;
            if (!evolutionMove && levels[i] != pk.CurrentLevel) continue;
            if (moves[i] == 0 || moves[i] > pk.MaxMoveID || pk.HasMove(moves[i]) || offers.Any(o => o.Move == moves[i])) continue;
            offers.Add(new EvolutionMoveOffer(moves[i], MoveName(moves[i]), evolutionMove));
        }
        return offers;
    }

    // ── Bag ──────────────────────────────────────────────────────────────────

    private static int BagCount(SaveFile sav, ushort item)
    {
        try
        {
            return sav.Inventory.Pouches.SelectMany(p => p.Items).Where(i => i.Index == item).Sum(i => i.Count);
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException or InvalidOperationException) { return 0; }
    }

    private static void TryConsumeBagItem(SaveFile sav, ushort item)
    {
        try
        {
            var bag = sav.Inventory;
            foreach (var pouch in bag.Pouches)
            {
                var entry = pouch.Items.FirstOrDefault(i => i.Index == item && i.Count > 0);
                if (entry is null) continue;
                entry.Count--;
                if (entry.Count <= 0) entry.Clear();
                pouch.ClearCount0();
                bag.CopyTo(sav);
                return;
            }
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException or InvalidOperationException) { }
    }

    // ── Names ────────────────────────────────────────────────────────────────

    private static EvolutionPlan Empty(EntityDetail detail, string reason) =>
        new(detail.Species, detail.Form, detail.SpeciesName, detail.Nickname, false, detail.Level, 0, [], [], reason);

    private static string SpeciesName(ushort species) =>
        species < Strings.Value.specieslist.Length ? Strings.Value.specieslist[species] : $"#{species}";

    private static string MoveName(ushort move) =>
        move < Strings.Value.movelist.Length ? Strings.Value.movelist[move] : $"Move {move}";

    private static string ItemName(string[] items, int item) =>
        item > 0 && item < items.Length && items[item].Length > 0 ? items[item] : item > 0 ? $"item {item}" : "nothing";

    private static string AbilityName(PKM pk) =>
        pk.Format < 3 ? "—" : pk.Ability < Strings.Value.abilitylist.Length ? Strings.Value.abilitylist[pk.Ability] : $"#{pk.Ability}";

    /// <summary>H/A/B/C/D/S (app order) from PKHeX's H/A/B/S/C/D.</summary>
    private static IReadOnlyList<int> StatsOf(PKM pk)
    {
        Span<ushort> s = stackalloc ushort[6];
        pk.LoadStats(pk.PersonalInfo, s);
        return [s[0], s[1], s[2], s[4], s[5], s[3]];
    }

    private static string Humanize(string id) => Regex.Replace(id, "([a-z0-9])([A-Z])", "$1 $2");
}
