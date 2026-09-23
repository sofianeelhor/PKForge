using System.Diagnostics.CodeAnalysis;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// The result of moving one entity into a save's format. <see cref="Backwards"/> is true
/// when no official route exists (a newer structure written into an older game), in which
/// case <see cref="Warnings"/> says, in plain language, every compromise the downgrade made.
/// </summary>
public sealed record TransferConversion(PKM Entity, bool Backwards, IReadOnlyList<string> Warnings);

/// <summary>
/// Converts an entity into any save's format, either direction. Forward moves use PKHeX's
/// official routes (Pal Park, Poké Transfer, Bank, HOME). Backward moves copy every field
/// the two structures share (PKHeX's reflection transfer, the same path its
/// "allow incompatible conversion" setting enables) and then sanitize the result for the
/// target generation so the game can actually load it: moves, ability, item, ball, form,
/// origin data, language, names, PID-linked traits, IV/EV scales and party stats.
/// PKHeX's global <see cref="EntityConverter.AllowIncompatibleConversion"/> switch is never
/// touched; the downgrade is scoped to this call.
/// The only refusals are physical ones: the species (or every form of it) does not exist
/// in the target game's data.
/// </summary>
public static class CrossFormatConverter
{
    private const int PokeBall = (int)Ball.Poke;

    private static readonly Lazy<GameStrings> Strings = new(() => GameInfo.GetStrings("en"));

    /// <summary>
    /// Converts <paramref name="source"/> into <paramref name="target"/>'s entity format.
    /// Returns null, with a plain-language <paramref name="refusal"/>, only when the Pokémon
    /// physically cannot exist in the target game. The source is never modified.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "PKM subclasses are preserved: PKHeX.Core is consumed whole and every entity type is referenced by EntityBlank.")]
    public static TransferConversion? Convert(PKM source, SaveFile target, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        refusal = null;

        var destType = target.PKMType;
        if (source.GetType() == destType)
            return new TransferConversion(source, false, []);

        refusal = ExplainImpossible(source, target);
        if (refusal is not null)
            return null;

        // An official route wins whenever one exists; PKHeX already sanitizes those.
        var forward = EntityConverter.ConvertToType(source, destType, out var result);
        if (forward is not null && result is EntityConverterResult.Success or EntityConverterResult.None)
            return new TransferConversion(forward, false, []);

        return Downgrade(source, target);
    }

    /// <summary>
    /// Why <paramref name="source"/> cannot exist in <paramref name="target"/> at all, or null
    /// when it can (possibly with compromises). The sentence names the species and the game.
    /// </summary>
    public static string? ExplainImpossible(PKM source, SaveFile target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        var species = source.Species;
        var name = SpeciesLabel(species);
        var generation = target.Generation;
        if (species > target.MaxSpeciesID)
            return $"{name} (No. {species}) does not exist in Generation {generation} games: " +
                   $"their data only knows species No. 1 to {target.MaxSpeciesID}, so there is nothing to store it as.";
        if (!target.Personal.IsPresentInGame(species, 0) && !target.Personal.IsPresentInGame(species, source.Form))
            return $"{name} is not in {VersionLabel(target.Version)}'s species data at all, so the game has no way to store it.";
        return null;
    }

    [RequiresUnreferencedCode("Uses PKHeX's reflection-based property transfer.")]
    private static TransferConversion? Downgrade(PKM source, SaveFile target)
    {
        var warnings = new List<string>
        {
            $"Backwards transfer (Gen {source.Format} format → Gen {target.Generation}): no official route exists, " +
            "so legality checkers will likely flag this Pokémon even though the game can load it.",
        };

        var subject = source.Clone();
        if (subject.IsEgg)
        {
            subject.ForceHatchPKM();
            warnings.Add("The egg hatches: an egg from a newer game cannot be carried into an older one.");
        }

        var pk = target.BlankPKM;
        subject.TransferPropertiesWithReflection(pk);
        if (!EntityConverter.IsCompatibleWithModifications(pk))
            return null; // guarded by ExplainImpossible; kept as a safety net

        var context = new DowngradeContext(subject, pk, target, warnings);
        context.Sanitize();
        return new TransferConversion(pk, true, warnings);
    }

