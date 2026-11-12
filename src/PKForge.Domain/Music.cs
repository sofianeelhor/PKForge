namespace PKForge.Domain;

/// <summary>A track in the user's music library: display name + SAF document id.</summary>
public sealed record MusicTrack(string DocumentId, string Title);

/// <summary>Play order for the background music.</summary>
public enum MusicOrder { InOrder, Shuffle }

/// <summary>Where the player is: each place can have its own music. <see cref="Menus"/> is every
/// screen without music of its own; Home, the boxes and the Bank follow it until given their own.
/// Stored by name, so new places go at the end.</summary>
public enum MusicPlace { Menus, ParkDay, ParkNight, Home, Boxes, Bank }

/// <summary>What a place plays. <see cref="Default"/> is the whole library for the menus, and
/// whatever the menus play for every other place. <see cref="Shuffle"/> is the whole library
/// shuffled, whatever the global order.</summary>
public enum MusicSourceKind { Default, AllTracks, Tracks, Silence, Shuffle }

/// <summary>A place's music: a kind, and the chosen tracks (document ids) for <see cref="MusicSourceKind.Tracks"/>.</summary>
public sealed record MusicSource(MusicSourceKind Kind, IReadOnlyList<string> Tracks)
{
    public static readonly MusicSource Default = new(MusicSourceKind.Default, []);
    public static readonly MusicSource AllTracks = new(MusicSourceKind.AllTracks, []);
    public static readonly MusicSource Silence = new(MusicSourceKind.Silence, []);
    public static readonly MusicSource Shuffle = new(MusicSourceKind.Shuffle, []);

    public static MusicSource Of(IEnumerable<string> tracks)
    {
        var list = tracks.Distinct().ToArray();
        return list.Length == 0 ? Default : new(MusicSourceKind.Tracks, list);
    }

    public string Serialize() => Kind switch
    {
        MusicSourceKind.AllTracks => "all",
        MusicSourceKind.Silence => "silence",
        MusicSourceKind.Shuffle => "shuffle",
        MusicSourceKind.Tracks => "tracks\n" + string.Join("\n", Tracks),
        _ => "",
    };

    public static MusicSource Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return Default;
        if (text == "all") return AllTracks;
        if (text == "silence") return Silence;
        if (text == "shuffle") return Shuffle;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 && lines[0] == "tracks" ? Of(lines.Skip(1)) : Default;
    }
}

public static class MusicPlaces
{
    /// <summary>The library indices a place plays, in library order; null when the place is silent.
    /// The park falls back on the menus' music, and chosen tracks that left the library are skipped
    /// (none left: the place falls back as if nothing was chosen).</summary>
    public static int[]? Playlist(MusicPlace place, Func<MusicPlace, MusicSource> sourceOf, IReadOnlyList<MusicTrack> library)
    {
        var source = sourceOf(place);
        if (source.Kind == MusicSourceKind.Tracks)
        {
            var chosen = source.Tracks.ToHashSet();
            var indices = Enumerable.Range(0, library.Count).Where(i => chosen.Contains(library[i].DocumentId)).ToArray();
            if (indices.Length > 0) return indices;
            source = MusicSource.Default;
        }
        if (source.Kind == MusicSourceKind.Default && place != MusicPlace.Menus)
            return Playlist(MusicPlace.Menus, sourceOf, library);
        if (source.Kind == MusicSourceKind.Silence || library.Count == 0) return null;
        return Enumerable.Range(0, library.Count).ToArray();
    }

    /// <summary>Whether a place shuffles its music: a Shuffle source does, following the menus'
    /// when it has no music of its own; otherwise the global order decides.</summary>
    public static bool Shuffles(MusicPlace place, Func<MusicPlace, MusicSource> sourceOf, MusicOrder order)
    {
        var source = sourceOf(place);
        if (source.Kind == MusicSourceKind.Shuffle) return true;
        if (source.Kind == MusicSourceKind.Default && place != MusicPlace.Menus)
            return Shuffles(MusicPlace.Menus, sourceOf, order);
        return order == MusicOrder.Shuffle;
    }

    /// <summary>Music volume, 0 to 100 in steps of 10; anything else read from the preferences is clamped.</summary>
    public const int DefaultVolume = 100, VolumeStep = 10;

    public static int ClampVolume(int volume) => Math.Clamp(volume, 0, 100);

    /// <summary>The next volume step up or down, clamped to 0..100.</summary>
    public static int StepVolume(int volume, int direction) =>
        ClampVolume((ClampVolume(volume) + VolumeStep / 2) / VolumeStep * VolumeStep + Math.Sign(direction) * VolumeStep);

    /// <summary>The player's gain for a volume: perceived loudness is logarithmic, so the slider
    /// follows a square curve (50 sounds about half as loud, not barely quieter); 0 is silent.</summary>
    public static float Gain(int volume)
    {
        var v = ClampVolume(volume) / 100f;
        return v * v;
    }

    /// <summary>The place's music in a few words, for the menus.</summary>
    public static string Describe(MusicPlace place, MusicSource source, IReadOnlyList<MusicTrack> library)
    {
        switch (source.Kind)
        {
            case MusicSourceKind.Silence: return "Silence";
            case MusicSourceKind.AllTracks: return "All tracks";
            case MusicSourceKind.Shuffle: return "Shuffle";
            case MusicSourceKind.Tracks:
                var titles = library.Where(t => source.Tracks.Contains(t.DocumentId)).Select(t => t.Title).ToArray();
                if (titles.Length == 1) return titles[0];
                if (titles.Length > 1) return $"{titles.Length} tracks";
                break;
        }
        return place == MusicPlace.Menus ? "All tracks" : "Same as menus";
    }
}

/// <summary>
/// The background music player: pick mp3s once, they persist as the default library
/// across launches (totally optional). Play/pause, skip, order. Each place (the menus, the
/// park by day, the park by night) can play its own tracks. Implementations own the
/// platform media session; the domain stays engine-clean.
/// </summary>
public interface IMusicPlayer
{
    IReadOnlyList<MusicTrack> Library { get; }
    bool IsPlaying { get; }
    /// <summary>The player wants music (Play, or autostart), even where the place is silent.</summary>
    bool IsOn { get; }
    int? CurrentIndex { get; }
    MusicOrder Order { get; }
    MusicPlace Place { get; }

    /// <summary>Adds tracks from user-picked documents; returns how many were added.</summary>
    int Add(IReadOnlyList<PickedDocument> documents);
    void Remove(int index);
    void Clear();

    void Play();
    void Pause();
    void Skip();
    /// <summary>Sets play order and persists it.</summary>
    void SetOrder(MusicOrder order);
    /// <summary>Makes the current library the app's default startup music (or forgets it).</summary>
    void SetAutostart(bool on);
    bool Autostart { get; }

    MusicSource SourceFor(MusicPlace place);
    /// <summary>Sets and persists a place's music; heard at once when the player is there.</summary>
    void SetSource(MusicPlace place, MusicSource source);
    /// <summary>The player moved: the music fades to that place's, or carries on when it is the same.</summary>
    void EnterPlace(MusicPlace place);

    /// <summary>Music volume 0..100, persisted; <see cref="SetVolume"/> is heard at once.</summary>
    int Volume { get; }
    void SetVolume(int volume);
}
