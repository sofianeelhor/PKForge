using Android.Content;
using Android.Media;
using PKForge.Domain;

namespace PKForge.App.Platforms.Android;

/// <summary>
/// MediaPlayer-based background music: one platform player, gapless-enough track
/// advancement on completion, persisted library + order + autostart in Preferences.
/// Tracks live as SAF document ids (read via ContentResolver), never copied.
/// </summary>
public sealed class MusicPlayer : IMusicPlayer, IDisposable
{
    private MediaPlayer? _player;
    private readonly List<MusicTrack> _library = [];
    private readonly Random _rng = new();
    private int? _index;

    public IReadOnlyList<MusicTrack> Library => _library;
    public bool IsPlaying => _player?.IsPlaying == true;
    public int? CurrentIndex => _index;
    public MusicOrder Order
    {
        get => Enum.Parse<MusicOrder>(Preferences.Default.Get("music_order", nameof(MusicOrder.InOrder)));
        set => Preferences.Default.Set("music_order", value.ToString());
    }
    public bool Autostart
    {
        get => Preferences.Default.Get("music_autostart", false);
        set => Preferences.Default.Set("music_autostart", value);
    }

    public MusicPlayer()
    {
        var saved = Preferences.Default.Get("music_library", "");
        if (saved.Length > 0)
            foreach (var entry in saved.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = entry.Split('\u0001');
                if (parts.Length == 2) _library.Add(new MusicTrack(parts[0], parts[1]));
            }
    }

    public int Add(IReadOnlyList<PickedDocument> documents)
    {
        var added = 0;
        foreach (var document in documents)
        {
            if (_library.Any(t => t.DocumentId == document.DocumentId)) continue;
            _library.Add(new MusicTrack(document.DocumentId, document.DisplayName));
            added++;
        }
        Persist();
        return added;
    }

    public void Remove(int index)
    {
        if ((uint)index >= _library.Count) return;
        if (_index == index) { StopInternal(); _index = null; }
        else if (_index > index) _index--;
        _library.RemoveAt(index);
        Persist();
    }

    public void Clear()
    {
        StopInternal();
        _library.Clear();
        _index = null;
        Persist();
    }

    public void Play()
    {
        if (_library.Count == 0) return;
        _index ??= 0;
        PlayIndex(_index.Value);
    }

    public void Pause() => _player?.Pause();

    public void Skip()
    {
        if (_library.Count == 0) return;
        var next = Order == MusicOrder.Shuffle
            ? _rng.Next(_library.Count)
            : ((_index ?? 0) + 1) % _library.Count;
        _index = next;
        PlayIndex(next);
    }

    public void SetOrder(MusicOrder order) => Order = order;

    public void SetAutostart(bool on) => Autostart = on;

    /// <summary>Starts playback at app launch when the user asked for it.</summary>
    public void MaybeAutostart()
    {
        if (Autostart && _library.Count > 0) Play();
    }

    private void PlayIndex(int index)
    {
        StopInternal();
        var track = _library[index];
        try
        {
            var context = global::Android.App.Application.Context ?? throw new InvalidOperationException("No app context.");
            _player = new MediaPlayer();
            var attrs = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Music)!
                .Build()!;
            _player.SetAudioAttributes(attrs);
            _player.SetDataSource(context, global::Android.Net.Uri.Parse("content://" + track.DocumentId)!);
            _player.Prepare();
            _player.Start();
            _player.Completion += (_, _) => Skip();
        }
        catch
        {
            // Unplayable track: skip forward instead of dying.
            if (_library.Count > 1) Skip();
        }
    }

    private void StopInternal()
    {
        if (_player is null) return;
        _player.Completion -= null;
        _player.Stop();
        _player.Release();
        _player = null;
    }

    private void Persist() =>
        Preferences.Default.Set("music_library", string.Join("\n", _library.Select(t => $"{t.DocumentId}\u0001{t.Title}")));

    public void Dispose() => StopInternal();
}