    /// <summary>One downgrade's working state: source, destination, and the running warning list.</summary>
    private sealed class DowngradeContext(PKM src, PKM pk, SaveFile target, List<string> warnings)
    {
        private int Generation => pk.Format;
        private string Gen => $"Gen {pk.Format}";

        public void Sanitize()
        {
            SanitizeForm();
            SanitizeOrigin();
            SanitizeMoves();
            var abilitySlot = SanitizeAbility();
            SanitizeIdentity(abilitySlot);
            SanitizeHeldItem();
            SanitizeBall();
            SanitizeLanguageAndNames();
            SanitizeStatsScale();
            ReportDroppedFeatures();

            pk.CurrentFriendship = src.CurrentFriendship;
            pk.ResetPartyStats();
            pk.HealPP();
            pk.RefreshChecksum();
        }

        private void SanitizeForm()
        {
            if (src.Form == 0 || target.Personal.IsPresentInGame(pk.Species, src.Form))
            {
                if (pk.Form != src.Form && Generation >= 4)
                    pk.Form = src.Form;
                return;
            }
            pk.Form = 0;
            warnings.Add($"Form {src.Form} of {SpeciesLabel(pk.Species)} does not exist in {Gen}: stored as its base form.");
        }

        /// <summary>Origin game, met location, egg location and met level valid for the target structure.</summary>
        private void SanitizeOrigin()
        {
            if (Generation <= 2)
                return; // Gen 1-2 structures carry no origin game

            var originFits = src.Version.IsValidSavedVersion() && src.Version <= pk.MaxGameID
                && src.Generation > 0 && src.Generation <= Generation;
            var fallbackVersion = target.Version.IsValidSavedVersion() && target.Version <= pk.MaxGameID
                ? target.Version
                : target.Context.GetSingleGameVersion();
            if (originFits)
            {
                pk.Version = src.Version;
            }
            else
            {
                pk.Version = fallbackVersion;
                warnings.Add($"Origin game {VersionLabel(src.Version)} did not exist yet in {Gen}: recorded as {VersionLabel(fallbackVersion)}.");
            }

            // A downgraded mon's met data always describes a newer game (or the transfer
            // into it: Pal Park, Poké Transfer, Bank), never a place in the target game.
            var trade = LinkTradeLocation(Generation);
            pk.MetLocation = trade;
            warnings.Add($"Met location has no equivalent in {Gen}: recorded as {LocationLabel(trade, egg: false)}.");
            if (Generation >= 4 && pk.EggLocation != 0)
            {
                pk.EggLocation = 0;
                warnings.Add($"Egg location has no equivalent in {Gen} and is cleared.");
            }
            if (pk.MetLevel > pk.CurrentLevel)
                pk.MetLevel = pk.CurrentLevel;
        }

