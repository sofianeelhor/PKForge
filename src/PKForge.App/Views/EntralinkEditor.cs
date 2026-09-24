using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Gen 5 Entralink (PKHeX SAV_Misc5): Entralink levels, the Entree Forest filled only from PKHeX's
/// legal Dream World templates, and on Black 2/White 2 the Pass Powers and Funfest missions.
/// </summary>
public static class EntralinkEditor
{
    private const string Areas = "Entree Forest areas";
    private const string Fill = "Fill the forest with legal Dream World Pokémon";
    private const string Clear = "Empty the forest";
    private const string UnlockAreas = "Open every forest area";
    private const string Levels = "Entralink levels…";
    private const string Powers = "Pass Powers";
    private const string Missions = "Funfest missions";

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var state = EntralinkService.GetState(session);
            var detail = $"White Forest level {state.WhiteForestLevel} · Black City level {state.BlackCityLevel}" + Environment.NewLine +
                $"Forest: {state.VisitorCount} Pokémon · areas 1-{state.UnlockedAreas} open{(state.NinthAreaUnlocked ? " + area 9" : "")}" +
                Environment.NewLine + "Pokémon met in the Entree Forest are Dream World catches; only PKHeX's legal Dream World list is used here.";
            var blocked = PKForge.App.Services.HardcoreMode.Blocks(SaveAction.EditWorld, out var status);

            var options = new List<PadOption> { new(Areas, IconPath: "tree") };
            if (!blocked)
            {
                options.Add(new PadOption(Fill, IconPath: "fill", Accent: UiTokens.Green));
                options.Add(new PadOption(UnlockAreas, IconPath: "all"));
                options.Add(new PadOption(Clear, IconPath: "clear"));
                options.Add(new PadOption(Levels, IconPath: "level"));
            }
            if (state.IsB2W2)
            {
                options.Add(new PadOption(Powers, IconPath: "power", Detail: string.Join(" · ", state.PassPowers)));
                options.Add(new PadOption(Missions, IconPath: "check",
                    Detail: $"{state.Missions.Count(m => m.Unlocked)}/{state.Missions.Count} unlocked"));
            }
            var choice = await EditorMenu.ShowAsync(host, "Entralink", blocked ? detail + Environment.NewLine + status : detail, options.ToArray());
            bool ok = true;
            switch (choice)
            {
                case Areas:
                    await ShowAreasAsync(host, state);
                    break;
                case Fill:
                    if (await EditorMenu.ConfirmAsync(host, "Refill the forest?",
                            "Every slot is replaced with a random Pokémon from the Dream World list for this game, each with a move it can have there. All areas open.", "Fill"))
                        ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                            $"Entree Forest filled: {EntralinkService.FillForestLegally(s).VisitorCount} legal Dream World Pokémon.");
                    break;
                case UnlockAreas:
                    ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                    {
                        EntralinkService.UnlockAllAreas(s);
                        return "Every Entree Forest area is open.";
                    });
                    break;
                case Clear:
                    if (await EditorMenu.ConfirmAsync(host, "Empty the forest?", "Every Entree Forest Pokémon leaves.", "Empty"))
                        ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                        {
                            EntralinkService.ClearForest(s);
                            return "Entree Forest emptied.";
                        });
                    break;
                case Levels:
                {
                    var white = await StatsPopup.ShowSingleAsync(host, "White Forest level", state.WhiteForestLevel, EntralinkService.MaxLevel, "0 - 999");
                    if (white is not { } w) break;
                    var black = await StatsPopup.ShowSingleAsync(host, "Black City level", state.BlackCityLevel, EntralinkService.MaxLevel, "0 - 999");
                    if (black is not { } b) break;
                    ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                    {
                        var after = EntralinkService.SetLevels(s, w, b);
                        return $"Entralink levels: White Forest {after.WhiteForestLevel}, Black City {after.BlackCityLevel}.";
                    });
                    break;
                }
                case Powers:
                    ok = await ShowPowersAsync(host, viewModel, state, blocked);
                    break;
                case Missions:
                    ok = await ShowMissionsAsync(host, viewModel, state, blocked);
                    break;
                default:
                    return;
            }
            if (!ok) return;
        }
    }

    private static Task ShowAreasAsync(Grid host, EntralinkState state)
    {
        var lines = state.Areas.Select(a =>
            $"{a.Name}{(a.Unlocked ? "" : " (locked)")}: {(a.Visitors.Count == 0 ? "empty" : string.Join(", ", a.Visitors.Distinct().Take(8)) + (a.Visitors.Distinct().Count() > 8 ? "…" : ""))}");
        return EditorMenu.ShowAsync(host, "Entree Forest", string.Join(Environment.NewLine, lines), "OK");
    }

    private static async Task<bool> ShowPowersAsync(Grid host, BoxBrowserViewModel viewModel, EntralinkState state, bool blocked)
    {
        var detail = "Pass Powers are Entralink boosts (encounters, hatching, capture, EXP...) you can switch on in the field.";
        if (blocked)
        {
            await EditorMenu.ShowAsync(host, "Pass Powers", detail + Environment.NewLine + string.Join(Environment.NewLine, state.PassPowers), "OK");
            return true;
        }
        var slots = state.PassPowers.Select((p, i) => $"Slot {i + 1} · {p}").ToArray();
        var slotChoice = await EditorMenu.ShowAsync(host, "Pass Powers", detail, slots);
        var slot = Array.IndexOf(slots, slotChoice);
        if (slot < 0) return true;
        var choices = EntralinkService.GetPassPowerChoices();
        var picked = await PickerMenu.ShowAsync(host, $"Slot {slot + 1}",
            choices.Select(c => new PickItem(c.Value, c.Name)).ToArray());
        if (picked is null) return true;
        return await WorldEventsMenu.WriteAsync(viewModel, s =>
            $"Pass Power slot {slot + 1}: {EntralinkService.SetPassPower(s, slot, picked.Id).PassPowers[slot]}.");
    }

    private static async Task<bool> ShowMissionsAsync(Grid host, BoxBrowserViewModel viewModel, EntralinkState state, bool blocked)
    {
        var lines = string.Join(Environment.NewLine, state.Missions.Select(m => $"{(m.Unlocked ? "✓" : "·")} {m.Name}"));
        if (blocked)
        {
            await EditorMenu.ShowAsync(host, "Funfest missions", lines, "OK");
            return true;
        }
        const string unlock = "Unlock every mission";
        var choice = await EditorMenu.ShowAsync(host, "Funfest missions",
            "Unlocking sets each mission's story prerequisite flags, as PKHeX does." + Environment.NewLine + lines,
            new PadOption(unlock, IconPath: "all", Accent: UiTokens.Green));
        if (choice != unlock) return true;
        return await WorldEventsMenu.WriteAsync(viewModel, s =>
            $"{EntralinkService.UnlockAllMissions(s).Missions.Count(m => m.Unlocked)} Funfest missions unlocked.");
    }
}
