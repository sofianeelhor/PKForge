using System.Text.Json;
using System.Text.Json.Serialization;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>Persistent personal bag presets, replaced atomically after serialization.</summary>
public sealed class ItemPresetStore(string path)
{
    public IReadOnlyList<ItemPreset> Read() => File.Exists(path)
        ? JsonSerializer.Deserialize(File.ReadAllText(path), ItemPresetJsonContext.Default.ListItemPreset)
            ?? throw new InvalidDataException("The item preset library is unreadable.")
        : [];

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

    private void Write(IReadOnlyList<ItemPreset> presets)
    {
        var json = JsonSerializer.Serialize(presets.ToList(), ItemPresetJsonContext.Default.ListItemPreset);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".item-presets-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

[JsonSerializable(typeof(List<ItemPreset>))]
internal partial class ItemPresetJsonContext : JsonSerializerContext
{
}
