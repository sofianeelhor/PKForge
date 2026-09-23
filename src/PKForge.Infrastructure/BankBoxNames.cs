using System.Text.Json;

namespace PKForge.Infrastructure;

/// <summary>
/// Optional names for the bank's boxes, kept beside the bank index as <c>box-names.json</c>
/// (box index → name). Boxes without a name show their number. Written atomically; a
/// missing or unreadable file simply means "no names" - names are decoration, never data.
/// </summary>
public sealed class BankBoxNames
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<int, string> _names;

    public BankBoxNames(string bankRootDirectory)
    {
        Directory.CreateDirectory(bankRootDirectory);
        _path = Path.Combine(bankRootDirectory, "box-names.json");
        _names = Load(_path);
    }

    /// <summary>The name given to <paramref name="box"/>, or null when it has none.</summary>
    public string? Get(int box)
    {
        lock (_gate) return _names.TryGetValue(box, out var name) ? name : null;
    }

    /// <summary>Sets several names in one write; a blank name clears that box's name.</summary>
    public void SetMany(IEnumerable<(int Box, string? Name)> names)
    {
        lock (_gate)
        {
            foreach (var (box, name) in names)
            {
                if (string.IsNullOrWhiteSpace(name)) _names.Remove(box);
                else _names[box] = name.Trim();
            }
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_names));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private static Dictionary<int, string> Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<int, string>>(File.ReadAllText(path)) ?? [];
        }
        catch
        {
            // Names are decoration: an unreadable file means no names, not a broken bank.
        }
        return [];
    }
}
