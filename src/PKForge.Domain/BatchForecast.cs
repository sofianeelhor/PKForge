using System.Globalization;

namespace PKForge.Domain;

/// <summary>One field a batch plan touches on one Pokémon: what it reads now and what it would read.
/// <paramref name="Known"/> false when the plan line is not one the forecast understands (an expert
/// line): its outcome is only known once the engine runs it.</summary>
public sealed record BatchFieldChange(string Field, string Before, string After, bool Known = true)
{
    public bool Changes => !Known || Before != After;
}

/// <summary>
/// What a batch plan (PKHeX "Prop=Value" lines, as the batch editor and the presets write them)
/// would do to one Pokémon, worked out from its decoded fields without touching the save, so the
/// screens can show before → after live while the plan is built. The engine's dry run stays the
/// final word (legality, lines this does not know) before anything is written.
/// </summary>
public static class BatchForecast
{
    private static readonly string[] Stats = ["HP", "ATK", "DEF", "SPA", "SPD", "SPE"];

    /// <summary>The fields <paramref name="plan"/> touches on <paramref name="mon"/>, one line per
    /// field (the six IVs are one line, the six EVs another), in the plan's order.</summary>
    public static IReadOnlyList<BatchFieldChange> Of(EntityDetail mon, IEnumerable<string> plan, Func<int, string>? itemName = null)
    {
        ArgumentNullException.ThrowIfNull(mon);
        ArgumentNullException.ThrowIfNull(plan);
        var lines = new List<BatchFieldChange>();
        int[] ivs = [.. Six(mon.IVs)];
        int[] evs = [.. Six(mon.EVs)];
        var ivAt = -1;
        var evAt = -1;
        var ppAt = -1;
        foreach (var raw in plan)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var eq = line.IndexOf('=');
            var key = (eq < 0 ? line : line[..eq]).Trim().TrimStart('.');
            var value = eq < 0 ? "" : line[(eq + 1)..].Trim();
            int? number = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

            if (key.StartsWith("IV_", StringComparison.OrdinalIgnoreCase) && Stat(key[3..]) is { } iv && number is { } ivValue)
            {
                ivs[iv] = ivValue;
                if (ivAt < 0) { ivAt = lines.Count; lines.Add(null!); }
            }
            else if (key.StartsWith("EV_", StringComparison.OrdinalIgnoreCase) && Stat(key[3..]) is { } ev && number is { } evValue)
            {
                evs[ev] = evValue;
                if (evAt < 0) { evAt = lines.Count; lines.Add(null!); }
            }
            else if (key.Equals("Level", StringComparison.OrdinalIgnoreCase) && number is { } level)
                lines.Add(new("Level", Lv(mon.Level), Lv(Math.Clamp(level, 1, 100))));
            else if (key.Equals("Friendship", StringComparison.OrdinalIgnoreCase) && number is { } friendship)
                lines.Add(new("Friendship", Num(mon.Friendship), Num(Math.Clamp(friendship, 0, 255))));
            else if (key.Equals("HeldItem", StringComparison.OrdinalIgnoreCase) && number is { } item)
                lines.Add(new("Held item", ItemText(mon.HeldItem, itemName), ItemText(item, itemName)));
            else if (key.Equals("IsNicknamed", StringComparison.OrdinalIgnoreCase) && value.Equals("false", StringComparison.OrdinalIgnoreCase))
                lines.Add(new("Name", mon.Nickname, mon.SpeciesName));
            else if (key.Equals("Heal", StringComparison.OrdinalIgnoreCase))
            {
                var max = mon.Stats is { Count: > 0 } stats ? stats[0] : 0;
                lines.Add(max > 0
                    ? new("HP", $"{mon.CurrentHp}/{max}", $"{max}/{max}")
                    : new("HP", "", "healed", Known: false));
                if (mon.StatusCondition != 0) lines.Add(new("Status", "afflicted", "healthy"));
            }
            else if (key.StartsWith("Move", StringComparison.OrdinalIgnoreCase) && key.EndsWith("_PPUps", StringComparison.OrdinalIgnoreCase))
            {
                if (ppAt < 0) { ppAt = lines.Count; lines.Add(new("PP Ups", "", "max", Known: false)); }
            }
            else if (key.StartsWith("HyperTrain", StringComparison.OrdinalIgnoreCase))
                lines.Add(new("Hyper Training", "", "where legal", Known: false));
            else
                lines.Add(new(key, "", value.Length > 0 ? value : "on", Known: false));
        }
        if (ivAt >= 0) lines[ivAt] = new("IVs", Spread(Six(mon.IVs)), Spread(ivs));
        if (evAt >= 0) lines[evAt] = new("EVs", Spread(Six(mon.EVs)), Spread(evs));
        return lines;
    }

    /// <summary>Whether <paramref name="plan"/> would change anything on <paramref name="mon"/>.</summary>
    public static bool Changes(EntityDetail mon, IEnumerable<string> plan) => Of(mon, plan).Any(line => line.Changes);

    private static int? Stat(string name)
    {
        var i = Array.FindIndex(Stats, s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? null : i;
    }

    private static IEnumerable<int> Six(IReadOnlyList<int>? values)
    {
        for (var i = 0; i < 6; i++) yield return values is not null && i < values.Count ? values[i] : 0;
    }

    private static string Spread(IEnumerable<int> values) => string.Join("/", values.Select(Num));
    private static string Lv(int level) => "Lv. " + Num(level);
    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string ItemText(int item, Func<int, string>? name) =>
        item == 0 ? "none" : name?.Invoke(item) is { Length: > 0 } text ? text : "item " + Num(item);
}
