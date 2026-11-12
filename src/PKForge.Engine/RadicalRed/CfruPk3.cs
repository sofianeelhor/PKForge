using PKHeX.Core;

namespace PKForge.Engine.RadicalRed;

/// <summary>
/// The id bridge between a CFRU record and the PK3 other games speak. A CFRU record stores
/// the hack's own species, move, item and ball numbers (Radical Red keeps Hoenn-internal
/// species order, so its Lairon is 383, which a PK3 reads as Groudon); a PK3 stores
/// Generation 3's national ones. Every id crosses through the hack's tables, and an id
/// with no counterpart on the other side refuses the conversion with the reason, never
/// lands as a different id. A ball or ability Generation 3 lacks is the exception: the
/// Pokémon crosses in a Poké Ball with the ability its PID picks, and the change is reported.
/// </summary>
internal static class CfruPk3
{
    private const int MaxSpecies = 386; // Deoxys
    private const int MaxMove = 354;    // Psycho Boost
    private const int MaxBall = 12;     // Premier Ball

    /// <summary>A CFRU mon with every id already read through its own game's tables.</summary>
    /// <param name="BaseSpecies">False for a form the hack stores as its own species id.</param>
    /// <param name="Moves">The stored moves with their national ids (0 when the table has none).</param>
    /// <param name="NationalItem">The held item's modern PKHeX id, 0 when none or unbridged.</param>
    /// <param name="NationalAbility">The active ability's national id, 0 when unbridged.</param>
    /// <param name="Ball">The PKHeX ball id, 0 when the hack's ball has none.</param>
    internal readonly record struct Outbound(
        string SpeciesName, int National, bool BaseSpecies, uint Pid,
        IReadOnlyList<(int Stored, int National, string Name)> Moves,
        int HeldItem, int NationalItem, string ItemName,
        bool HiddenAbility, int NationalAbility, int Ball);

    /// <summary>The ids a Pokémon lands with in a CFRU game, every one in the hack's numbering.</summary>
    /// <param name="AbilitySlot">The PID parity its ability needs there, null when its own PID already picks it.</param>
    internal readonly record struct Inbound(int Species, int[] Moves, int HeldItem, int Ball, bool HiddenAbility, int? AbilitySlot);

    /// <summary>A PK3 carrying the mon's national ids, or null with the reason one of them has
    /// no Generation 3 counterpart. The caller fills every non-id field.</summary>
    /// <param name="adjustments">Receives what changed to fit Generation 3 (ball, ability).</param>
    public static PK3? ToPk3(in Outbound mon, out string? refusal, List<string>? adjustments = null)
    {
        refusal = Check(mon, out var moves, out var item, out var abilityBit, out var ball, adjustments);
        if (refusal is not null) return null;
        var pk3 = new PK3
        {
            Species = (ushort)mon.National,
            PID = mon.Pid,
            HeldItem = item,
            Move1 = moves[0], Move2 = moves[1], Move3 = moves[2], Move4 = moves[3],
            Ball = (byte)ball,
        };
        pk3.AbilityBit = abilityBit;
        return pk3;
    }

    private static string? Check(in Outbound mon, out ushort[] moves, out ushort item, out bool abilityBit, out int ball,
        List<string>? adjustments)
    {
        moves = new ushort[4];
        item = 0;
        abilityBit = false;
        ball = mon.Ball;
        var strings = GameInfo.GetStrings("en");
        var name = mon.SpeciesName;
        if (mon.National is <= 0 or > MaxSpecies)
            return $"{name} does not exist in Generation 3.";
        if (!mon.BaseSpecies)
            return $"{name} is a form Generation 3 does not have.";

        for (var i = 0; i < mon.Moves.Count && i < 4; i++)
        {
            var (stored, national, moveName) = mon.Moves[i];
            if (stored == 0) continue;
            if (national is <= 0 or > MaxMove)
                return $"{name} knows {moveName}, a move Generation 3 does not have.";
            moves[i] = (ushort)national;
        }

        if (mon.HeldItem != 0)
        {
            item = mon.NationalItem > 0 ? ItemConverter.GetItemOld3((ushort)mon.NationalItem) : (ushort)0;
            if (item == 0)
                return $"{name} holds {mon.ItemName}, an item Generation 3 does not have.";
        }

        var abilityName = (uint)mon.NationalAbility < strings.abilitylist.Length ? strings.abilitylist[mon.NationalAbility] : $"#{mon.NationalAbility}";
        var personal = PersonalTable.E[mon.National];
        var first = !mon.HiddenAbility && mon.NationalAbility != 0 && mon.NationalAbility == personal.Ability1;
        var second = !mon.HiddenAbility && mon.NationalAbility != 0 && mon.NationalAbility == personal.Ability2;
        if (first || second)
        {
            // Both slots can name the same ability: the PID's slot is the one the game would pick.
            abilityBit = second && (!first || (mon.Pid & 1) == 1);
        }
        else
        {
            // A hidden ability, or one the species has only in later games: it takes the
            // ability its PID picks in Generation 3, as a caught one would.
            abilityBit = personal.Ability2 != 0 && personal.Ability2 != personal.Ability1 && (mon.Pid & 1) == 1;
            var landed = strings.abilitylist[abilityBit ? personal.Ability2 : personal.Ability1];
            adjustments?.Add(mon.HiddenAbility
                ? $"Its hidden ability {abilityName} becomes {landed}: Generation 3 has no hidden abilities."
                : $"Its ability {abilityName} becomes {landed}: {strings.specieslist[mon.National]} cannot have it in Generation 3.");
        }

        if (mon.Ball is <= 0 or > MaxBall)
        {
            ball = (int)Ball.Poke;
            adjustments?.Add($"Its {(mon.Ball > 0 && mon.Ball < strings.balllist.Length ? strings.balllist[mon.Ball] : "ball")} becomes a Poké Ball: Generation 3 does not have it.");
        }
        return null;
    }

