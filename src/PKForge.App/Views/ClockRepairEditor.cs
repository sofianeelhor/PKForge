using PKForge.App.Services;
using PKForge.App.Theme;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Ruby/Sapphire/Emerald clock repair (PKHeX SAV_RTC3), explained in plain words. Writes use
/// <see cref="SaveAction.RepairRtc"/>, which Hardcore mode refuses like every other data edit.
/// </summary>
public static class ClockRepairEditor
{
    private const string Reset = "Reset both clocks (recommended)";
    private const string BerryFix = "Berry fix (move past the 1-year bug)";
    private const string ArmReset = "Let the game ask for the time again";
    private const string Explain = "What do these do?";

    private const SaveAction Action = SaveAction.RepairRtc;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        while (true)
        {
            var state = ClockRepairService.GetState(session);
            var detail =
                $"Clock start: {state.Initial}{Environment.NewLine}" +
                $"Last berry/event update: {state.Elapsed}{Environment.NewLine}" +
                (state.ResetArmed ? "The in-game clock reset is armed." : "The in-game clock reset is not armed.") +
                Environment.NewLine + "Your emulator's own clock (mGBA's RTC data) is not changed.";
            if (await WorldEventsMenu.BlockedAsync(host, "Clock repair", detail, Action)) return;

            var choice = await EditorMenu.ShowAsync(host, "Clock repair", detail,
                new PadOption(Reset, Accent: UiTokens.Green, IconPath: "restore"),
                new PadOption(BerryFix, IconPath: "fix", Accent: WorldEventsMenu.Good(state.BerryFixApplied)),
                new PadOption(ArmReset, IconPath: "calendar", Accent: WorldEventsMenu.Good(state.ResetArmed)),
                new PadOption(Explain, IconPath: "info"));
            switch (choice)
            {
                case Reset:
                    if (!await WorldEventsMenu.WriteAsync(viewModel, s =>
                        {
                            ClockRepairService.ResetClocks(s);
                            return "Clocks reset: berries and daily events restart from now.";
                        }, Action)) return;
                    break;
                case BerryFix:
                    if (!await WorldEventsMenu.WriteAsync(viewModel, s =>
                        $"Berry fix applied: {ClockRepairService.ApplyBerryFix(s).Elapsed}.", Action)) return;
                    break;
                case ArmReset:
                    if (!await WorldEventsMenu.WriteAsync(viewModel, s =>
                        {
                            ClockRepairService.ArmInGameReset(s);
                            return "Clock reset armed: the game offers to set the time on next boot.";
                        }, Action)) return;
                    break;
                case Explain:
                    await EditorMenu.ShowAsync(host, "About the clock",
                        "The game stores two times: when the clock started, and when berries and daily events last updated. " +
                        "Time only moves forward in the game when the cartridge clock runs past that last update." +
                        Environment.NewLine + Environment.NewLine +
                        "Reset both clocks: sets both to zero, so the game starts counting again from the cartridge clock. " +
                        "Use this after a dead battery or after moving the save to another device or emulator." +
                        Environment.NewLine + Environment.NewLine +
                        "Berry fix: Ruby and Sapphire stop updating berries after the clock has run for a year. " +
                        "This moves the last-update counter past day 734, as PKHeX's Berry fix does." +
                        Environment.NewLine + Environment.NewLine +
                        "Ask for the time again: turns on the game's own clock reset screen.",
                        "OK");
                    break;
                default:
                    return;
            }
        }
    }
}
