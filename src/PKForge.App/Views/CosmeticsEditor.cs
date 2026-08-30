using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// PKHeX-style cosmetic data that belongs to an individual Pokémon rather than its
/// battle build. The engine gates every group by the entity format, so a control is
/// never shown merely because the currently-open save is from a newer generation.
/// </summary>
public static class CosmeticsEditor
{
    private static readonly string[] ContestNames = ["Cool", "Beauty", "Cute", "Smart", "Tough", "Sheen"];

    public static async Task<bool> ShowAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            var c = session.GetCosmetics(box, slot);
            var options = new List<PadOption>();
            if (c.Markings.Count != 0) options.Add(new PadOption($"Box markings · {c.Markings.Count(m => m.Value != 0)}/{c.Markings.Count}"));
            if (c.ContestStats.Count != 0) options.Add(new PadOption($"Contest stats · {c.ContestStats.Sum()}/1530"));
            if (c.SupportsSize || c.SupportsScale) options.Add(new PadOption("Size"));
            if (c.SupportsAffection || c.SupportsFullnessEnjoyment) options.Add(new PadOption("Care"));
            if (HasSpecial(c)) options.Add(new PadOption("Special"));

            if (options.Count == 0)
            {
                await EditorMenu.ShowAsync(host, "COSMETICS", "This Pokémon format has no cosmetic data.", "OK");
                return dirty;
            }