        /// <summary>
        /// Moves the target cannot hold (index beyond its move table) are replaced with
        /// moves the species learns by level up in the target game at its current level.
        /// </summary>
        private void SanitizeMoves()
        {
            Span<ushort> wanted = [src.Move1, src.Move2, src.Move3, src.Move4];
            var kept = new List<ushort>(4);
            var lost = new List<ushort>(4);
            foreach (var move in wanted)
            {
                if (move == 0) continue;
                if (move <= pk.MaxMoveID && !kept.Contains(move)) kept.Add(move);
                else if (move > pk.MaxMoveID) lost.Add(move);
            }

            // Refill the slots the lost moves leave empty (at least one move must remain).
            var slots = Math.Max(1, kept.Count + lost.Count);
            var gained = new List<ushort>(4);
            if (kept.Count < slots)
            {
                foreach (var move in LevelUpMoves())
                {
                    if (kept.Count >= slots) break;
                    if (kept.Contains(move)) continue;
                    kept.Add(move);
                    gained.Add(move);
                }
                if (kept.Count == 0)
                {
                    kept.Add((ushort)Move.Tackle);
                    gained.Add((ushort)Move.Tackle);
                }
            }

            Span<ushort> moves = stackalloc ushort[4];
            for (var i = 0; i < kept.Count && i < 4; i++)
                moves[i] = kept[i];
            pk.SetMoves(moves);
            Span<int> ppUps = [src.Move1_PPUps, src.Move2_PPUps, src.Move3_PPUps, src.Move4_PPUps];
            for (var i = 0; i < 4; i++)
            {
                var index = wanted.IndexOf(moves[i]);
                SetPPUps(i, moves[i] != 0 && index >= 0 ? ppUps[index] : 0);
            }

            if (lost.Count > 0)
            {
                var replacement = gained.Count > 0
                    ? $" Replaced with level-up moves from {VersionLabel(pk.Format >= 3 ? pk.Version : target.Version)}: {string.Join(", ", gained.Select(MoveLabel))}."
                    : string.Empty;
                warnings.Add($"{string.Join(", ", lost.Select(MoveLabel))} did not exist in {Gen} and cannot be kept.{replacement}");
            }
        }

        /// <summary>The last moves the species learns by level up at its level, most recent first.</summary>
        private IEnumerable<ushort> LevelUpMoves()
        {
            Span<ushort> encounter = stackalloc ushort[4];
            try
            {
                var version = Generation >= 3 ? pk.Version : target.Version;
                var learnset = GameData.GetLearnSource(version).GetLearnset(pk.Species, pk.Form);
                learnset.SetEncounterMoves(pk.CurrentLevel, encounter);
            }
            catch (Exception)
            {
                // No learnset for this game/species pairing: fall back to the caller's default.
                return [];
            }
            var list = new List<ushort>(4);
            for (var i = encounter.Length - 1; i >= 0; i--)
                if (encounter[i] != 0 && encounter[i] <= pk.MaxMoveID)
                    list.Add(encounter[i]);
            return list;
        }

        private void SetPPUps(int index, int value)
        {
            value = Math.Clamp(value, 0, 3);
            switch (index)
            {
                case 0: pk.Move1_PPUps = value; break;
                case 1: pk.Move2_PPUps = value; break;
                case 2: pk.Move3_PPUps = value; break;
                default: pk.Move4_PPUps = value; break;
            }
        }

        /// <summary>Keeps the source's ability slot where the target has it; returns the slot used (0/1/2).</summary>
        private int SanitizeAbility()
        {
            if (Generation <= 2)
                return 0;
            var slot = src.AbilityNumber switch { 4 => 2, 2 => 1, _ => 0 };
            var info = pk.PersonalInfo;
            if (slot == 2 && (Generation < 5 || info.AbilityCount < 3))
            {
                warnings.Add($"Hidden Ability {AbilityLabel(src.Ability)} does not exist in {Gen}: the first regular ability is used instead.");
                slot = 0;
            }
            if (slot == 1 && info.AbilityCount < 2)
                slot = 0;
            pk.RefreshAbility(slot);
            if (pk.Ability != src.Ability && src.Ability > pk.MaxAbilityID && slot != 2)
                warnings.Add($"Ability {AbilityLabel(src.Ability)} did not exist in {Gen}: now {AbilityLabel(pk.Ability)}.");
            return slot;
        }

