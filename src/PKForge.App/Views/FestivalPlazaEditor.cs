using System.Globalization;
using PKForge.App.Services;
using PKForge.App.ViewModels;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// Sun/Moon and Ultra Sun/Ultra Moon Festival Plaza, after PKHeX's SAV_FestivalPlaza: coins,
/// rank, rewards, phrases and the seven facilities. Everything is shown in Hardcore mode;
/// the writes are trainer data and go through the safe mutation pipeline.
/// </summary>
public static class FestivalPlazaEditor
{
    private const string SetCoins = "Set Festival Coins…";
    private const string SetRank = "Set plaza rank…";
    private const string AllRewards = "Receive every rank reward";
    private const string AllPhrases = "Unlock every phrase";
    private const string Facilities = "Facilities";
    private const string ChangeType = "Change facility…";
    private const string ChangeColor = "Change colour…";
    private const SaveAction Action = SaveAction.EditTrainer;

    public static async Task ShowAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel)
    {
        if (!FestivalPlazaService.IsSupported(session))
        {
            await EditorMenu.ShowAsync(host, "Festival Plaza", "Festival Plaza exists only in Sun, Moon, Ultra Sun and Ultra Moon.", "OK");
            return;
        }

        var slot = Math.Max(0, viewModel.SelectedSlot);
        while (true)
        {
            var info = FestivalPlazaService.Get(session);
            var received = info.Rewards.Count(r => r.State == FestivalPlazaService.RewardReceived);
            var summary = $"{(info.PlazaName.Length != 0 ? info.PlazaName + " · " : "")}Rank {info.Rank} ({info.RankCoinRange} FC)" + Environment.NewLine +
                $"{Fc(info.Coins)} FC now · {Fc(info.CoinsUsed)} spent · {Fc(info.CoinsCollected)} collected" + Environment.NewLine +
                $"Rewards {received}/{info.Rewards.Count} · phrases {info.PhrasesUnlocked}/{info.PhraseCount}";
            var blocked = HardcoreMode.Blocks(Action, out var status);
            var options = new List<PadOption> { new(Facilities, Detail: "The seven facilities: kind, level and colour.") };
            if (blocked)
                summary += Environment.NewLine + status;
            else
            {
                options.Add(new PadOption(SetCoins, Detail: "Coins to spend; the collected total follows."));
                options.Add(new PadOption(SetRank, Detail: "Plaza rank, 1 to 999."));
                options.Add(new PadOption(AllRewards, Detail: "Marks every rank reward as received."));
                options.Add(new PadOption(AllPhrases, Detail: "Every phrase the tent can teach."));
            }

            var choice = await EditorMenu.ShowAsync(host, "Festival Plaza", summary, options.ToArray());
            switch (choice)
            {
                case null: return;
                case Facilities:
                    if (!await ShowFacilitiesAsync(host, session, viewModel, slot, blocked)) return;
                    break;
                case SetCoins:
                {
                    var coins = await StatsPopup.ShowSingleAsync(host, "Festival Coins", info.Coins, FestivalPlazaService.MaxCoins);
                    if (coins is not { } value || value == info.Coins) break;
                    if (!await WriteAsync(viewModel, slot, s => { FestivalPlazaService.SetCoins(s, value); return $"{Fc(value)} Festival Coins."; })) return;
                    break;
                }
                case SetRank:
                {
                    var rank = await StatsPopup.ShowSingleAsync(host, "Plaza rank", info.Rank, FestivalPlazaService.MaxRank);
                    if (rank is not { } value || value == info.Rank) break;
                    if (!await WriteAsync(viewModel, slot, s => { FestivalPlazaService.SetRank(s, value); return $"Festival Plaza rank {Math.Max(value, 1)}."; })) return;
                    break;
                }
                case AllRewards:
                    if (!await WriteAsync(viewModel, slot, s =>
                        {
                            FestivalPlazaService.SetAllRewards(s, FestivalPlazaService.RewardReceived);
                            return "Every rank reward received.";
                        })) return;
                    break;
                case AllPhrases:
                    if (!await WriteAsync(viewModel, slot, s => { FestivalPlazaService.UnlockAllPhrases(s); return "Every phrase unlocked."; })) return;
                    break;
            }
        }
    }

    /// <summary>False when a write failed and the editor should close.</summary>
    private static async Task<bool> ShowFacilitiesAsync(Grid host, ISaveEngineSession session, BoxBrowserViewModel viewModel,
        int slot, bool blocked)
    {
        while (true)
        {
            var facilities = FestivalPlazaService.Get(session).Facilities;
            var picked = await PickerMenu.ShowAsync(host, "Facilities", [.. facilities.Select(f => new PickItem(f.Index,
                $"{f.Index + 1} · {f.TypeLabel}", Detail: $"{f.ColorName}{(f.IsIntroduced ? " · introduced" : "")}"))]);
            if (picked is null) return true;
            var facility = facilities[picked.Id];
            if (blocked)
            {
                await EditorMenu.ShowAsync(host, facility.TypeLabel, facility.ColorName, "OK");
                continue;
            }

            var choice = await EditorMenu.ShowAsync(host, facility.TypeLabel, $"Colour: {facility.ColorName}",
                new PadOption(ChangeType, Detail: "Kind, flavour and level."),
                new PadOption(ChangeColor));
            if (choice == ChangeType)
            {
                var types = FestivalPlazaService.GetFacilityTypes(session);
                var type = await PickerMenu.ShowAsync(host, "Facility", [.. types.Select((t, i) => new PickItem(i, t))], facility.Type);
                if (type is null || type.Id == facility.Type) continue;
                if (!await WriteAsync(viewModel, slot, s =>
                        $"Facility {facility.Index + 1}: {FestivalPlazaService.SetFacilityType(s, facility.Index, type.Id).TypeLabel}."))
                    return false;
            }
            else if (choice == ChangeColor)
            {
                var colors = FestivalPlazaService.GetColorNames(session, facility.Type);
                var color = await PickerMenu.ShowAsync(host, "Colour", [.. colors.Select((c, i) => new PickItem(i, c))], facility.Color);
                if (color is null || color.Id == facility.Color) continue;
                if (!await WriteAsync(viewModel, slot, s =>
                        $"Facility {facility.Index + 1}: {FestivalPlazaService.SetFacilityColor(s, facility.Index, color.Id).ColorName}."))
                    return false;
            }
        }
    }

    private static Task<bool> WriteAsync(BoxBrowserViewModel viewModel, int slot, Func<ISaveEngineSession, string> edit) =>
        viewModel.RunMutationAsync(s => new GenerationOutcome(true, edit(s)), slot, refreshSlot: false, action: Action);

    private static string Fc(int value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
