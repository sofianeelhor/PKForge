using System.IO.Compression;
using System.Text;
using PKHeX.Core;

namespace PKForge.App.Services;

/// <summary>
/// The projectpokemon EventsGallery, packed per generation at build time by
/// tools/EventGen (4.3k wondercards, ~280 KB). The matching generation is
/// extracted once per save and handed to PKHeX's local event tables (EGDB),
/// which the event service merges with the embedded database (MGDB).
/// </summary>
public static class EventArchive
{
    private static int _loadedGeneration = -1;
    private static readonly Lock Gate = new();

    public static void EnsureLoaded(int generation)
    {
        if (generation is < 4 or > 9) return;
        lock (Gate)
        {
            if (_loadedGeneration == generation) return;
            try
            {
                var target = Extract(generation);
                EncounterEvent.RefreshMGDB(target);
                _loadedGeneration = generation;
            }
            catch
            {
                // A missing or corrupt archive must never break the wonder card menu;
                // the embedded database still works.
                _loadedGeneration = -1;
            }
        }
    }

    private static string Extract(int generation)
    {
        var target = Path.Combine(FileSystem.CacheDirectory, "events", $"g{generation}");
        if (Directory.Exists(target) && Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Any())
            return target;
        Directory.CreateDirectory(target);

        using var asset = FileSystem.OpenAppPackageFileAsync($"events/events-g{generation}.bin.gz")
            .GetAwaiter().GetResult();
        using var gz = new GZipStream(asset, CompressionMode.Decompress);
        using var reader = new BinaryReader(gz, Encoding.UTF8, leaveOpen: true);
        var count = reader.ReadInt32();
        for (var i = 0; i < count; i++)
        {
            var nameLength = reader.ReadUInt16();
            var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
            var length = reader.ReadInt32();
            var data = reader.ReadBytes(length);

            var path = Path.GetFullPath(Path.Combine(target, name));
            if (!path.StartsWith(target, StringComparison.Ordinal)) continue; // path traversal guard
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
        return target;
    }
}
