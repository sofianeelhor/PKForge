using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// HeartGold/SoulSilver gadgets: the Apricorn Box (PKHeX SAV_Apricorn) and the Pokéwalker
/// (PKHeX SAV_Misc4 Walker tab). Apricorns are bag contents (<see cref="SaveAction.EditInventory"/>);
/// Pokéwalker steps, watts and courses are world state (<see cref="SaveAction.EditWorld"/>).
/// </summary>
public static class HgssGadgetsEditor
{
    private const string AllApricorns = "Fill every Apricorn (99)";
    private const string NoApricorns = "Empty the Apricorn Box";

    private const string Steps = "Set steps…";
    private const string Watts = "Set watts…";
    private const string UnlockAll = "Unlock every course";

    public static async Task ShowApricornsAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var apricorns = ApricornService.GetApricorns(session);
            var detail = "Give Apricorns to Kurt in Azalea Town and he makes one Poké Ball from each, ready the next day.";
            if (await WorldEventsMenu.BlockedAsync(host, "Apricorn Box",
                    detail + Environment.NewLine + string.Join(Environment.NewLine, apricorns.Select(a => $"{a.Name}: {a.Count}")),
                    SaveAction.EditInventory))
                return;

            var options = apricorns.Select(a => new PadOption($"{a.Name} × {a.Count}", IconPath: "ball", Detail: $"Makes a {a.BallMade}"))
                .Append(new PadOption(AllApricorns, IconPath: "fill", Accent: UiTokens.Green))
                .Append(new PadOption(NoApricorns, IconPath: "clear"))
                .ToArray();
            var choice = await EditorMenu.ShowAsync(host, "Apricorn Box", detail, options);
            if (choice is null) return;
            bool ok;
            if (choice == AllApricorns)
                ok = await WorldEventsMenu.WriteAsync(viewModel, s => { ApricornService.SetAll(s, ApricornService.MaxCount); return "Every Apricorn set to 99."; }, SaveAction.EditInventory);
            else if (choice == NoApricorns)
                ok = await WorldEventsMenu.WriteAsync(viewModel, s => { ApricornService.SetAll(s, 0); return "Apricorn Box emptied."; }, SaveAction.EditInventory);
            else
            {
                var index = Array.FindIndex(options, o => o.Label == choice);
                if (index < 0 || index >= apricorns.Count) continue;
                var apricorn = apricorns[index];
                var count = await StatsPopup.ShowSingleAsync(host, apricorn.Name, apricorn.Count, ApricornService.MaxCount, "0 - 99");
                if (count is not { } value) continue;
                ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                    $"{apricorn.Name}: {ApricornService.SetCount(s, apricorn.Index, value)[apricorn.Index].Count}.", SaveAction.EditInventory);
            }
            if (!ok) return;
        }
    }

    public static async Task ShowPokewalkerAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var state = PokewalkerService.GetState(session);
            var unlocked = state.Courses.Count(c => c.Unlocked);
            var detail = $"Steps {state.Steps:N0} · Watts {state.Watts:N0} · {unlocked}/{state.Courses.Count} courses unlocked" +
                Environment.NewLine + "Pick a course to see what it gives.";
            var blocked = PKForge.App.Services.HardcoreMode.Blocks(SaveAction.EditWorld, out _);

            var options = new List<PadOption>();
            if (!blocked)
            {
                options.Add(new PadOption(Steps, IconPath: "records"));
                options.Add(new PadOption(Watts, IconPath: "stats"));
                options.Add(new PadOption(UnlockAll, IconPath: "all", Accent: UiTokens.Green));
            }
            options.AddRange(state.Courses.Select(c => new PadOption(CourseLabel(c),
                Accent: c.Unlocked ? UiTokens.Green : null, IconPath: c.Unlocked ? "check" : "padlock",
                Detail: string.Join(", ", c.Encounters))));
            var choice = await EditorMenu.ShowAsync(host, "Pokéwalker",
                blocked ? detail + Environment.NewLine + PKForge.App.Services.HardcoreMode.StatusFor(SaveAction.EditWorld) : detail,
                options.ToArray());
            if (choice is null) return;

            bool ok;
            switch (choice)
            {
                case Steps:
                case Watts:
                {
                    var isSteps = choice == Steps;
                    var value = await StatsPopup.ShowSingleAsync(host, isSteps ? "Steps" : "Watts",
                        (int)Math.Min(isSteps ? state.Steps : state.Watts, PokewalkerService.MaxCounter), (int)PokewalkerService.MaxCounter,
                        $"0 - {PokewalkerService.MaxCounter:N0}");
                    if (value is not { } v) continue;
                    ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                    {
                        var after = PokewalkerService.SetCounters(s, isSteps ? (uint)v : state.Steps, isSteps ? state.Watts : (uint)v);
                        return $"Pokéwalker: {after.Steps:N0} steps, {after.Watts:N0} watts.";
                    });
                    break;
                }
                case UnlockAll:
                    ok = await WorldEventsMenu.WriteAsync(viewModel, s =>
                        $"{PokewalkerService.UnlockAll(s).Courses.Count(c => c.Unlocked)} courses unlocked (every course your game's language has).");
                    break;
                default:
                {
                    var course = state.Courses.FirstOrDefault(c => CourseLabel(c) == choice);
                    if (course is null) continue;
                    ok = await ShowCourseAsync(host, viewModel, course, blocked);
                    break;
                }
            }
            if (!ok) return;
        }
    }

    private static async Task<bool> ShowCourseAsync(Grid host, BoxBrowserViewModel viewModel, PokewalkerCourse course, bool blocked)
    {
        var detail = (course.IsSpecial
                ? "Special course: it was handed out by events, not unlocked by earning watts."
                : "Unlocked on the Pokéwalker by earning watts.") + Environment.NewLine +
            "Pokémon you can catch here:" + Environment.NewLine + string.Join(Environment.NewLine, course.Encounters) +
            (course.AvailableForLanguage ? "" : Environment.NewLine + "Your game's language never received this course.");
        if (blocked || (!course.AvailableForLanguage && !course.Unlocked))
        {
            await EditorMenu.ShowAsync(host, course.Name, detail, "OK");
            return true;
        }
        var toggle = course.Unlocked ? "Lock this course" : "Unlock this course";
        var choice = await EditorMenu.ShowAsync(host, course.Name, detail,
            new PadOption(toggle, IconPath: course.Unlocked ? "padlock" : "check", Accent: course.Unlocked ? null : UiTokens.Green));
        if (choice != toggle) return true;
        return await WorldEventsMenu.WriteAsync(viewModel, s =>
        {
            PokewalkerService.SetCourse(s, course.Index, !course.Unlocked);
            return $"{course.Name} {(course.Unlocked ? "locked" : "unlocked")}.";
        });
    }

    private static string CourseLabel(PokewalkerCourse c) => $"{c.Index + 1:00} {c.Name}{(c.IsSpecial ? " ★" : "")}";
}
