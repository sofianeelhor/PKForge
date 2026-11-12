using System.Globalization;
using Android.Content;
using PKForge.Domain;
using PKForge.Infrastructure;

namespace PKForge.App.Services;

/// <summary>
/// The app side of moving PKForge's data to another device: reads and writes every stored
/// preference with its exact type, builds the archive, keeps a safety archive before an
/// import, and remembers the folders to link again.
/// </summary>
public static class AppDataTransfer
{
    private const int SafetyArchivesKept = 3;

    private static string Root => FileSystem.AppDataDirectory;

    /// <summary>Safety archives live beside the data but outside what an archive holds.</summary>
    public static string SafetyFolder => Path.Combine(Root, "data-safety");

    // MAUI keeps Preferences.Default in the app's default shared preferences file.
    private static ISharedPreferences Store =>
        Android.App.Application.Context.GetSharedPreferences($"{Android.App.Application.Context.PackageName}_preferences", FileCreationMode.Private)
        ?? throw new InvalidOperationException("The app preferences cannot be opened.");

    public static IReadOnlyList<PreferenceValue> ReadPreferences()
    {
        var values = new List<PreferenceValue>();
        foreach (var (key, value) in Store.All ?? new Dictionary<string, object>())
        {
            switch (value)
            {
                case Java.Lang.String text: values.Add(new(key, "string", text.ToString())); break;
                case string text: values.Add(new(key, "string", text)); break;
                case Java.Lang.Boolean flag: values.Add(new(key, "bool", flag.BooleanValue() ? "true" : "false")); break;
                case Java.Lang.Integer number: values.Add(new(key, "int", number.IntValue().ToString(CultureInfo.InvariantCulture))); break;
                case Java.Lang.Long number: values.Add(new(key, "long", number.LongValue().ToString(CultureInfo.InvariantCulture))); break;
                case Java.Lang.Float number: values.Add(new(key, "float", number.FloatValue().ToString("R", CultureInfo.InvariantCulture))); break;
                default: AppLog.Warn("transfer", $"Preference {key} has a type an archive does not carry; left out"); break;
            }
        }
        return [.. values.OrderBy(v => v.Key, StringComparer.Ordinal)];
    }

    /// <summary>Writes the preferences an import applies, each with its own type, in one commit.</summary>
    public static void ApplyPreferences(IReadOnlyList<PreferenceValue> values)
    {
        using var editor = Store.Edit() ?? throw new InvalidOperationException("The app preferences cannot be written.");
        foreach (var value in DataTransfer.ToApply(values))
        {
            _ = value.Type switch
            {
                "bool" => editor.PutBoolean(value.Key, value.Value == "true"),
                "int" => editor.PutInt(value.Key, int.Parse(value.Value, CultureInfo.InvariantCulture)),
                "long" => editor.PutLong(value.Key, long.Parse(value.Value, CultureInfo.InvariantCulture)),
                "float" => editor.PutFloat(value.Key, float.Parse(value.Value, CultureInfo.InvariantCulture)),
                _ => editor.PutString(value.Key, value.Value),
            };
        }
        if (!editor.Commit()) throw new IOException("The settings could not be saved.");
    }

    /// <summary>Writes an archive of everything here to <paramref name="path"/>.</summary>
    public static DataArchiveManifest ExportTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        DataArchiveManifest manifest;
        using (var output = File.Create(temporary))
            manifest = DataArchive.Export(output, Root, ReadPreferences(), AppInfo.Current.VersionString, DateTimeOffset.UtcNow);
        File.Move(temporary, path, overwrite: true);
        return manifest;
    }

    /// <summary>An archive in the cache, for the share sheet.</summary>
    public static string ExportForSharing()
    {
        var path = Path.Combine(FileSystem.CacheDirectory, "export", $"pkforge-data-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        foreach (var old in Directory.Exists(Path.GetDirectoryName(path)) ? Directory.GetFiles(Path.GetDirectoryName(path)!) : [])
            File.Delete(old);
        ExportTo(path);
        return path;
    }

    /// <summary>Saves everything here before an import; the newest few are kept.</summary>
    public static string WriteSafetyArchive()
    {
        var path = Path.Combine(SafetyFolder, $"before-import-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        ExportTo(path);
        foreach (var old in Directory.GetFiles(SafetyFolder, "before-import-*.zip").OrderDescending(StringComparer.Ordinal).Skip(SafetyArchivesKept))
            File.Delete(old);
        return path;
    }

    /// <summary>Folders an import brought by name, waiting to be linked on this device.</summary>
    public static IReadOnlyList<WatchedRoot> ToRelink => DataTransfer.ReadRoots(Preferences.Default.Get(DataTransfer.RelinkKey, string.Empty));

    public static void SetToRelink(IReadOnlyList<WatchedRoot> roots)
    {
        if (roots.Count == 0) Preferences.Default.Remove(DataTransfer.RelinkKey);
        else Preferences.Default.Set(DataTransfer.RelinkKey, System.Text.Json.JsonSerializer.Serialize(roots.ToList()));
    }

    /// <summary>Opens a picked archive: copied first, since a zip needs a stream it can seek.</summary>
    public static async Task<(DataArchiveReader Reader, Stream File)> OpenAsync(FileResult pick)
    {
        var copy = Path.Combine(FileSystem.CacheDirectory, "import", "archive.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        await using (var source = await pick.OpenReadAsync())
        await using (var target = File.Create(copy))
            await source.CopyToAsync(target);
        var stream = File.OpenRead(copy);
        try { return (DataArchive.Open(stream), stream); }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}
