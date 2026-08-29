using PKForge.App.Theme;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// Cross-format Pokérus and ribbon editor. The engine reports exactly what the current
/// Pokémon structure can store; this surface never guesses from the save generation.
/// </summary>
public static class AwardsEditor
{
    public static async Task<bool> ShowAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            var pokerus = session.GetPokerus(box, slot);
            var ribbons = session.GetRibbons(box, slot);
            var selected = ribbons.Count(r => r.Value != 0);
            var options = new List<PadOption>();
            if (pokerus.Supported || pokerus.Status != PokerusStatus.Susceptible)
                options.Add(new PadOption($"Pokérus · {PokerusLabel(pokerus)}", IconPath: PokerusIcon(pokerus.Status)));
            if (ribbons.Count != 0)
                options.Add(new PadOption($"Ribbons · {selected}/{ribbons.Count}", IconPath: "ribbons"));

            if (options.Count == 0)
            {
                await EditorMenu.ShowAsync(host, "AWARDS", "This Pokémon format has no Pokérus or ribbon data.", "OK");
                return dirty;
            }

            var choice = await EditorMenu.ShowAsync(host, "AWARDS", null, options.ToArray());
            if (choice is null) return dirty;
            if (choice.StartsWith("Pokérus", StringComparison.Ordinal))
            {
                if (await EditPokerusAsync(host, session, box, slot)) dirty = true;
            }
            else if (choice.StartsWith("Ribbons", StringComparison.Ordinal))
            {
                if (await EditRibbonsAsync(host, session, box, slot)) dirty = true;
            }
        }
    }

    private static async Task<bool> EditPokerusAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var current = session.GetPokerus(box, slot);
        var choice = await EditorMenu.ShowAsync(host, "POKéRUS",
            "Infectious spreads Pokérus. Cured keeps the immunity marker but no longer spreads it.",
            new PadOption("Infect", IconPath: "pokerus-infected", Accent: UiTokens.GiftRed),
            new PadOption("Cure", IconPath: "pokerus-cured", Accent: UiTokens.Green),
            new PadOption("Clear / susceptible", Glyph: "○", Accent: UiTokens.Ink1));
        if (choice is null) return false;

        var status = choice switch
        {
            "Infect" => PokerusStatus.Infectious,
            "Cure" => PokerusStatus.Cured,
            _ => PokerusStatus.Susceptible,
        };
        if (status == current.Status) return false;
        session.SetPokerus(box, slot, status);
        return true;
    }

    private static async Task<bool> EditRibbonsAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        var dirty = false;
        while (true)
        {
            var ribbons = session.GetRibbons(box, slot);
            var items = ribbons.Select((r, i) => new PickItem(i,
                r.MaxValue == 1
                    ? $"{(r.Value != 0 ? "✓" : "□")} {r.Name}{(r.IsMark ? " · mark" : "")}"
                    : $"{r.Name} · {r.Value}/{r.MaxValue}",
                $"ribbons/{r.Id.ToLowerInvariant()}.png"))
                .ToList();
            var picked = await PickerMenu.ShowAsync(host, "RIBBONS & MARKS", items);
            if (picked is null) return dirty;

            var ribbon = ribbons[picked.Id];
            if (ribbon.MaxValue == 1)
            {
                session.SetRibbon(box, slot, ribbon.Id, ribbon.Value == 0 ? 1 : 0);
                dirty = true;
                continue;
            }

            var count = await StatsPopup.ShowSingleAsync(host, ribbon.Name, ribbon.Value, ribbon.MaxValue);
            if (count is not { } value || value == ribbon.Value) continue;
            session.SetRibbon(box, slot, ribbon.Id, value);
            dirty = true;
        }
    }

    private static string PokerusLabel(PokerusInfo info) => info.Status switch
    {
        PokerusStatus.Infectious => $"infectious · {info.Days} day{(info.Days == 1 ? "" : "s")}",
        PokerusStatus.Cured => "cured",
        _ => "susceptible",
    };

    private static string PokerusIcon(PokerusStatus status) =>
        status == PokerusStatus.Cured ? "pokerus-cured" : "pokerus-infected";
}
