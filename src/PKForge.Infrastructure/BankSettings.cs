using System.Text.Json;
using System.Text.Json.Nodes;
using PKForge.Domain;

namespace PKForge.Infrastructure;

/// <summary>
/// Bank-wide settings that follow the Bank between devices, kept beside its index as
/// <c>bank-settings.json</c>: one JSON object, so keys a newer version adds survive this one.
/// A missing or unreadable file reads as no settings.
/// </summary>
public sealed class BankSettings
{
    private const string LivingDexStartBoxKey = "livingDexStartBox";
    private readonly string _path;
    private readonly Lock _gate = new();
    private JsonObject _values;

    /// <summary>Raised after this device changed a setting (not when another device's settings are applied).</summary>
    public event EventHandler? Changed;

    public BankSettings(string bankRootDirectory)
    {
        Directory.CreateDirectory(bankRootDirectory);
        _path = Path.Combine(bankRootDirectory, "bank-settings.json");
        _values = Load(_path);
    }

    /// <summary>First Bank box of the living dex region, or null before a run created one.</summary>
    public int? LivingDexStartBox
    {
        get
        {
            lock (_gate)
                return _values[LivingDexStartBoxKey] is JsonValue value && value.TryGetValue<int>(out var box) && box >= 0 ? box : null;
        }
        set
        {
            lock (_gate)
            {
                if (value is { } box) _values[LivingDexStartBoxKey] = box;
                else _values.Remove(LivingDexStartBoxKey);
                Write();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The living dex region follows its box through <see cref="IBankService.RemapBoxes"/>.</summary>
    public void Remap(BankBoxRemap remap)
    {
        ArgumentNullException.ThrowIfNull(remap);
        if (LivingDexStartBox is { } start) LivingDexStartBox = remap.Map(start);
    }

    /// <summary>Every setting as one JSON object (what sync sends).</summary>
    public JsonElement ToJson()
    {
        lock (_gate) return JsonSerializer.SerializeToElement(_values);
    }

    /// <summary>Replaces every setting with another device's (what sync applies).</summary>
    public void Replace(JsonElement settings)
    {
        lock (_gate)
        {
            _values = JsonNode.Parse(settings.GetRawText()) as JsonObject ?? new JsonObject();
            Write();
        }
    }

    /// <summary>Reads the file again, after it was put back from a copy.</summary>
    public void Reload()
    {
        lock (_gate) _values = Load(_path);
    }

    private void Write()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, _values.ToJsonString());
        File.Move(tmp, _path, overwrite: true);
    }

    private static JsonObject Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject values) return values;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            // Settings are optional: an unreadable file means defaults.
        }
        return new JsonObject();
    }
}
