namespace PKForge.Domain;

/// <summary>
/// What restoring an older restore point would bring back, in Hardcore mode's terms:
/// every Pokémon the restore point holds that the save no longer does. Such a Pokémon
/// was moved to the Bank, sent to another game or released since that point, and
/// restoring would make it exist twice (or come back from release).
/// </summary>
/// <param name="Reappearing">Pokémon in the restore point that are missing from the current save.</param>
/// <param name="InBank">How many of <paramref name="Reappearing"/> match a Bank entry right now.</param>
public sealed record RestoreResurrection(IReadOnlyList<SlotSummary> Reappearing, int InBank)
{
    /// <summary>True when restoring changes nothing about which Pokémon exist.</summary>
    public bool IsSafe => Reappearing.Count == 0;

    /// <summary>Reappearing Pokémon that are not in the Bank: sent to another game or released.</summary>
    public int Elsewhere => Reappearing.Count - InBank;

    /// <summary>
    /// Compares the restore point's boxes with the current save's. Slots are matched as a
    /// multiset on what a snapshot carries (species, form, shininess, nickname, egg), so
    /// box moves and sorts since the restore point are not mistaken for departures; two
    /// indistinguishable Pokémon are counted as one pair each. Bank matches ignore the
    /// nickname because bank entries carry a display name rather than a nickname flag.
    /// </summary>
    public static RestoreResurrection Detect(
        IReadOnlyList<SlotSummary> restorePoint,
        IReadOnlyList<SlotSummary> current,
        IReadOnlyList<BankEntryInfo> bank)
    {
        var present = new Dictionary<(int, int, bool, string, bool), int>();
        foreach (var slot in current)
        {
            if (slot.Species is not { } species) continue;
            var key = (species, slot.Form, slot.IsShiny, slot.Nickname ?? string.Empty, slot.IsEgg);
            present[key] = present.GetValueOrDefault(key) + 1;
        }

        var reappearing = new List<SlotSummary>();
        foreach (var slot in restorePoint)
        {
            if (slot.Species is not { } species) continue;
            var key = (species, slot.Form, slot.IsShiny, slot.Nickname ?? string.Empty, slot.IsEgg);
            if (present.GetValueOrDefault(key) > 0) present[key]--;
            else reappearing.Add(slot);
        }

        var banked = new Dictionary<(int, int, bool), int>();
        foreach (var info in bank)
        {
            var key = (info.Species, info.Form, info.Shiny);
            banked[key] = banked.GetValueOrDefault(key) + 1;
        }

        var inBank = 0;
        foreach (var slot in reappearing)
        {
            var key = (slot.Species!.Value, slot.Form, slot.IsShiny);
            if (banked.GetValueOrDefault(key) <= 0) continue;
            banked[key]--;
            inBank++;
        }

        return new RestoreResurrection(reappearing, inBank);
    }
}