        /// <summary>
        /// Gen 3-5 derive nature (3-4), gender, ability slot and shininess from the PID. A new
        /// PID is rolled only when the copied one contradicts the source's traits.
        /// </summary>
        private void SanitizeIdentity(int abilitySlot)
        {
            if (Generation <= 2)
            {
                if (pk.IsShiny != src.IsShiny)
                {
                    pk.SetIsShiny(src.IsShiny);
                    if (pk.IsShiny != src.IsShiny)
                        warnings.Add($"Shininess could not be carried into {Gen}'s stat DNA.");
                }
                return;
            }
            if (Generation >= 6)
                return; // PID is free-form from Gen 6 on

            var gen34 = Generation <= 4;
            var nature = src.Nature;
            var gender = src.Gender;
            var abilityBits = gen34 ? (uint)(abilitySlot & 1) : abilitySlot == 1 ? 0x0001_0000u : 0u;
            if (Generation == 5)
            {
                pk.Nature = nature;
                pk.Gender = gender;
            }
            if (PidFits(pk.PID, gen34, nature, gender, abilityBits))
                return;

            var rnd = Util.Rand;
            var fits = false;
            for (var attempt = 0; attempt < 200_000 && !fits; attempt++)
            {
                var pid = EntityPID.GetRandomPID(rnd, pk.Species, gender, pk.Version, nature, pk.Form, abilityBits);
                fits = PidFits(pid, gen34, nature, gender, abilityBits);
                pk.PID = pid;
            }
            if (Generation == 4)
                pk.Gender = gender;
            pk.RefreshAbility(abilitySlot);
            warnings.Add($"New PID generated: {Gen} ties nature, gender and ability to it, and the old one disagreed.");
            if (!fits && pk.IsShiny != src.IsShiny)
                warnings.Add($"Shininess could not be kept alongside the nature and gender in {Gen}'s PID rules.");
        }

        private bool PidFits(uint pid, bool gen34, Nature nature, byte gender, uint abilityBits)
        {
            if (gen34 && pid % 25 != (uint)nature)
                return false;
            if (gen34 ? (pid & 1) != abilityBits : (pid & 0x0001_0000) != abilityBits)
                return false;
            var ratio = pk.PersonalInfo.Gender;
            if (!PersonalInfo.IsSingleGender(ratio) && EntityGender.GetFromPID(pid, ratio) != gender)
                return false;
            if (pk.Species == (ushort)Species.Unown && Generation == 3 && EntityPID.GetUnownForm3(pid) != src.Form)
                return false;
            var xor = (pid >> 16) ^ (pid & 0xFFFF) ^ pk.TID16 ^ pk.SID16;
            return (xor < 8) == src.IsShiny;
        }

        private void SanitizeHeldItem()
        {
            if (Generation == 1 || src.HeldItem == 0)
                return;
            var item = ItemConverter.GetItemForFormat(src.HeldItem, src.Context, pk.Context);
            var allowed = target.HeldItems;
            if (item < 0 || item > pk.MaxItemID || (item != 0 && allowed.Length > 0 && !allowed.Contains((ushort)item)))
                item = 0;
            pk.HeldItem = item;
            if (item == 0)
                warnings.Add($"Held item {ItemLabel(src)} does not exist in {Gen} and is removed.");
        }

        private void SanitizeBall()
        {
            if (Generation <= 2)
                return;
            var ball = src.Ball;
            if (ball is > 0 && ball <= pk.MaxBallID)
            {
                pk.Ball = ball;
                return;
            }
            pk.Ball = PokeBall;
            warnings.Add($"{BallLabel(ball)} did not exist in {Gen}: now a Poké Ball.");
        }

        private void SanitizeLanguageAndNames()
        {
            if (Generation >= 3)
            {
                var available = Language.GetAvailableGameLanguages(pk.Context);
                if (!available.Contains((byte)src.Language))
                {
                    var fallback = src.Language == (int)LanguageID.SpanishL ? LanguageID.Spanish : LanguageID.English;
                    pk.Language = (int)fallback;
                    warnings.Add($"Language {(LanguageID)src.Language} did not exist in {Gen}: recorded as {fallback}.");
                }
                else
                {
                    pk.Language = src.Language;
                }
            }

            pk.OriginalTrainerName = src.OriginalTrainerName;
            if (!string.Equals(pk.OriginalTrainerName, src.OriginalTrainerName, StringComparison.Ordinal))
                warnings.Add($"Trainer name '{src.OriginalTrainerName}' does not fit {Gen}'s character set or length: stored as '{pk.OriginalTrainerName}'.");

            if (!src.IsNicknamed)
            {
                pk.ClearNickname();
                return;
            }
            pk.IsNicknamed = true;
            pk.Nickname = src.Nickname;
            if (string.Equals(pk.Nickname, src.Nickname, StringComparison.Ordinal))
                return;
            if (pk.Nickname.Length == 0)
            {
                pk.ClearNickname();
                warnings.Add($"Nickname '{src.Nickname}' cannot be written in {Gen}'s character set: reset to the species name.");
            }
            else
            {
                warnings.Add($"Nickname '{src.Nickname}' does not fit {Gen}'s character set or length: stored as '{pk.Nickname}'.");
            }
        }