    /// <summary>
    /// What a CFRU game reads an incoming Pokémon from: any Generation 3+ entity as it is (all
    /// of them store national species, move and ability ids), a Generation 1 or 2 one through
    /// its Virtual Console transfer. Converting to a PK3 first refused every Generation 4+
    /// Pokémon, since PKHeX never converts down. Null when there is no such reading.
    /// </summary>
    public static PKM? Source(PKM entity) =>
        entity.Format >= 3 ? entity : EntityConverter.ConvertToType(entity, typeof(PK7), out _);

    /// <summary>
    /// The hack ids <paramref name="pkm"/> (a <see cref="Source"/> reading) lands with in
    /// <paramref name="game"/>, or null with the reason one of its species, form, moves, held
    /// item, ability or ball has no counterpart there.
    /// </summary>
    /// <param name="abilities">The national ability ids a hack species' two slots and hidden slot hold.</param>
    /// <param name="storeBall">The hack's ball id for a PKHeX ball, -1 when it has none.</param>
    /// <param name="adjustments">Receives what changed to fit the hack (an ability it lacks).</param>
    public static Inbound? FromPkm(PKM pkm, string game,
        Func<int, int> speciesFromNational, Func<int, int> moveFromNational, Func<int, int> itemFromNational,
        Func<int, (int A1, int A2, int Hidden)> abilities, Func<int, int> storeBall, out string? refusal,
        List<string>? adjustments = null)
    {
        var strings = GameInfo.GetStrings("en");
        var name = pkm.Species < strings.specieslist.Length ? strings.specieslist[pkm.Species] : $"#{pkm.Species}";
        refusal = null;

        var species = speciesFromNational(pkm.Species);
        if (species <= 0)
        {
            refusal = $"{name} does not exist in {game}.";
            return null;
        }
        // A CFRU record stores no form (Unown's letter comes from its PID): a later form would
        // land as the base species, so it is refused rather than silently changed.
        if (pkm.Format > 3 && pkm.Form != 0 && pkm.Species != (ushort)Species.Unown)
        {
            refusal = $"{name} is in a form {game} cannot store.";
            return null;
        }

        var stored = new[] { pkm.Move1, pkm.Move2, pkm.Move3, pkm.Move4 };
        var moves = new int[4];
        for (var i = 0; i < 4; i++)
        {
            if (stored[i] == 0) continue;
            moves[i] = moveFromNational(stored[i]);
            if (moves[i] <= 0)
            {
                refusal = $"{name} knows {strings.movelist[stored[i]]}, a move {game} does not have.";
                return null;
            }
        }

        var item = 0;
        if (pkm.HeldItem != 0)
        {
            // Generation 3 numbers its items apart; later formats already store the modern id.
            var gen3 = pkm.Format == 3;
            var national = gen3 ? ItemConverter.GetItemFuture3((ushort)pkm.HeldItem) : (ushort)pkm.HeldItem;
            var known = (!gen3 || ItemConverter.IsItemTransferable34(national)) && national < strings.itemlist.Length;
            item = known ? itemFromNational(national) : 0;
            if (item <= 0)
            {
                refusal = $"{name} holds {(known ? strings.itemlist[national] : "an item")}, which {game} does not have.";
                return null;
            }
        }

        // A CFRU mon stores no ability id: the PID's parity picks slot one or two, a flag the
        // hidden slot. An ability the hack puts in the other slot is kept by asking the caller
        // for a PID of that parity: hacks reorder slots, and Generation 5+ PIDs pick nothing.
        var (a1, a2, hidden) = abilities(species);
        var ability = pkm.Ability;
        if (ability == 0 || (ability != a1 && ability != a2 && ability != hidden))
        {
            // The hack gave the species other abilities (Radical Red uses the modern ones, so a
            // FireRed Gengar's Levitate is gone): it takes the hack's ability in the same slot,
            // as a Pokémon leaving the hack does the other way, and the change is reported.
            var index = pkm.PersonalInfo.GetIndexOfAbility(ability);
            var landed = index == 2 && hidden != 0 ? hidden : index == 1 && a2 != 0 ? a2 : a1;
            if (landed == 0)
            {
                refusal = $"{name} has no ability in {game}'s data.";
                return null;
            }
            adjustments?.Add($"Its ability {AbilityName(strings, ability)} becomes {AbilityName(strings, landed)}: {name} cannot have it in {game}.");
            ability = landed;
        }
        var picked = (pkm.PID & 1) == 1 && a2 != 0 ? a2 : a1;
        var hiddenAbility = false;
        int? abilitySlot = null;
        if (ability != picked)
        {
            if (ability == a1) abilitySlot = 0;
            else if (ability == a2) abilitySlot = 1;
            else hiddenAbility = true;
        }

        var ball = storeBall(pkm.Ball);
        if (ball < 0)
        {
            refusal = $"{name}'s {(pkm.Ball < strings.balllist.Length ? strings.balllist[pkm.Ball] : "ball")} does not exist in {game}.";
            return null;
        }
        return new Inbound(species, moves, item, ball, hiddenAbility, abilitySlot);
    }

    private static string AbilityName(GameStrings strings, int ability) =>
        (uint)ability < strings.abilitylist.Length ? strings.abilitylist[ability] : $"#{ability}";
}
