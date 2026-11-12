using PKForge.Domain;

namespace PKForge.App.Services;

/// <summary>The Poképark photo album: photos the player takes of the park, each with what was
/// happening, kept in the app's own private data folder (newest first, the last 200). Other apps
/// cannot see that folder: Share sends a copy of a photo out.</summary>
public static class PokeparkAlbum
{
    private static readonly ParkAlbumStore Store = new(System.IO.Path.Combine(FileSystem.AppDataDirectory, "pokepark-album"));

    /// <summary>Where the photos are kept.</summary>
    public static string Folder => Store.Folder;

    /// <summary>Writes an encoded JPEG and its caption.</summary>
    public static void Save(byte[] jpeg, string caption, DateTime takenAt) => Store.Save(jpeg, caption, takenAt);

    public static IReadOnlyList<ParkAlbumStore.Photo> List() => Store.List();

    /// <summary>Deletes one photo and its caption.</summary>
    public static bool Delete(ParkAlbumStore.Photo photo) => Store.Delete(photo);
}