        private void SanitizeStatsScale()
        {
            if (Generation <= 2 && pk is GBPKM gb)
            {
                Span<int> evs = stackalloc int[6];
                src.GetEVs(evs);
                // Gen 1-2 stat experience grows as the square of a modern EV; Special takes Sp. Atk.
                gb.SetSqrtEVs([evs[0], evs[1], evs[2], evs[3], evs[4]]);
                warnings.Add($"IVs become {Gen} DVs (halved, 0-15) and EVs become stat experience; Sp. Def shares Special.");
                return;
            }
            if (src is IHyperTrain { HyperTrainFlags: not 0 } && pk is not IHyperTrain)
                warnings.Add($"Hyper Training does not exist in {Gen}: the stats fall back to the real IVs.");
        }

        private void ReportDroppedFeatures()
        {
            if (src is ITeraTypeReadOnly && pk is not ITeraTypeReadOnly)
                warnings.Add($"Tera Type is dropped: {Gen} has no Terastallization.");
            if (src is IGigantamax { CanGigantamax: true } && pk is not IGigantamax)
                warnings.Add($"Gigantamax Factor is dropped: {Gen} has no Dynamax.");
            if (src is IAlphaReadOnly { IsAlpha: true } && pk is not IAlphaReadOnly)
                warnings.Add($"Alpha status is dropped: {Gen} has no Alphas.");
            if (src.Format >= 6 && Generation < 6 && (src.RelearnMove1 | src.RelearnMove2 | src.RelearnMove3 | src.RelearnMove4) != 0)
                warnings.Add($"Relearnable moves are dropped: {Gen} does not store them.");
            if (src.Format >= 6 && Generation < 6 && src is IMemoryOT { OriginalTrainerMemory: not 0 })
                warnings.Add($"Memories are dropped: {Gen} does not store them.");
        }

        private string LocationLabel(ushort id, bool egg) =>
            GameInfo.GetLocationName(egg, id, pk.Format, pk.Generation, pk.Version) is { Length: > 0 } name ? name : $"#{id}";
    }

    /// <summary>The location a traded-in Pokémon of unknown origin reads as, per generation.</summary>
    private static ushort LinkTradeLocation(int generation) => generation switch
    {
        3 => Locations.LinkTrade3NPC,
        4 => Locations.LinkTrade4,
        5 => Locations.LinkTrade5,
        _ => Locations.LinkTrade6,
    };

    private static string SpeciesLabel(ushort species) =>
        species < Strings.Value.specieslist.Length ? Strings.Value.specieslist[species] : $"Species #{species}";
    private static string MoveLabel(ushort move) =>
        move < Strings.Value.movelist.Length ? Strings.Value.movelist[move] : $"Move #{move}";
    private static string AbilityLabel(int ability) =>
        (uint)ability < Strings.Value.abilitylist.Length ? Strings.Value.abilitylist[ability] : $"Ability #{ability}";
    private static string BallLabel(int ball) =>
        (uint)ball < Strings.Value.balllist.Length && ball > 0 ? Strings.Value.balllist[ball] : $"Ball #{ball}";
    private static string VersionLabel(GameVersion version) =>
        GameInfo.GetVersionName(version) is { Length: > 0 } name ? name : version.ToString();
    private static string ItemLabel(PKM entity)
    {
        var items = Strings.Value.GetItemStrings(entity.Context, entity.Version);
        return entity.HeldItem > 0 && entity.HeldItem < items.Length ? items[entity.HeldItem] : $"#{entity.HeldItem}";
    }
}
