using System.Text.Json;
using System.Text.Json.Serialization;

namespace PKForge.Infrastructure;

/// <summary>A personal Pokémon preset: a name and the batch lines it applies ("Level=100", "IV_SPE=0"…).</summary>
public sealed record MonPreset(string Id, string Name, IReadOnlyList<string> Lines);

/// <summary>Persistent personal Pokémon presets (the Presets screen's own cards), replaced
/// atomically after serialization, like the bag's <see cref="ItemPresetStore"/>.</summary>
public sealed class MonPresetStore(string path)
{
    public IReadOnlyList<MonPreset> Read() => File.Exists(path)
        ? [.. (JsonSerializer.Deserialize(File.ReadAllText(path), MonPresetJsonContext.Default.ListMonPreset)
            ?? throw new InvalidDataException("The Pokémon preset library is unreadable."))
            .Where(p => p is { Id: not null, Name: not null })
            .Select(p => p with { Lines = [.. (p.Lines ?? []).Where(l => l is not null)] })]
        : [];

    /// <summary>Whether pulled bytes are a preset library this store can read (the folder mirror's check).</summary>
    public static bool IsLibrary(byte[] bytes)
    {
        try { return JsonSerializer.Deserialize(bytes, MonPresetJsonContext.Default.ListMonPreset) is not null; }
        catch (Exception error) when (error is JsonException or NotSupportedException) { return false; }
    }

    public void Save(MonPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Trim().Length > 40)
            throw new ArgumentException("Use a preset name between 1 and 40 characters.");
        var lines = preset.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) throw new ArgumentException("A preset needs at least one line.");
        var presets = Read().ToList();
        if (presets.Any(p => p.Id != preset.Id && string.Equals(p.Name, preset.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("A preset with that name already exists. Choose another name.");
        var clean = preset with { Name = preset.Name.Trim(), Lines = lines };
        var index = presets.FindIndex(p => p.Id == preset.Id);
        if (index < 0) presets.Add(clean);
        else presets[index] = clean;
        Write(presets);
    }

    /// <summary>The presets of <paramref name="incoming"/> this library lacks (by id), added in one
    /// write; a name already taken gets a number. Existing presets are never changed.</summary>
    public int Merge(IReadOnlyList<MonPreset> incoming)
    {
        var presets = Read().ToList();
        var added = 0;
        foreach (var preset in incoming)
        {
            if (preset is null || string.IsNullOrWhiteSpace(preset.Id) || string.IsNullOrWhiteSpace(preset.Name)) continue;
            if (presets.Any(p => p.Id == preset.Id)) continue;
            var lines = (preset.Lines ?? []).Select(l => l?.Trim() ?? "").Where(l => l.Length > 0).ToArray();
            if (lines.Length == 0) continue;
            var baseName = preset.Name.Trim();
            var name = baseName;
            for (var n = 2; presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
                name = $"{baseName} ({n})";
            presets.Add(preset with { Name = name, Lines = lines });
            added++;
        }
        if (added > 0) Write(presets);
        return added;
    }

    public void Delete(string id) => Write(Read().Where(p => p.Id != id).ToArray());

    private void Write(IReadOnlyList<MonPreset> presets)
    {
        AtomicFile.WriteText(path, JsonSerializer.Serialize(presets.ToList(), MonPresetJsonContext.Default.ListMonPreset));
    }
}

[JsonSerializable(typeof(List<MonPreset>))]
internal partial class MonPresetJsonContext : JsonSerializerContext
{
}
