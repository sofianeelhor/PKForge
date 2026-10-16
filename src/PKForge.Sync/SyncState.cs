using System.Text.Json;
using PKForge.Sync.Protocol;

namespace PKForge.Sync;

/// <summary>The server version and fingerprint of an item as this device last saw it in sync.</summary>
public sealed record KnownItem(long Version, string Fingerprint);

/// <summary>Items one Bank operation touched; pushed together so the server applies them all or none.</summary>
public sealed class PendingGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public List<string> Keys { get; set; } = [];
    /// <summary>Only automatic fact rewrites: on conflict the other device's version wins.</summary>
    public bool Background { get; set; }
    /// <summary>Sent at least once: its id may already have a receipt, so its contents never change.</summary>
    public bool Sent { get; set; }
}

public enum ConflictKind { BothEdited, EditedHereReleasedThere, ReleasedHereEditedThere, Boxes, Settings }

public enum ConflictChoice { KeepThisDevice, KeepOtherDevice, KeepBoth }

/// <summary>A change the person decides about. Until then this device keeps <see cref="Local"/>.</summary>
public sealed record SyncConflict(Guid Id, string Key, ConflictKind Kind, JsonElement? Local, SyncItem Server, DateTimeOffset FoundUtc);

public sealed record ActivityEntry(DateTimeOffset Utc, string Message);

/// <summary>Everything this device remembers about syncing its Bank.</summary>
public sealed class SyncStateData
{
    public const int ActivityLimit = 200;

    public string? ServerUrl { get; set; }
    public Guid AccountId { get; set; }
    public Guid DeviceId { get; set; }
    /// <summary>The local Bank has been merged with (or replaced by) the server's.</summary>
    public bool Joined { get; set; }
    /// <summary>What was synced is unknown (unreadable state): rebuild it from a full download.</summary>
    public bool NeedsResync { get; set; }
    public long Cursor { get; set; }
    public string? Epoch { get; set; }
    /// <summary>When a sync last finished.</summary>
    public DateTimeOffset? LastSyncedUtc { get; set; }
    /// <summary>The server's display name, as it described itself when this device connected (null: show the host).</summary>
    public string? ServerName { get; set; }
    /// <summary>The server no longer knows this device's key (removed from the account): sign in again.</summary>
    public bool DeviceRemoved { get; set; }
    public Dictionary<string, KnownItem> Known { get; set; } = [];
    /// <summary>SHA-256 of each local Pokémon's bytes, so they are read only when they change.</summary>
    public Dictionary<Guid, string> BlobHashes { get; set; } = [];
    /// <summary>When this device last moved each Pokémon (absent: when it was added).</summary>
    public Dictionary<Guid, DateTimeOffset> MovedUtc { get; set; } = [];
    public List<PendingGroup> Pending { get; set; } = [];
    /// <summary>Fingerprints pushed but not yet acknowledged: a conflict with one of them is our own write.</summary>
    public Dictionary<string, List<string>> InFlight { get; set; } = [];
    /// <summary>Server changes not applied because this device had changed the item too.</summary>
    public Dictionary<string, SyncItem> Behind { get; set; } = [];
    /// <summary>Values the server refused, by fingerprint: not sent again until they change.</summary>
    public Dictionary<string, string> Refused { get; set; } = [];
    /// <summary>Why each refused item was refused (an <see cref="ErrorCodes"/> value), so the refusal can lift when its cause does.</summary>
    public Dictionary<string, string> RefusedCodes { get; set; } = [];
    public List<SyncConflict> Conflicts { get; set; } = [];
    public List<ActivityEntry> Activity { get; set; } = [];

