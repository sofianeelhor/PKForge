using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PKForge.Domain;
using PKForge.Sync.Protocol;

namespace PKForge.Sync;

/// <summary>
/// How the local Bank becomes sync items (keys and JSON values) and back, and how two values are
/// compared: by a fingerprint of their canonical JSON, so formatting and escaping never look like edits.
/// </summary>
public static class SyncValues
{
    /// <summary>The fingerprint of an item that does not exist (never created, or released).</summary>
    public const string Deleted = "deleted";

    private static readonly JsonSerializerOptions Canonical = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static readonly string BoxesKey = Key(ItemKinds.Boxes, ItemKinds.SingletonId);
    public static readonly string SettingsKey = Key(ItemKinds.Settings, ItemKinds.SingletonId);

    public static string Key(string kind, string id) => $"{kind}:{id}";
    public static string MonKey(Guid id) => Key(ItemKinds.Mon, id.ToString("D"));

    public static (string Kind, string Id) SplitKey(string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        return (key[..colon], key[(colon + 1)..]);
    }

    public static bool IsMon(string key) => key.StartsWith(ItemKinds.Mon + ":", StringComparison.Ordinal);
    public static Guid MonId(string key) => Guid.Parse(SplitKey(key).Id);

    public static JsonElement Mon(BankEntry entry, string blob, DateTimeOffset movedUtc) =>
        JsonSerializer.SerializeToElement(
            new MonValue(entry.Box, entry.Slot, movedUtc, JsonSerializer.SerializeToElement(entry.Info, ProtocolJson.Options), entry.AddedUtc, blob),
            ProtocolJson.Options);

    public static MonValue ReadMon(JsonElement value) =>
        value.Deserialize<MonValue>(ProtocolJson.Options) ?? throw new InvalidDataException("Not a Pokémon value.");

    public static BankEntry ToEntry(Guid id, MonValue mon) =>
        new(id, mon.Box, mon.Slot,
            mon.Info.Deserialize<BankEntryInfo>(ProtocolJson.Options) ?? throw new InvalidDataException("A Pokémon value without its facts."),
            mon.AddedUtc);

    /// <summary>True when two Pokémon values differ only in where they sit (and when they were moved).</summary>
    public static bool SameExceptPosition(JsonElement a, JsonElement b)
    {
        var x = ReadMon(a);
        var y = ReadMon(b);
        return x.Blob == y.Blob && x.AddedUtc == y.AddedUtc && Fingerprint(x.Info) == Fingerprint(y.Info);
    }

    public static JsonElement Boxes(int count, IReadOnlyDictionary<int, string> names, IReadOnlyDictionary<int, string> wallpapers) =>
        JsonSerializer.SerializeToElement(new BoxesValue(count, Indexed(names, count), Indexed(wallpapers, count)), ProtocolJson.Options);

    public static BoxesValue ReadBoxes(JsonElement value) =>
        value.Deserialize<BoxesValue>(ProtocolJson.Options) ?? throw new InvalidDataException("Not a boxes value.");

    /// <summary>An indexed list (null = nothing set) back to a box → value map.</summary>
    public static Dictionary<int, string> FromIndexed(IReadOnlyList<string?>? values)
    {
        var result = new Dictionary<int, string>();
        for (var i = 0; i < (values?.Count ?? 0); i++)
        {
            if (!string.IsNullOrWhiteSpace(values![i])) result[i] = values[i]!;
        }
        return result;
    }

    public static string Fingerprint(JsonElement? value)
    {
        if (value is not { } element) return Deleted;
        var canonical = JsonSerializer.Serialize(JsonNode.Parse(element.GetRawText()), Canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static List<string?> Indexed(IReadOnlyDictionary<int, string> values, int count)
    {
        var length = values.Keys.Where(k => k >= 0 && k < count).DefaultIfEmpty(-1).Max() + 1;
        var list = new List<string?>(length);
        for (var i = 0; i < length; i++) list.Add(values.TryGetValue(i, out var value) ? value : null);
        return list;
    }
}
