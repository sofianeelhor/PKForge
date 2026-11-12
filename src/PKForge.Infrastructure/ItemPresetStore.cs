using System.Text.Json;
using System.Text.Json.Serialization;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>Persistent personal bag presets, replaced atomically after serialization.</summary>
public sealed class ItemPresetStore(string path)
{
    public IReadOnlyList<ItemPreset> Read() => File.Exists(path)
        ? Clean(JsonSerializer.Deserialize(File.ReadAllText(path), ItemPresetJsonContext.Default.ListItemPreset)
            ?? throw new InvalidDataException("The item preset library is unreadable."))
        : [];

    /// <summary>Whether pulled bytes are a preset library this store can read (the folder mirror's check).</summary>
    public static bool IsLibrary(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize(bytes, ItemPresetJsonContext.Default.ListItemPreset) is not null; }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return false; }
    }

    /// <summary>Presets as JSON may lack parts (a null entry, no items): broken ones are dropped, missing lists read as empty.</summary>
    internal static IReadOnlyList<ItemPreset> Clean(IEnumerable<ItemPreset?> presets) =>
        [.. presets.Where(p => p is { Id: not null, Name: not null }).Select(p => p! with { Items = [.. (p.Items ?? []).Where(i => i is not null)] })];

    public void Save(ItemPreset preset)
    {
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Trim().Length > 60)
            throw new ArgumentException("Use a preset name between 1 and 60 characters.");
        var presets = Read().ToList();
        if (presets.Any(p => p.Id != preset.Id && string.Equals(p.Name, preset.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A preset with that name already exists. Choose another name.");
        var index = presets.FindIndex(p => p.Id == preset.Id);
        if (index < 0) presets.Add(preset with { Name = preset.Name.Trim() });
        else presets[index] = preset with { Name = preset.Name.Trim() };
        Write(presets);
    }

    public void Delete(string id) => Write(Read().Where(p => p.Id != id).ToArray());

    /// <summary>The presets of <paramref name="incoming"/> this library lacks (by id), added in one
    /// write; a name already taken gets a number. Existing presets are never changed.</summary>
    public int Merge(IReadOnlyList<ItemPreset> incoming)
    {
        var presets = Read().ToList();
        var added = 0;
        foreach (var preset in Clean(incoming).Where(p => !string.IsNullOrWhiteSpace(p.Name)))
        {
            if (presets.Any(p => p.Id == preset.Id)) continue;
            var name = preset.Name.Trim();
            for (var n = 2; presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
                name = $"{preset.Name.Trim()} ({n})";
            presets.Add(preset with { Name = name });
            added++;
        }
        if (added > 0) Write(presets);
        return added;
    }

    private void Write(IReadOnlyList<ItemPreset> presets)
    {
        AtomicFile.WriteText(path, JsonSerializer.Serialize(presets.ToList(), ItemPresetJsonContext.Default.ListItemPreset));
    }
}

[JsonSerializable(typeof(List<ItemPreset>))]
internal partial class ItemPresetJsonContext : JsonSerializerContext
{
}
