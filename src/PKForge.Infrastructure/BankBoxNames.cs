using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// Optional names for the bank's boxes, kept beside the bank index as <c>box-names.json</c>
/// (box index → name). Boxes without a name show their number. Written atomically; a
/// missing or unreadable file simply means "no names" - names are decoration, never data.
/// </summary>
public sealed class BankBoxNames(string bankRootDirectory)
{
    private readonly BoxKeyedFile _file = new(bankRootDirectory, "box-names.json");

    /// <summary>Raised after this device changed a name (not when another device's names are applied).</summary>
    public event EventHandler? Changed;

    /// <summary>The name given to <paramref name="box"/>, or null when it has none.</summary>
    public string? Get(int box) => _file.Get(box);

    /// <summary>Sets several names in one write; a blank name clears that box's name.</summary>
    public void SetMany(IEnumerable<(int Box, string? Name)> names)
    {
        _file.SetMany(names);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Names follow their boxes through <see cref="IBankService.RemapBoxes"/>.</summary>
    public void Remap(BankBoxRemap remap)
    {
        _file.Remap(remap);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reads the file again, after it was put back from a copy.</summary>
    public void Reload() => _file.Reload();

    /// <summary>Every box name, by box.</summary>
    public IReadOnlyDictionary<int, string> All() => _file.All();

    /// <summary>Replaces every name in one write.</summary>
    public void ReplaceAll(IReadOnlyDictionary<int, string> names) => _file.ReplaceAll(names);
}
