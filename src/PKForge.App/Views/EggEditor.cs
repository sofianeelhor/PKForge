using PKForge.App.Theme;
using PKForge.Domain;
using PKForge.Engine;

namespace PKForge.App.Views;

/// <summary>
/// The egg tools of one Pokémon (issue #75): for an egg, its hatch counter, its starting moves,
/// its egg place and date (the met / origin editor), and Hatch now; for a Pokémon, turning it
/// into an egg of its line. Every change follows PKHeX's egg rules (<see cref="EggService"/>):
/// one it finds illegal is refused with the reason, nothing written. Like the other sub-editors
/// it edits the live session; the caller writes once, with a restore point, when it closes.
/// </summary>
public static class EggEditor
{
    private const string Title = "Egg";
    private const string Counter = "Hatch counter";
    private const string Moves = "Starting moves";
    private const string Origin = "Egg place and date";
    private const string HatchNow = "Hatch now";
    private const string MakeEgg = "Turn into an egg";
    private const string MetData = "Met / origin";

    public static async Task<bool> ShowAsync(Grid host, ISaveEngineSession session, int box, int slot)
    {
        if (!EggService.IsSupported(session))
        {
            await EditorMenu.ShowAsync(host, Title, "This game has no eggs to edit here (eggs are edited from Gen 3 on, outside Let's Go and the Legends games).", "OK");
            return false;
        }
        var dirty = false;
        while (true)
        {
            EggStatus egg;
            try { egg = EggService.Get(session, box, slot); }
            catch (InvalidOperationException error) { await EditorMenu.ShowAsync(host, Title, error.Message, "OK"); return dirty; }

            var legal = egg.Legal ? "Legal." : "PKHeX finds it illegal: see the legality report.";
            string message;
            var options = new List<PadOption>();
            if (egg.IsEgg)
            {
                message = $"{egg.Species} egg. Hatches in {egg.HatchCounter} cycle{(egg.HatchCounter == 1 ? "" : "s")}, about {egg.StepsLeft:N0} steps. {legal}";
                options.Add(new($"{Counter} · {egg.HatchCounter}/{egg.MaxHatchCounter}", IconPath: "calendar",
                    Detail: $"Cycles left, {egg.MinHatchCounter} to {egg.MaxHatchCounter} for this species."));
                options.Add(new(Moves, IconPath: "moves", Detail: "Level-up, egg and inherited moves; PKHeX checks the set."));
                options.Add(new(Origin, IconPath: "map", Detail: "Where and when it was received; PKHeX can suggest."));
                options.Add(new(HatchNow, IconPath: "egg", Accent: UiTokens.Green, Detail: "With the met place and date the game writes."));
            }
            else
            {
                message = $"{egg.Species}. {legal}";
                options.Add(egg.MakeEggRefusal is { } refusal
                    ? new(MakeEgg, IconPath: "egg", Detail: refusal)
                    : new(MakeEgg, IconPath: "egg", Accent: UiTokens.Blueprint, Detail: "A fresh egg of its line, keeping nature and IVs where legal."));
                options.Add(new(MetData, IconPath: "map", Detail: "Hatched from an egg, egg place and date, met data."));
            }

            var choice = await EditorMenu.ShowAsync(host, Title, message, options.ToArray());
            if (choice is null) return dirty;
            GenerationOutcome? outcome = null;
            if (choice.StartsWith(Counter, StringComparison.Ordinal))
            {
                var value = await StatsPopup.ShowSingleAsync(host, Counter, egg.HatchCounter, egg.MaxHatchCounter,
                    $"{egg.MinHatchCounter} to {egg.MaxHatchCounter} cycles");
                if (value is { } cycles && cycles != egg.HatchCounter) outcome = EggService.SetHatchCounter(session, box, slot, cycles);
            }
            else if (choice == Moves)
            {
                outcome = await PickMovesAsync(host, session, box, slot, egg.Moves);
            }
            else if (choice is Origin or MetData)
            {
                if (await MetOriginEditor.ShowAsync(host, session, box, slot)) dirty = true;
            }
            else if (choice == HatchNow)
            {
                if (await PadMenu.ConfirmAsync(host, "Hatch now?", $"The {egg.Species} egg hatches with the met place and date the game writes, and is registered in the Pokédex.", "Hatch"))
                    outcome = EggService.Hatch(session, box, slot);
            }
            else if (choice == MakeEgg)
            {
                if (egg.MakeEggRefusal is { } refusal) await EditorMenu.ShowAsync(host, MakeEgg, refusal, "OK");
                else if (await PadMenu.ConfirmAsync(host, "Turn into an egg?", $"{egg.Species} becomes a fresh egg of its line. Its level, moves, ribbons and history are gone. Restore points can undo it.", "Turn into an egg"))
                    outcome = EggService.MakeEgg(session, box, slot);
            }

            if (outcome is null) continue;
            if (outcome.Success) dirty = true;
            await EditorMenu.ShowAsync(host, Title, outcome.Message, "OK");
        }
    }

    /// <summary>Four move slots, each picked from what an egg of the species can start with; Apply asks PKHeX.</summary>
    private static async Task<GenerationOutcome?> PickMovesAsync(Grid host, ISaveEngineSession session, int box, int slot, IReadOnlyList<int> current)
    {
        var choices = EggService.GetEggMoveChoices(session, box, slot);
        if (choices.Count == 0) return new GenerationOutcome(false, "No move list is known for this egg.");
        var names = choices.ToDictionary(c => c.Move, c => c.Name);
        var moves = current.Concat(Enumerable.Repeat(0, 4)).Take(4).ToArray();
        const string Apply = "Apply";
        while (true)
        {
            var options = moves.Select((move, i) => new PadOption($"Move {i + 1} · {(move == 0 ? "none" : names.GetValueOrDefault(move, $"#{move}"))}",
                IconPath: "moves", Detail: choices.FirstOrDefault(c => c.Move == move)?.Source)).ToList();
            options.Add(new(Apply, Accent: UiTokens.Green, Detail: "PKHeX checks that an egg can start with these."));
            var choice = await EditorMenu.ShowAsync(host, Moves, null, options.ToArray());
            if (choice is null) return null;
            if (choice == Apply) return EggService.SetEggMoves(session, box, slot, moves);
            var index = choice[5] - '1';
            if (index is < 0 or > 3) continue;
            var items = new List<PickItem> { new(0, "None", Detail: "Leave this slot empty") };
            items.AddRange(choices.Select(c => new PickItem(c.Move, c.Name, Detail: c.Source)));
            var picked = await PickerMenu.ShowAsync(host, $"Move {index + 1}", items, moves[index]);
            if (picked is null) continue;
            if (picked.Id != 0 && moves.Where((_, i) => i != index).Contains(picked.Id)) continue;
            moves[index] = picked.Id;
        }
    }
}