    /// <summary>Notes items one operation changed, merging with any unsent group that shares an item.</summary>
    public void RecordChange(IReadOnlyCollection<string> keys, bool background)
    {
        if (keys.Count == 0) return;
        var merged = new HashSet<string>(keys);
        var allBackground = background;
        // A merged group keeps the place of its earliest member: order matters when a box is added
        // before a Pokémon is put in it.
        var position = -1;
        for (var i = Pending.Count - 1; i >= 0; i--)
        {
            var group = Pending[i];
            if (group.Sent || !group.Keys.Any(merged.Contains)) continue;
            merged.UnionWith(group.Keys);
            allBackground &= group.Background;
            Pending.RemoveAt(i);
            position = i;
        }
        var combined = new PendingGroup { Keys = [.. merged], Background = allBackground };
        if (position < 0) Pending.Add(combined);
        else Pending.Insert(position, combined);
    }

    /// <summary>The server holds <paramref name="fingerprint"/> at <paramref name="version"/>, and this device knows it.</summary>
    public void Adopt(string key, long version, string fingerprint)
    {
        Known[key] = new KnownItem(version, fingerprint);
        InFlight.Remove(key);
        Behind.Remove(key);
        Refused.Remove(key);
        RefusedCodes.Remove(key);
        // Refusals lift with their cause: a synced box layout may make room, a release frees fair-use space.
        var lifted = key == SyncValues.BoxesKey ? ErrorCodes.InvalidItem
            : SyncValues.IsMon(key) && fingerprint == SyncValues.Deleted ? ErrorCodes.QuotaExceeded
            : null;
        if (lifted is null) return;
        foreach (var refused in RefusedCodes.Where(r => r.Value == lifted).Select(r => r.Key).ToList())
        {
            Refused.Remove(refused);
            RefusedCodes.Remove(refused);
        }
    }

    public void AddInFlight(string key, string fingerprint)
    {
        if (!InFlight.TryGetValue(key, out var sent)) InFlight[key] = sent = [];
        if (!sent.Contains(fingerprint)) sent.Add(fingerprint);
    }

    public bool IsInFlight(string key, string fingerprint) => InFlight.TryGetValue(key, out var sent) && sent.Contains(fingerprint);

    public void AddActivity(DateTimeOffset utc, string message)
    {
        Activity.Add(new ActivityEntry(utc, message));
        if (Activity.Count > ActivityLimit) Activity.RemoveRange(0, Activity.Count - ActivityLimit);
    }
}

/// <summary>
/// Keeps <see cref="SyncStateData"/> in <c>sync-state.json</c>, written atomically (previous copy kept as
/// .bak). Frequent small changes can wait for <see cref="Flush"/>; nothing is lost by waiting, because
/// the engine finds unsynced changes by comparing the Bank with what was last synced.
/// </summary>
public sealed class SyncStateStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private SyncStateData _state;
    private bool _unsaved;

    public SyncStateStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "sync-state.json");
        _state = Load(_path);
    }

    public T Read<T>(Func<SyncStateData, T> read)
    {
        lock (_gate) return read(_state);
    }

    public void Update(Action<SyncStateData> change, bool persist = true)
    {
        lock (_gate)
        {
            change(_state);
            _unsaved = true;
            if (persist) Save();
        }
    }

    public T Update<T>(Func<SyncStateData, T> change, bool persist = true)
    {
        lock (_gate)
        {
            var result = change(_state);
            _unsaved = true;
            if (persist) Save();
            return result;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_unsaved) Save();
        }
    }

    /// <summary>Forgets everything (sign out).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _state = new SyncStateData();
            Save();
        }
    }

    private void Save()
    {
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, ProtocolJson.Options));
        if (File.Exists(_path)) File.Copy(_path, _path + ".bak", overwrite: true);
        File.Move(tmp, _path, overwrite: true);
        _unsaved = false;
    }

    private static SyncStateData Load(string path)
    {
        var existed = false;
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                existed = true;
                if (JsonSerializer.Deserialize<SyncStateData>(File.ReadAllText(candidate), ProtocolJson.Options) is { } state) return state;
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
            {
                // Try the backup next.
            }
        }
        return new SyncStateData { NeedsResync = existed };
    }
}
