using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>
/// Bringing a Pokéwalker walk home exactly once, whatever stops it halfway (an IO error, a full
/// Bank, Android closing the app during the legality checks):
/// <list type="number">
/// <item><see cref="Begin"/> ends the walk and records the homecoming (with its catches already
/// made legal) in one state, which the caller saves before anything is banked: the walk cannot
/// come home a second time.</item>
/// <item><see cref="Bank"/> banks each catch and saves that it did, and sends the finds to the Item
/// Bank under the homecoming's key, which the Item Bank takes only once. Run again after a crash, it
/// picks up where it stopped; a catch banked just before the crash is recognised by its bytes.</item>
/// <item>The caller clears <see cref="WalkerState.Homecoming"/> once the Pokémon itself has grown.</item>
/// </list>
/// </summary>
public static class WalkerHomecomings
{
    public const string SourceName = "Pokéwalker";

    /// <summary>The walk ends: its catches are made legal now, and the state holds the homecoming
    /// instead of the walk. A state already bringing one home is returned as it is.</summary>
    public static WalkerState Begin(WalkerState counted, ITrainerInfo trainer)
    {
        ArgumentNullException.ThrowIfNull(counted);
        if (counted.Homecoming is not null) return counted;
        var walk = counted.Walk ?? throw new InvalidOperationException("Nobody is on a walk.");
        var (state, home) = WalkerRules.Return(counted);
        var catches = new List<WalkerPendingCatch>();
        var notes = new List<string>();
        foreach (var slot in home.Catches)
        {
            if (WalkerService.Catch(walk.Course, slot, trainer, out var why) is not { } caught) { notes.Add($"A catch could not be made legal ({why})."); continue; }
            catches.Add(new WalkerPendingCatch(Convert.ToBase64String(caught.Data), caught.Format, caught.Name, caught.Level));
        }
        return state with { Homecoming = new WalkerPendingHome(WalkerRules.HomeKey(walk), walk, home, catches, notes) };
    }

    /// <summary>Banks what the homecoming still holds, saving progress with <paramref name="save"/> after
    /// each step. Returns the state reached and what happened, in sentences.</summary>
    public static (WalkerState State, List<string> Lines) Bank(WalkerState state, IBankService bank,
        Func<byte[], string, BankEntryInfo?> describe, IItemBankStore items, Action<WalkerState> save)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(describe);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(save);
        var pending = state.Homecoming ?? throw new InvalidOperationException("No walk is coming home.");
        var lines = new List<string>(pending.Notes);
        for (var i = 0; i < pending.Catches.Count; i++)
        {
            var caught = pending.Catches[i];
            if (!caught.Banked)
            {
                var bytes = caught.Bytes();
                var box = AlreadyBanked(bank, bytes);
                if (box is null)
                {
                    if (describe(bytes, caught.Format) is not { } info) { lines.Add($"{caught.Name} could not be read back, so it stays with the walk."); continue; }
                    box = bank.Add(bytes, info).Box;
                }
                caught = caught with { Banked = true, Box = box.Value };
                pending = pending with { Catches = [.. pending.Catches.Select((c, at) => at == i ? caught : c)] };
                state = state with { Homecoming = pending };
                save(state);
            }
            lines.Add($"{caught.Name} (Lv. {caught.Level}) is in your Bank, box {caught.Box + 1}.");
        }

        var finds = pending.Home.Items;
        if (finds.Count > 0)
        {
            if (!pending.ItemsBanked)
            {
                items.UpdateOnce(pending.Key, bankItems =>
                {
                    foreach (var name in finds)
                        if (WalkerService.BankKey(name) is { } key) bankItems = bankItems.Deposit(key.Key, key.Name, 1, SourceName, 4);
                    return bankItems;
                });
                pending = pending with { ItemsBanked = true };
                state = state with { Homecoming = pending };
                save(state);
            }
            lines.Add($"{string.Join(", ", finds)} {(finds.Count == 1 ? "is" : "are")} in your Item Bank.");
        }
        return (state, lines);
    }

    /// <summary>The box of a Pokéwalker catch the Bank already holds with exactly these bytes (the
    /// deposit landed, the note of it did not), else null.</summary>
    private static int? AlreadyBanked(IBankService bank, byte[] bytes)
    {
        foreach (var entry in bank.GetAll().Where(e => e.Info.SourceName == SourceName))
            if (bank.GetData(entry.Id).AsSpan().SequenceEqual(bytes)) return entry.Box;
        return null;
    }
}
