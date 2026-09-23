using PKForge.App.Theme;
using PKForge.Domain;

namespace PKForge.App.Views;

/// <summary>
/// The one nature chooser every editor opens: each row says what the nature does
/// ("+Atk −SpA", neutral, liked/disliked flavours) and, when the Pokémon's numbers are
/// known, a live panel shows its six stats now versus with the highlighted nature.
/// Viewing is read-only; callers apply the pick through their usual (guarded) paths.
/// </summary>
public static class NaturePicker
{
    /// <summary>Picker rows: name plus the stat effect and flavour line.</summary>
    public static List<PickItem> Items(IReadOnlyList<string> names) =>
        Enumerable.Range(0, Math.Min(names.Count, NatureFacts.Count)).Where(i => names[i].Length > 0)
            .Select(i => new PickItem(i, names[i], null, $"{NatureFacts.EffectLabel(i)}  ·  {NatureFacts.FlavorLabel(i)}"))
            .ToList();

    /// <summary>Names with the effect appended ("Adamant  +Atk −SpA"), for editor rows that show the current pick.</summary>
    public static IReadOnlyList<string> DisplayNames(IReadOnlyList<string> names) =>
        names.Select((name, i) => name.Length > 0 && NatureFacts.IsValid(i) ? $"{name}  {NatureFacts.EffectLabel(i)}" : name).ToList();

    /// <summary>The preview service registered by the app (null outside the MAUI host).</summary>
    public static IStatPreviewService? Service =>
        IPlatformApplication.Current?.Services.GetService<IStatPreviewService>();

    /// <param name="preview">Stats for every nature; null shows the labels only.</param>
    public static Task<PickItem?> ShowAsync(Grid host, IReadOnlyList<string> names, int? current, NatureStatPreview? preview,
        string title = "NATURE")
    {
        var items = Items(names);
        return PickerMenu.ShowAsync(host, title, items, current, preview is null ? null : BuildPanel(names, preview));
    }

    private static PickerPreview BuildPanel(IReadOnlyList<string> names, NatureStatPreview preview)
    {
        var heading = InfoKit.Heading();
        var basis = InfoKit.DetailLine(preview.Basis);
        basis.FontSize = 10;
        var note = InfoKit.Note();
        var grid = new InfoKit.StatDeltaGrid();

        void Show(PickItem? item)
        {
            if (item is null) return;
            var nature = item.Id;
            heading.Text = nature == preview.CurrentNature
                ? $"{item.Name.ToUpperInvariant()} (CURRENT)  {NatureFacts.EffectLabel(nature)}"
                : $"IF {item.Name.ToUpperInvariant()}  {NatureFacts.EffectLabel(nature)}";
            if (preview.StatNatureLock is { } locked && locked < names.Count)
            {
                note.Text = $"Minted: stats follow {names[locked]} ({NatureFacts.EffectLabel(locked)}); changing the nature keeps them.";
                note.IsVisible = true;
            }
            // Mark the stats this nature itself boosts/cuts, even when a mint masks the change.
            grid.Show(preview.Current, preview.StatsFor(nature), stat =>
                NatureFacts.Raised(nature) == stat ? (" +", UiTokens.Green)
                : NatureFacts.Lowered(nature) == stat ? (" −", UiTokens.RedOrange)
                : null);
        }

        return new PickerPreview(InfoKit.Card(heading, basis, grid, note), Show);
    }
}
