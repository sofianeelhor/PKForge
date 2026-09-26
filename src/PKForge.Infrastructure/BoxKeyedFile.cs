using System.Text.Json;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// A small box index → value map kept as JSON beside the bank index (box names, wallpapers).
/// Decoration, never data: written atomically, and a missing or unreadable file reads as empty.
/// </summary>
internal sealed class BoxKeyedFile
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<int, string> _values;

    public BoxKeyedFile(string bankRootDirectory, string fileName)
    {
        Directory.CreateDirectory(bankRootDirectory);
        _path = Path.Combine(bankRootDirectory, fileName);
        _values = Load(_path);
    }

    public string? Get(int box)
    {
        lock (_gate) return _values.TryGetValue(box, out var value) ? value : null;
    }

    /// <summary>Sets several values in one write; a blank value clears that box.</summary>
    public void SetMany(IEnumerable<(int Box, string? Value)> values)
    {
        lock (_gate)
        {
            foreach (var (box, value) in values)
            {
                if (string.IsNullOrWhiteSpace(value)) _values.Remove(box);
                else _values[box] = value.Trim();
            }
            Write();
        }
    }

    /// <summary>Follows <see cref="IBankService.RemapBoxes"/>: values move with their box; a deleted box's is dropped.</summary>
    public void Remap(BankBoxRemap remap)
    {
        ArgumentNullException.ThrowIfNull(remap);
        lock (_gate)
        {
            _values = _values
                .Where(v => remap.Map(v.Key) is not null)
                .ToDictionary(v => remap.Map(v.Key)!.Value, v => v.Value);
            Write();
        }
    }

    private void Write()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_values));
        File.Move(tmp, _path, overwrite: true);
    }

    private static Dictionary<int, string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // Decoration: an unreadable file means no values, not a broken bank.
        }
        return [];
    }
}
