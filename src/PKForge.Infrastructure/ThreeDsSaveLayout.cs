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
/// level: a parent of the user folder (found by a bounded search), the app files folder, sdmc,
/// "Nintendo 3DS", or any folder below it down to a single game's. Names match in any case.
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
        if (Same(rootName, NintendoThreeDs)) return root;
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

    /// <summary>How far below the grant a "Nintendo 3DS" folder is searched for, and how many folders at most.</summary>
    private const int SearchDepth = 4;
    private const int SearchBudget = 400;

    /// <summary>
    /// Every main save reachable from the granted folder, whichever level it is: at or above
    /// "Nintendo 3DS" (known paths first, then a bounded search), or somewhere below it.
    /// </summary>
    public static IEnumerable<ThreeDsSaveHit<TNode>> FindMainSaves<TNode>(TNode root, string rootName,
        Func<TNode, IEnumerable<TreeEntry<TNode>>> list, Action<string>? trace = null, CancellationToken cancellationToken = default)
        where TNode : class
    {
        if (FindNintendoThreeDs(root, rootName, list, trace) is { } known) return EnumerateMainSaves(known, list, cancellationToken);
        if (Search(root, list, cancellationToken) is { } found)
        {
            trace?.Invoke("3DS layout: found Nintendo 3DS by searching below the granted folder");
            return EnumerateMainSaves(found, list, cancellationToken);
        }
        return Below(root, rootName, list, trace, cancellationToken);
    }

    /// <summary>Breadth-first, bounded: the nearest "Nintendo 3DS" folder under the grant.</summary>
    private static TNode? Search<TNode>(TNode root, Func<TNode, IEnumerable<TreeEntry<TNode>>> list, CancellationToken cancellationToken)
        where TNode : class
    {
        var level = new List<TNode> { root };
        var visited = 0;
        for (var depth = 0; depth < SearchDepth && level.Count > 0; depth++)
        {
            var next = new List<TNode>();
            foreach (var folder in level)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > SearchBudget) return null;
                foreach (var child in list(folder).Where(x => x.IsDirectory))
                {
                    if (Same(child.Name, NintendoThreeDs)) return child.Node;
                    next.Add(child.Node);
                }
            }
            level = next;
        }
        return null;
    }

    /// <summary>
    /// A grant below "Nintendo 3DS", recognised by its shape: an ID0 or ID1 folder, "title",
    /// "00040000", one game's folder, or its data folder.
    /// </summary>
    private static IEnumerable<ThreeDsSaveHit<TNode>> Below<TNode>(TNode root, string rootName,
        Func<TNode, IEnumerable<TreeEntry<TNode>>> list, Action<string>? trace, CancellationToken cancellationToken)
        where TNode : class
    {
        if (MainOf(root, list) is { } game)
        {
            trace?.Invoke("3DS layout: the grant is one game's folder");
            return [new ThreeDsSaveHit<TNode>(game, rootName)];
        }
        if (Same(rootName, "data") && Child(root, "00000001", list) is { } slot && MainIn(slot, list) is { } main)
        {
            trace?.Invoke("3DS layout: the grant is one game's data folder");
            return [new ThreeDsSaveHit<TNode>(main, "unknown")];
        }
        if (Same(rootName, "00040000")) return Games(root, list, cancellationToken);
        if (Child(root, "00040000", list) is { } retail) return Games(retail, list, cancellationToken);
        if (Child(root, "title", list) is not null) return FromId1(root, list, cancellationToken);
        var id1s = list(root).Where(x => x.IsDirectory && Child(x.Node, "title", list) is not null).ToList();
        if (id1s.Count > 0) return id1s.SelectMany(id1 => FromId1(id1.Node, list, cancellationToken));
        trace?.Invoke("3DS layout: nothing under the granted folder looks like a 3DS save tree");
        return [];
    }

    /// <summary>Every retail title's main save under a "Nintendo 3DS" directory.</summary>
    public static IEnumerable<ThreeDsSaveHit<TNode>> EnumerateMainSaves<TNode>(TNode nintendoThreeDs,
        Func<TNode, IEnumerable<TreeEntry<TNode>>> list, CancellationToken cancellationToken = default) where TNode : class
    {
        foreach (var id0 in list(nintendoThreeDs).Where(x => x.IsDirectory))
        foreach (var id1 in list(id0.Node).Where(x => x.IsDirectory))
        foreach (var hit in FromId1(id1.Node, list, cancellationToken))
            yield return hit;
    }

    private static IEnumerable<ThreeDsSaveHit<TNode>> FromId1<TNode>(TNode id1, Func<TNode, IEnumerable<TreeEntry<TNode>>> list,
        CancellationToken cancellationToken) where TNode : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var title = Child(id1, "title", list);
        var retail = title is null ? null : Child(title, "00040000", list);
        return retail is null ? [] : Games(retail, list, cancellationToken);
    }

    /// <summary>Retail titles live under 00040000/&lt;game&gt;/data/00000001/main.</summary>
    private static IEnumerable<ThreeDsSaveHit<TNode>> Games<TNode>(TNode retail, Func<TNode, IEnumerable<TreeEntry<TNode>>> list,
        CancellationToken cancellationToken) where TNode : class
    {
        foreach (var game in list(retail).Where(x => x.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MainOf(game.Node, list) is { } main)
                yield return new ThreeDsSaveHit<TNode>(main, game.Name);
        }
    }

    private static TNode? MainOf<TNode>(TNode game, Func<TNode, IEnumerable<TreeEntry<TNode>>> list) where TNode : class
    {
        var data = Child(game, "data", list);
        var slot = data is null ? null : Child(data, "00000001", list);
        return slot is null ? null : MainIn(slot, list);
    }

    private static TNode? MainIn<TNode>(TNode slot, Func<TNode, IEnumerable<TreeEntry<TNode>>> list) where TNode : class =>
        list(slot).FirstOrDefault(x => !x.IsDirectory && Same(x.Name, "main")).Node;

    private static TNode? Child<TNode>(TNode parent, string name, Func<TNode, IEnumerable<TreeEntry<TNode>>> list)
        where TNode : class =>
        list(parent).FirstOrDefault(x => x.IsDirectory && Same(x.Name, name)).Node;

    // Emulators and file managers do not agree on case ("Nintendo 3DS", "nintendo 3ds", "SDMC").
    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
