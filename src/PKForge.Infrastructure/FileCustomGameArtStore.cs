using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// <see cref="ICustomGameArtStore"/> as flat PNG files in one private folder
/// (<c>game-art/emerald-rogue--hero.png</c>), each written atomically (temp file + replace).
/// </summary>
public sealed class FileCustomGameArtStore(string directory) : ICustomGameArtStore
{
    public string Directory => directory;

    public event Action<string>? Changed;

    public string? PathOf(string? key, GameArtKind kind)
    {
        if (!CustomGameArt.IsValidKey(key)) return null;
        var path = System.IO.Path.Combine(directory, CustomGameArt.FileName(key!, kind));
        return File.Exists(path) ? path : null;
    }

    public void Put(string key, GameArtKind kind, byte[] png)
    {
        if (!CustomGameArt.IsValidKey(key)) throw new ArgumentException($"\"{key}\" is not a game art key.", nameof(key));
        ArgumentNullException.ThrowIfNull(png);
        if (!CustomGameArt.LooksLikePng(png)) throw new InvalidDataException("Game art is stored as PNG only.");
        AtomicFile.Write(System.IO.Path.Combine(directory, CustomGameArt.FileName(key, kind)), png);
        Changed?.Invoke(key);
    }

    public void Remove(string key, GameArtKind kind)
    {
        if (PathOf(key, kind) is not { } path) return;
        File.Delete(path);
        Changed?.Invoke(key);
    }

    /// <summary>The PKForge folder's view of this store: private <c>key--kind.png</c> shown as
    /// <c>Games/key/kind.png</c>; a pulled file must be a well-named PNG.</summary>
    public static DirectoryMirrorSource MirrorSource(string directory, RecentlyRemovedStore removed, Func<DateTimeOffset>? clock = null) =>
        new(CustomGameArt.MirrorPrefix, directory, removed, clock, CustomGameArt.ToMirrorName, CustomGameArt.FromMirrorName,
            bytes => CustomGameArt.LooksLikePng(bytes));

    public bool HasAny(string key) => CustomGameArt.Kinds.Any(kind => PathOf(key, kind) is not null);
}