            var choice = await EditorMenu.ShowAsync(host, "COSMETICS", null, options.ToArray());
            if (choice is null) return dirty;
            if (choice.StartsWith("Box markings", StringComparison.Ordinal)) dirty |= await EditMarkingsAsync(host, session, box, slot);
            else if (choice.StartsWith("Contest stats", StringComparison.Ordinal)) dirty |= await EditContestAsync(host, session, box, slot);
            else if (choice == "Size") dirty |= await EditSizeAsync(host, session, box, slot);
            else if (choice == "Care") dirty |= await EditCareAsync(host, session, box, slot);
            else if (choice == "Special") dirty |= await EditSpecialAsync(host, session, box, slot);
        }
    }

    private static bool HasSpecial(CosmeticInfo c) => c.SupportsFavorite || c.SupportsDynamax || c.SupportsAlpha || c.SupportsSociability;

    private static async Task<bool> EditMarkingsAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            var c = session.GetCosmetics(box, slot);
            var items = c.Markings.Select((m, i) => new PickItem(i, $"{m.Name} · {MarkingValueName(m.Value, m.MaxValue)}")).ToList();
            var picked = await PickerMenu.ShowAsync(host, "BOX MARKINGS", items);
            if (picked is null) return dirty;

            var marking = c.Markings[picked.Id];
            var next = await PickMarkingValueAsync(host, marking);
            if (next is not { } value || value == marking.Value) continue;
            var values = c.Markings.Select(m => m.Value).ToArray();
            values[picked.Id] = value;
            session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(Markings: values));
            dirty = true;
        }
    }

    private static async Task<int?> PickMarkingValueAsync(Grid host, CosmeticMarking marking)
    {
        if (marking.MaxValue == 1)
        {
            var choice = await EditorMenu.ShowAsync(host, marking.Name, null,
                new PadOption("Off"), new PadOption("On"));
            return choice switch { "Off" => 0, "On" => 1, _ => null };
        }
        var color = await EditorMenu.ShowAsync(host, marking.Name, null,
            new PadOption("Off"), new PadOption("Blue"), new PadOption("Pink"));
        return color switch { "Off" => 0, "Blue" => 1, "Pink" => 2, _ => null };
    }

    private static async Task<bool> EditContestAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            var c = session.GetCosmetics(box, slot);
            var options = ContestNames.Select((name, i) => new PadOption($"{name} · {c.ContestStats[i]}/255")).ToList();
            options.Add(new PadOption("Max all"));
            options.Add(new PadOption("Clear all"));
            var choice = await EditorMenu.ShowAsync(host, "CONTEST STATS", null, options.ToArray());
            if (choice is null) return dirty;

            var values = c.ContestStats.ToArray();
            if (choice == "Max all") values = [255, 255, 255, 255, 255, 255];
            else if (choice == "Clear all") values = [0, 0, 0, 0, 0, 0];
            else
            {
                var index = Array.FindIndex(ContestNames, name => choice.StartsWith(name, StringComparison.Ordinal));
                if (index < 0) continue;
                var value = await StatsPopup.ShowSingleAsync(host, ContestNames[index], values[index], 255);
                if (value is not { } next || next == values[index]) continue;
                values[index] = next;
            }
            if (values.SequenceEqual(c.ContestStats)) continue;
            session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(ContestStats: values));
            dirty = true;
        }
    }

    private static async Task<bool> EditSizeAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var c = session.GetCosmetics(box, slot);
        var options = new List<PadOption>();
        if (c.SupportsSize)
        {
            options.Add(new PadOption($"Height scalar · {c.HeightScalar}/255"));
            options.Add(new PadOption($"Weight scalar · {c.WeightScalar}/255"));
        }
        if (c.SupportsScale) options.Add(new PadOption($"Scale · {c.Scale}/255"));
        var choice = await EditorMenu.ShowAsync(host, "SIZE", null, options.ToArray());
        if (choice is null) return false;

        var current = choice.StartsWith("Height", StringComparison.Ordinal) ? c.HeightScalar
            : choice.StartsWith("Weight", StringComparison.Ordinal) ? c.WeightScalar : c.Scale;
        var value = await StatsPopup.ShowSingleAsync(host, choice.Split('·')[0].Trim(), current, 255);
        if (value is not { } next || next == current) return false;
        var edit = choice.StartsWith("Height", StringComparison.Ordinal) ? new CosmeticEdit(HeightScalar: next)
            : choice.StartsWith("Weight", StringComparison.Ordinal) ? new CosmeticEdit(WeightScalar: next)
            : new CosmeticEdit(Scale: next);
        session.ApplyCosmeticEdit(box, slot, edit);
        return true;
    }

    private static async Task<bool> EditCareAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var c = session.GetCosmetics(box, slot);
        var options = new List<PadOption>();
        if (c.SupportsAffection)
        {
            options.Add(new PadOption($"OT affection · {c.OriginalTrainerAffection}/255"));
            options.Add(new PadOption($"Handler affection · {c.HandlingTrainerAffection}/255"));
        }
        if (c.SupportsFullnessEnjoyment)
        {
            options.Add(new PadOption($"Fullness · {c.Fullness}/255"));
            options.Add(new PadOption($"Enjoyment · {c.Enjoyment}/255"));
        }
        var choice = await EditorMenu.ShowAsync(host, "CARE", null, options.ToArray());
        if (choice is null) return false;
        var current = choice.StartsWith("OT", StringComparison.Ordinal) ? c.OriginalTrainerAffection
            : choice.StartsWith("Handler", StringComparison.Ordinal) ? c.HandlingTrainerAffection
            : choice.StartsWith("Fullness", StringComparison.Ordinal) ? c.Fullness : c.Enjoyment;
        var value = await StatsPopup.ShowSingleAsync(host, choice.Split('·')[0].Trim(), current, 255);
        if (value is not { } next || next == current) return false;
        var edit = choice.StartsWith("OT", StringComparison.Ordinal) ? new CosmeticEdit(OriginalTrainerAffection: next)
            : choice.StartsWith("Handler", StringComparison.Ordinal) ? new CosmeticEdit(HandlingTrainerAffection: next)
            : choice.StartsWith("Fullness", StringComparison.Ordinal) ? new CosmeticEdit(Fullness: next)
            : new CosmeticEdit(Enjoyment: next);
        session.ApplyCosmeticEdit(box, slot, edit);
        return true;
    }

    private static async Task<bool> EditSpecialAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var c = session.GetCosmetics(box, slot);
        var options = new List<PadOption>();
        if (c.SupportsFavorite) options.Add(new PadOption($"Favorite · {(c.IsFavorite ? "on" : "off")}"));
        if (c.SupportsDynamax)
        {
            options.Add(new PadOption($"Dynamax level · {c.DynamaxLevel}/10"));
            options.Add(new PadOption($"Gigantamax factor · {(c.CanGigantamax ? "on" : "off")}"));
        }
        if (c.SupportsAlpha) options.Add(new PadOption($"Alpha · {(c.IsAlpha ? "on" : "off")}"));
        if (c.SupportsSociability) options.Add(new PadOption($"Sociability · {c.Sociability}"));
        var choice = await EditorMenu.ShowAsync(host, "SPECIAL", null, options.ToArray());
        if (choice is null) return false;

        if (choice.StartsWith("Dynamax level", StringComparison.Ordinal))
        {
            var value = await StatsPopup.ShowSingleAsync(host, "DYNAMAX LEVEL", c.DynamaxLevel, 10);
            if (value is not { } next || next == c.DynamaxLevel) return false;
            session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(DynamaxLevel: next));
            return true;
        }
        if (choice.StartsWith("Sociability", StringComparison.Ordinal))
        {
            var value = await StatsPopup.ShowSingleAsync(host, "SOCIABILITY", (int)Math.Min(c.Sociability, int.MaxValue), int.MaxValue);
            if (value is not { } next || (uint)next == c.Sociability) return false;
            session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(Sociability: (uint)next));
            return true;
        }
        if (choice.StartsWith("Favorite", StringComparison.Ordinal)) session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(IsFavorite: !c.IsFavorite));
        else if (choice.StartsWith("Gigantamax", StringComparison.Ordinal)) session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(CanGigantamax: !c.CanGigantamax));
        else if (choice.StartsWith("Alpha", StringComparison.Ordinal)) session.ApplyCosmeticEdit(box, slot, new CosmeticEdit(IsAlpha: !c.IsAlpha));
        else return false;
        return true;
    }

    private static string MarkingValueName(int value, int max) => max == 1
        ? (value == 0 ? "off" : "on")
        : value switch { 1 => "blue", 2 => "pink", _ => "off" };
}
