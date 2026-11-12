namespace PKForge.Domain;

/// <summary>The Poképark photo album on disk: each photo is a JPEG named by when it was taken,
/// with its caption beside it in a .txt of the same name (newest first, the last 200).</summary>
public sealed class ParkAlbumStore(string folder)
{
    public sealed record Photo(string Path, string Caption, DateTime TakenAt);

    public const int Keep = 200;
    private const string Stamp = "yyyyMMdd-HHmmss-fff";

    /// <summary>Where the photos are kept.</summary>
    public string Folder { get; } = folder;

    /// <summary>Writes an encoded JPEG and its caption.</summary>
    public void Save(byte[] jpeg, string caption, DateTime takenAt)
    {
        Directory.CreateDirectory(Folder);
        var name = takenAt.ToString(Stamp, System.Globalization.CultureInfo.InvariantCulture);
        File.WriteAllBytes(System.IO.Path.Combine(Folder, name + ".jpg"), jpeg);
        File.WriteAllText(System.IO.Path.Combine(Folder, name + ".txt"), caption);
        foreach (var old in List().Skip(Keep)) Delete(old);
    }

    public IReadOnlyList<Photo> List()
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.GetFiles(Folder, "*.jpg").OrderByDescending(p => p, StringComparer.Ordinal).Select(path =>
        {
            var caption = System.IO.Path.ChangeExtension(path, ".txt");
            var stamp = System.IO.Path.GetFileNameWithoutExtension(path);
            var taken = DateTime.TryParseExact(stamp, Stamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var at) ? at : File.GetCreationTime(path);
            return new Photo(path, File.Exists(caption) ? File.ReadAllText(caption) : "", taken);
        }).ToArray();
    }

    /// <summary>Deletes one photo and its caption. Only files inside the album are touched;
    /// false when the photo was already gone or lives elsewhere.</summary>
    public bool Delete(Photo photo)
    {
        var full = System.IO.Path.GetFullPath(photo.Path);
        var root = System.IO.Path.GetFullPath(Folder);
        if (!string.Equals(System.IO.Path.GetDirectoryName(full), root.TrimEnd(System.IO.Path.DirectorySeparatorChar), StringComparison.Ordinal)
            || !full.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return false;
        File.Delete(full);
        var caption = System.IO.Path.ChangeExtension(full, ".txt");
        if (File.Exists(caption)) File.Delete(caption);
        return true;
    }
}
