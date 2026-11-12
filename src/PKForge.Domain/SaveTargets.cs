namespace PKForge.Domain;

/// <summary>
/// How the destination pickers (Send to a game, the Item Bank's game picker, the transfer sheet)
/// list the saves on the shelf. A backup copy the player put back on Home (<see cref="DetectedSave.IsCopy"/>,
/// see <see cref="SaveNoise"/>) used to stand under the very same name as its live save, and a
/// Bank → Platinum send landed in <c>ROMs/nds/backup/</c> (QA 2026-11-12). Live saves now always
/// come first, a copy says so right after the game's name (where no truncation hides it), and
/// its folder and date go on the line underneath.
/// </summary>
public static class SaveTargets
{
    /// <summary>The live saves first, then the copies; each keeps its order otherwise.</summary>
    public static IReadOnlyList<DetectedSave> LiveFirst(IEnumerable<DetectedSave> saves) =>
        [.. saves.Select((save, i) => (save, i)).OrderBy(x => x.save.IsCopy).ThenBy(x => x.i).Select(x => x.save)];

    /// <summary>
    /// One label per save, unique within the list (the menus answer with the label). Twins of one
    /// game are told apart by trainer, file and date, then a number; a copy reads
    /// "Pokémon Platinum (backup)" so it can never pass for the live save.
    /// </summary>
    public static string[] Labels(IReadOnlyList<DetectedSave> saves)
    {
        var labels = saves.Select(save =>
        {
            var label = save.IsCopy ? $"{save.GameLabel} (backup)" : save.GameLabel;
            if (saves.Count(other => other.GameLabel == save.GameLabel && other.IsCopy == save.IsCopy) > 1)
            {
                if (!string.IsNullOrEmpty(save.TrainerName)) label += $" · {save.TrainerName}";
                label += $" · {save.FileName}";
                if (save.LastModified is { } modified) label += $" · {modified:yyyy-MM-dd}";
            }
            return label;
        }).ToArray();
        var pristine = labels.ToArray();
        for (var i = 0; i < labels.Length; i++)
        {
            if (pristine.Count(label => label == pristine[i]) <= 1) continue;
            labels[i] += $" (#{Enumerable.Range(0, i + 1).Count(j => pristine[j] == pristine[i])})";
        }
        return labels;
    }

    /// <summary>The quiet line under a destination: where the file lives, and for a copy that it is one.</summary>
    public static string Detail(DetectedSave save) =>
        save.IsCopy ? $"Backup copy · {Where(save)}" : Where(save);

    /// <summary>"backup/POKEMON_PL.sav · 2026-11-12": the file's folder, name and date.</summary>
    public static string Where(DetectedSave save)
    {
        var place = string.IsNullOrEmpty(save.FolderHint) ? save.FileName : $"{save.FolderHint}/{save.FileName}";
        return save.LastModified is { } modified ? $"{place} · {modified:yyyy-MM-dd}" : place;
    }
}
