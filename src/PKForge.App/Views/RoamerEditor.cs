using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Roaming legendaries (PKHeX SAV_Roamer3 / Roamer4 / Roamer5 / SAV_Roamer6). Rerolls draw a new
/// seed with the game's own generation method, so the caught Pokémon stays legal; shiny is a seed
/// search, never a PID edit.
/// </summary>
public static class RoamerEditor
{
    private const string Reroll = "Reroll PID and IVs";
    private const string RerollShiny = "Reroll until shiny (legal)";
    private const string SetState = "Set roam state…";
    private const string Times = "Times encountered…";

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var roamers = RoamerService.GetRoamers(session);
            var options = roamers.Select(r => new PadOption($"{r.Label} · {r.State}",
                Accent: r.IsShiny ? UiTokens.Gold : null, IconPath: r.IsShiny ? "shiny" : "rarity",
                Detail: r.Species == 0 ? null : $"{r.SpeciesName} Lv.{r.Level}")).ToArray();
            var choice = await EditorMenu.ShowAsync(host, "Roaming legendaries", null, options);
            var index = Array.FindIndex(options, o => o.Label == choice);
            if (index < 0) return;
            if (!await ShowRoamerAsync(host, session, viewModel, roamers[index])) return;
        }
    }

    private static async Task<bool> ShowRoamerAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel, RoamerInfo roamer)
    {
        var detail = Describe(roamer);
        if (await WorldEventsMenu.BlockedAsync(host, roamer.Label, detail)) return true;

        var actions = new List<PadOption>();
        if (roamer.CanReroll)
        {
            actions.Add(new PadOption(Reroll, IconPath: "dice"));
            actions.Add(new PadOption(RerollShiny, IconPath: "shiny", Accent: UiTokens.Gold));
        }
        if (roamer.Generation == 6)
        {
            actions.Add(new PadOption(SetState, IconPath: "map"));
            actions.Add(new PadOption(Times, IconPath: "records"));
        }
        if (actions.Count == 0)
        {
            await EditorMenu.ShowAsync(host, roamer.Label, detail, "OK");
            return true;
        }

        var choice = await EditorMenu.ShowAsync(host, roamer.Label, detail, actions.ToArray());
        switch (choice)
        {
            case Reroll:
            case RerollShiny:
                var shiny = choice == RerollShiny;
                return await WorldEventsMenu.WriteAsync(viewModel, s =>
                {
                    var after = RoamerService.Reroll(s, roamer.Index, shiny);
                    return $"{after.SpeciesName}: new PID {after.Pid:X8}{(after.IsShiny ? " (shiny)" : "")}, IVs {string.Join("/", after.CatchIVs)}.";
                });
            case SetState:
            {
                var states = RoamerService.Gen6States.ToArray();
                var picked = await EditorMenu.ShowAsync(host, "Roam state",
                    "Captured and Defeated end the chase; Roaming puts the bird back on the routes.", states);
                var state = Array.IndexOf(states, picked);
                if (state < 0) return true;
                return await WorldEventsMenu.WriteAsync(viewModel, s =>
                    $"{RoamerService.SetGen6State(s, state).Label} is now {states[state].ToLowerInvariant()}.");
            }
            case Times:
            {
                var times = await StatsPopup.ShowSingleAsync(host, "Times encountered", (int)(roamer.TimesEncountered ?? 0), 9999, "0 - 9999");
                if (times is not { } value) return true;
                var current = RoamerService.Gen6States.ToList().IndexOf(roamer.State);
                return await WorldEventsMenu.WriteAsync(viewModel, s =>
                    $"{RoamerService.SetGen6State(s, Math.Max(0, current), (uint)value).Label}: met {value} time(s).");
            }
            default:
                return true;
        }
    }

    private static string Describe(RoamerInfo r)
    {
        if (r.Species == 0)
            return r.Note is null ? r.State : $"{r.State}{Environment.NewLine}{r.Note}";
        var lines = new List<string> { $"{r.SpeciesName} · Lv.{r.Level} · {r.State}" };
        if (r.Pid is { } pid)
            lines.Add($"PID {pid:X8}{(r.Nature is null ? "" : $" · {r.Nature}")}{(r.IsShiny ? " · SHINY" : "")}");
        if (r.IVs.Count == 6 && r.Species != 0)
        {
            lines.Add($"IVs {string.Join("/", r.IVs)} (HP/Atk/Def/SpA/SpD/Spe)");
            if (!r.CatchIVs.SequenceEqual(r.IVs))
                lines.Add($"Catch IVs {string.Join("/", r.CatchIVs)}");
        }
        if (r.TimesEncountered is { } times)
            lines.Add($"Met {times} time(s)");
        if (r.Note is not null)
            lines.Add(r.Note);
        return string.Join(Environment.NewLine, lines);
    }
}
