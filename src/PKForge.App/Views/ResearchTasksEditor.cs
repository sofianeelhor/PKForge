using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Legends: Arceus research tasks for one species, opened from the Pokédex editor's actions:
/// each task with its count and thresholds (A sets a count), then research level 10,
/// perfect, and the professor's report. Every write goes through the save's mutation path.
/// </summary>
public static class ResearchTasksEditor
{
    private const SaveAction Action = SaveAction.EditDex;

    /// <summary>Returns true when anything was written.</summary>
    public static async Task<bool> ShowAsync(Grid host, BoxBrowserViewModel viewModel, ISaveEngineSession session, int species, string name)
    {
        var wrote = false;
        while (true)
        {
            var page = LegendsResearchService.Get(session, species);
            var options = new List<PadOption>();
            foreach (var task in page.Tasks)
                options.Add(new PadOption(TaskLine(task), IconPath: task.IsMaxed ? "check" : null, Detail: TaskDetail(task)));
            const string complete = "Complete research (level 10)";
            const string perfect = "Perfect: max every task";
            const string report = "Report progress";
            if (!page.Complete) options.Add(new PadOption(complete, IconPath: "pokedex", Detail: "Raises tasks just enough for 100 points, then reports them."));
            if (!page.Perfect) options.Add(new PadOption(perfect, IconPath: "selectall", Detail: "Every task you can set at its last threshold, every form obtained, then reported."));
            if (page.Unreported > 0) options.Add(new PadOption(report, IconPath: "pokedex", Detail: $"{page.Unreported} task levels waiting for the professor."));
            options.Add(new PadOption("Close", IconPath: "close"));

            var status = $"Research level {page.Level}/10 · {page.Rate} pts" +
                (page.Perfect ? " · perfect" : page.Complete ? " · complete" : "");
            var choice = await PadMenu.ShowAsync(host, $"Research · {name}", status, [.. options]);
            if (choice is null or "Close") return wrote;

            if (choice == complete)
                wrote |= await WorldEventsMenu.WriteAsync(viewModel, s =>
                    $"{name}: research level {LegendsResearchService.Complete(s, species).Level}.", Action);
            else if (choice == perfect)
                wrote |= await WorldEventsMenu.WriteAsync(viewModel, s =>
                    LegendsResearchService.Perfect(s, species) is { Perfect: true }
                        ? $"{name}: research perfect."
                        : $"{name}: every task maxed; the rest follows the story.", Action);
            else if (choice == report)
                wrote |= await WorldEventsMenu.WriteAsync(viewModel, s =>
                    $"{name}: reported, research level {LegendsResearchService.Report(s, species).Level}.", Action);
            else if (page.Tasks.FirstOrDefault(t => TaskLine(t) == choice) is { } task)
            {
                if (!task.CanSet)
                {
                    viewModel.Status = $"{task.Label} follows the game's own progress.";
                    continue;
                }
                var value = await StatsPopup.ShowSingleAsync(host, task.Label, task.Value, 60000,
                    $"Levels at {string.Join(" · ", task.Thresholds)}");
                if (value is not { } v || v == task.Value) continue;
                wrote |= await WorldEventsMenu.WriteAsync(viewModel, s =>
                {
                    LegendsResearchService.SetTaskValue(s, species, task.Index, v);
                    return $"{name}: {task.Label} set to {v} (report it to earn the points).";
                }, Action);
            }
        }
    }

    /// <summary>Every research species at once (perfect where the story allows).</summary>
    public static async Task<bool> PerfectAllAsync(Grid host, BoxBrowserViewModel viewModel)
    {
        var confirm = await PadMenu.ShowAsync(host, "Perfect all research?",
            "Every species' tasks go to their last threshold, every form is marked obtained, and it's all reported. Quest and Arceus tasks follow the story.",
            "Perfect all", "Cancel");
        if (confirm != "Perfect all") return false;
        return await WorldEventsMenu.WriteAsync(viewModel, s =>
            $"Research perfect for {LegendsResearchService.PerfectAll(s)} species.", Action);
    }

    private static string TaskLine(ResearchTask task) =>
        $"{task.Index + 1}. {task.Label}{(task.Required ? " ★" : "")}";

    private static string TaskDetail(ResearchTask task) =>
        $"{task.Value}/{task.MaxValue} · levels {string.Join(" · ", task.Thresholds)} · reported {task.ReportedLevel}/{task.Thresholds.Count}" +
        (task.CanSet ? "" : " · follows the game");
}
