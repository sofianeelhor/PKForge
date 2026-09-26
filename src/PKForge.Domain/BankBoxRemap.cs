namespace PKForge.Domain;

/// <summary>
/// A rearrangement of the Bank's boxes: where every current box ends up. One remap is applied
/// in one index write, and everything keyed by box index (names, wallpapers, the living dex
/// region, marks, the cursor) follows it through <see cref="Map"/>.
/// </summary>
/// <param name="NewIndexOf">For each current box, its index afterwards, or -1 when it is deleted.</param>
/// <param name="NewCount">The box count afterwards.</param>
public sealed record BankBoxRemap(IReadOnlyList<int> NewIndexOf, int NewCount)
{
    /// <summary>Where box <paramref name="box"/> ends up; null when it is deleted or out of range.</summary>
    public int? Map(int box) => (uint)box < (uint)NewIndexOf.Count && NewIndexOf[box] >= 0 ? NewIndexOf[box] : null;

    /// <summary>True when nothing moves (a no-op swap or move).</summary>
    public bool IsIdentity => NewCount == NewIndexOf.Count && NewIndexOf.Select((to, from) => to == from).All(same => same);

    /// <summary>Two boxes trade places.</summary>
    public static BankBoxRemap Swap(int count, int a, int b)
    {
        Check(count, a);
        Check(count, b);
        var map = Enumerable.Range(0, count).ToArray();
        (map[a], map[b]) = (b, a);
        return new BankBoxRemap(map, count);
    }

    /// <summary>A box is taken out and put back at <paramref name="to"/>; the boxes between slide over one.</summary>
    public static BankBoxRemap Move(int count, int from, int to)
    {
        Check(count, from);
        Check(count, to);
        var map = new int[count];
        for (var box = 0; box < count; box++)
        {
            map[box] = box == from ? to
                : from < to && box > from && box <= to ? box - 1
                : to < from && box >= to && box < from ? box + 1
                : box;
        }
        return new BankBoxRemap(map, count);
    }

    /// <summary>A new empty box appears at <paramref name="at"/> (up to <paramref name="count"/>, the end).</summary>
    public static BankBoxRemap Insert(int count, int at)
    {
        if ((uint)at > (uint)count) throw new ArgumentOutOfRangeException(nameof(at));
        return new BankBoxRemap([.. Enumerable.Range(0, count).Select(box => box >= at ? box + 1 : box)], count + 1);
    }

    /// <summary>Box <paramref name="at"/> is deleted; the boxes after it move down one.</summary>
    public static BankBoxRemap Remove(int count, int at)
    {
        Check(count, at);
        if (count <= 1) throw new InvalidOperationException("The bank keeps at least one box.");
        return new BankBoxRemap([.. Enumerable.Range(0, count).Select(box => box == at ? -1 : box > at ? box - 1 : box)], count - 1);
    }

    /// <summary>
    /// Throws unless this is a valid remap of <paramref name="count"/> boxes: every surviving
    /// box lands on a distinct index inside the new count.
    /// </summary>
    public void Validate(int count)
    {
        if (NewIndexOf.Count != count) throw new ArgumentException($"The remap covers {NewIndexOf.Count} boxes, the bank has {count}.");
        if (NewCount < 1) throw new ArgumentException("The bank keeps at least one box.");
        var taken = new HashSet<int>();
        foreach (var to in NewIndexOf)
        {
            if (to < 0) continue;
            if (to >= NewCount || !taken.Add(to)) throw new ArgumentException("The remap sends two boxes to the same place or outside the bank.");
        }
    }

    private static void Check(int count, int box)
    {
        if ((uint)box >= (uint)count) throw new ArgumentOutOfRangeException(nameof(box));
    }
}
