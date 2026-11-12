namespace PKForge.Domain;

/// <summary>Who a park resident is, whatever slot or visit it came from: the same identity facts
/// the clone audit groups by (EC + PID, or PID + trainer on older formats). The park keeps one of
/// each and remembers its friendships by it, so a Pokémon that leaves and comes back keeps them.</summary>
public static class ParkIdentity
{
    /// <summary>The identity of a Pokémon from its own data; it survives moves, edits and evolution.</summary>
    public static string Soul(uint pid, uint? encryptionConstant, string trainer, int tid) =>
        "soul:" + CollectionAudit.CloneKey(new MonFingerprint(pid, encryptionConstant, trainer ?? "", tid, "", ""));

    /// <summary>Rewrites stored relationships onto identities (older ones were kept by visit id),
    /// merging any pair that now meets twice: the one with the most shared moments wins.</summary>
    public static ParkRelationship[] Rekey(IEnumerable<ParkRelationship> relationships, Func<string, string> soulOf) =>
        relationships.Select(r => r with { FirstId = soulOf(r.FirstId), SecondId = soulOf(r.SecondId) })
            .Where(r => r.FirstId != r.SecondId)
            .GroupBy(r => r.Key)
            .Select(g => g.OrderByDescending(r => r.SharedMoments).ThenByDescending(r => r.LastEventAt).First())
            .ToArray();

    /// <summary>How every pair of today's residents gets on, keyed by their ids in the park
    /// (<see cref="ParkRelationship.KeyFor"/>), from relationships kept by identity.</summary>
    public static IReadOnlyDictionary<string, (int Friendship, int Rivalry)> Relations(
        IEnumerable<ParkRelationship> relationships, IReadOnlyDictionary<string, string> soulById)
    {
        var stored = relationships.GroupBy(r => r.Key).ToDictionary(g => g.Key, g => g.Last());
        var ids = soulById.Keys.ToArray();
        var result = new Dictionary<string, (int, int)>();
        for (var i = 0; i < ids.Length; i++)
            for (var j = i + 1; j < ids.Length; j++)
                if (stored.TryGetValue(ParkRelationship.KeyFor(soulById[ids[i]], soulById[ids[j]]), out var r))
                    result[ParkRelationship.KeyFor(ids[i], ids[j])] = (r.Friendship, r.Rivalry);
        return result;
    }
}
