using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// From Gen 5 on a Pokémon stores which ability slot it has (1, 2, or 4 for the hidden one)
/// next to the ability id, and the games re-read the ability from that slot whenever the
/// species changes. Writing only the id left the slot behind: a Pokémon given its hidden
/// ability still said "slot 1", and evolving it handed it the first regular ability
/// (issue #72). The slot is written together with the id.
/// </summary>
internal static class AbilitySlotSync
{
    public static void Apply(PKM entity, IPersonalAbility personal, int ability)
    {
        var slot = FindSlot(entity, personal, ability);
        if (slot < 0)
        {
            // HaX: an ability off this species' table has no slot to point at; the id is
            // written raw and the slot stays as it was.
            entity.Ability = ability;
            return;
        }

        entity.RefreshAbility(slot);
        // Gen 5 has no slot field for the two regular abilities: PID bit 16 is the slot.
        // Rewrite the PID (keeping nature, gender and shininess) so the two agree.
        if (entity is PK5 && entity.AbilityNumber != 1 << slot)
            SaveEngineSession.TrySetPidDerived(entity, null, null, 1 << slot);
        entity.Ability = ability;
    }

    /// <summary>True when <paramref name="ability"/> sits on this species' table but the stored
    /// slot does not point at it, so the game would hand out another ability on evolution.</summary>
    public static bool IsStale(PKM entity, IPersonalAbility personal, int ability)
    {
        var slot = FindSlot(entity, personal, ability);
        return slot >= 0 && entity.AbilityNumber != 1 << slot;
    }

    /// <summary>The slot holding <paramref name="ability"/>. When two slots share it (many
    /// species repeat an ability) the current slot is kept so nothing moves needlessly.</summary>
    private static int FindSlot(PKM entity, IPersonalAbility personal, int ability)
    {
        var current = entity.AbilityNumber switch { 4 => 2, 2 => 1, _ => 0 };
        if (current < personal.AbilityCount && personal.GetAbilityAtIndex(current) == ability)
            return current;
        for (var i = 0; i < personal.AbilityCount; i++)
        {
            if (personal.GetAbilityAtIndex(i) == ability)
                return i;
        }
        return -1;
    }
}
