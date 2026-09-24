namespace PKForge.Infrastructure;

/// <summary>A directory entry in whatever tree the host walks (SAF documents, a real file system in tests).</summary>
public readonly record struct TreeEntry<TNode>(string Name, bool IsDirectory, TNode Node);

/// <summary>A 3DS "main" save found in an SD-structured tree.</summary>
public readonly record struct ThreeDsSaveHit<TNode>(TNode File, string TitleId);

/// <summary>
/// The Citra-family SDMC save layout, shared by Azahar, Lime3DS and Citra MMJ because they all
/// inherit Citra's ArchiveSource_SDSaveData: &lt;user&gt;/sdmc/Nintendo 3DS/&lt;ID0&gt;/&lt;ID1&gt;/title/00040000/&lt;low&gt;/data/00000001/main.
/// Saves are plain files (Citra does not encrypt the emulated SD), so the byte format is identical
/// across forks; only where the user directory lives differs. The walker accepts a grant of any
/// level from the app files folder down to "Nintendo 3DS" itself.
/// </summary>
public static class ThreeDsSaveLayout
{
    public const string NintendoThreeDs = "Nintendo 3DS";

    /// <summary>
    /// Paths tried, in order, from the granted folder to "Nintendo 3DS":
    /// Azahar/Lime3DS user folder (sdmc/...), Citra MMJ app files folder (citra-emu/sdmc/...),
    /// the sdmc folder itself, and the MMJ Android/data package folder (files/citra-emu/sdmc/...).
    /// </summary>
    private static readonly string[][] ProbePaths =
    [
        ["sdmc", NintendoThreeDs],
        ["citra-emu", "sdmc", NintendoThreeDs],
        [NintendoThreeDs],
        ["files", "citra-emu", "sdmc", NintendoThreeDs],
    ];

    /// <summary>Last path segment of a SAF document id ("primary:Android/data/x/files" → "files", "root/citra-emu" → "citra-emu").</summary>
    public static string LastSegment(string documentId)
    {
        var path = documentId.Replace('\\', '/').TrimEnd('/');
        var cut = Math.Max(path.LastIndexOf('/'), path.LastIndexOf(':'));
        return cut >= 0 ? path[(cut + 1)..] : path;
    }

    /// <summary>Finds the "Nintendo 3DS" directory under a granted root, or the root itself when it is that folder.</summary>
    public static TNode? FindNintendoThreeDs<TNode>(TNode root, string rootName,
        Func<TNode, IEnumerable<TreeEntry<TNode>>> list, Action<string>? trace = null) where TNode : class
    {
        if (rootName == NintendoThreeDs) return root;
        foreach (var path in ProbePaths)
        {
            var current = root;
            foreach (var segment in path)
            {
                current = Child(current, segment, list);
                if (current is null) break;
            }
            if (current is not null)
            {
                trace?.Invoke($"3DS layout matched {string.Join('/', path)}");
                return current;
            }
        }
        trace?.Invoke("3DS layout: no sdmc/Nintendo 3DS under the granted folder");
        return null;
    }

    /// <summary>Every retail title's main save under a "Nintendo 3DS" directory.</summary>
    public static IEnumerable<ThreeDsSaveHit<TNode>> EnumerateMainSaves<TNode>(TNode nintendoThreeDs,
        Func<TNode, IEnumerable<TreeEntry<TNode>>> list, CancellationToken cancellationToken = default) where TNode : class
    {
        foreach (var id0 in list(nintendoThreeDs).Where(x => x.IsDirectory))
        foreach (var id1 in list(id0.Node).Where(x => x.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = Child(id1.Node, "title", list);
            var retail = title is null ? null : Child(title, "00040000", list);
            if (retail is null) continue;
            foreach (var game in list(retail).Where(x => x.IsDirectory))
            {
                var data = Child(game.Node, "data", list);
                var slot = data is null ? null : Child(data, "00000001", list);
                if (slot is null) continue;
                var main = list(slot).FirstOrDefault(x => !x.IsDirectory && x.Name == "main");
                if (main.Node is not null)
                    yield return new ThreeDsSaveHit<TNode>(main.Node, game.Name);
            }
        }
    }

    private static TNode? Child<TNode>(TNode parent, string name, Func<TNode, IEnumerable<TreeEntry<TNode>>> list)
        where TNode : class =>
        list(parent).FirstOrDefault(x => x.IsDirectory && x.Name == name).Node;
}
