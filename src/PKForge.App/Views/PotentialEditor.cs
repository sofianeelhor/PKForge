using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// The potential block - Tera type, Hyper Training, and ability slot (capsule / patch
/// semantics). Every surface is gen-gated by what the mon's format can carry, so older
/// generations only see the options that apply. Edits apply to the session in memory;
/// the caller persists. Reusable by any editing session (bank or in-save).
/// </summary>
public static class PotentialEditor
{
    private static readonly string[] StatNames = ["HP", "Atk", "Def", "SpA", "SpD", "Spe"];

    public static async Task<bool> ShowAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            PotentialInfo p;
            try { p = session.GetPotential(box, slot); }
            catch (Exception error) { await EditorMenu.ShowAsync(host, "POTENTIAL", error.Message, "OK"); return dirty; }

            var options = new List<PadOption>();
            if (p.SupportsTera)
                options.Add(new PadOption($"Tera type · {p.TeraTypeName}{(p.TeraLocked ? " (fixed)" : "")}"));
            if (p.SupportsHyperTrain)
                options.Add(new PadOption($"Hyper Training · {p.HyperTrained.Count(t => t)}/6"));
            if (p.SupportsAbilitySlot)
                options.Add(new PadOption($"Ability slot · {p.AbilitySlots[p.AbilitySlot].Name}"));

            if (options.Count == 0)
            {
                await EditorMenu.ShowAsync(host, "POTENTIAL",
                    "This format has no Tera type, Hyper Training, or ability slot data.", "OK");
                return dirty;
            }

            var choice = await EditorMenu.ShowAsync(host, "POTENTIAL", null, options.ToArray());
            if (choice is null) return dirty;

            if (choice.StartsWith("Tera type", StringComparison.Ordinal))
            {
                if (p.TeraLocked)
                {
                    await EditorMenu.ShowAsync(host, "TERA TYPE",
                        "This Pokémon's Tera Type is fixed by its form and cannot be changed.", "OK");
                    continue;
                }
                var pick = await PickChoiceAsync(host, "TERA TYPE", session.GetTeraTypeChoices(), p.TeraType);
                if (pick is { } v) { session.ApplyPotentialEdit(box, slot, new PotentialEdit(TeraType: v)); dirty = true; }
            }
            else if (choice.StartsWith("Hyper Training", StringComparison.Ordinal))
            {
                if (await EditHyperTrainingAsync(host, session, box, slot)) dirty = true;
            }
            else if (choice.StartsWith("Ability slot", StringComparison.Ordinal))
            {
                var pick = await PickChoiceAsync(host, "ABILITY SLOT (CAPSULE / PATCH)", p.AbilitySlots, p.AbilitySlot);
                if (pick is { } v) { session.ApplyPotentialEdit(box, slot, new PotentialEdit(AbilitySlot: v)); dirty = true; }
            }
        }
    }

    /// <summary>Per-stat Hyper Training toggles, plus train-all / clear-all.</summary>
    private static async Task<bool> EditHyperTrainingAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            var p = session.GetPotential(box, slot);
            var options = new List<PadOption>();
            for (var i = 0; i < StatNames.Length; i++)
            {
                var trained = p.HyperTrained[i];
                options.Add(new PadOption($"{StatNames[i]} · {(trained ? "trained" : "-")}"));
            }
            options.Add(new PadOption("Train all", IconPath: "box"));
            options.Add(new PadOption("Clear all", IconPath: "hex"));

            var choice = await EditorMenu.ShowAsync(host, "HYPER TRAINING", null, options.ToArray());
            if (choice is null) return dirty;

            if (choice.StartsWith("Train all", StringComparison.Ordinal))
            {
                session.ApplyPotentialEdit(box, slot, new PotentialEdit(HyperTrained: [true, true, true, true, true, true]));
                dirty = true;
            }
            else if (choice.StartsWith("Clear all", StringComparison.Ordinal))
            {
                session.ApplyPotentialEdit(box, slot, new PotentialEdit(HyperTrained: [false, false, false, false, false, false]));
                dirty = true;
            }
            else
            {
                for (var i = 0; i < StatNames.Length; i++)
                {
                    if (!choice.StartsWith(StatNames[i], StringComparison.Ordinal)) continue;
                    var next = p.HyperTrained.ToArray();
                    next[i] = !next[i];
                    session.ApplyPotentialEdit(box, slot, new PotentialEdit(HyperTrained: next));
                    dirty = true;
                    break;
                }
            }
        }
    }

    private static async Task<int?> PickChoiceAsync(Grid host, string title, IReadOnlyList<NamedChoice> choices, int current)
    {
        var items = choices.Select(c => new PickItem(c.Id, c.Name)).ToList();
        var picked = await PickerMenu.ShowAsync(host, title, items, current);
        return picked?.Id;
    }
}
