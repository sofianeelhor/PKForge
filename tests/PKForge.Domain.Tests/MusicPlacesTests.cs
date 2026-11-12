using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class MusicPlacesTests
{
    private static readonly MusicTrack[] Library = [new("a", "Route 1"), new("b", "Lavender Town"), new("c", "Ecruteak")];

    private static int[]? Playlist(MusicPlace place, Dictionary<MusicPlace, MusicSource> sources) =>
        MusicPlaces.Playlist(place, p => sources.GetValueOrDefault(p, MusicSource.Default), Library);

    [Fact]
    public void Menus_play_the_whole_library_and_the_park_follows_them_by_default()
    {
        Assert.Equal([0, 1, 2], Playlist(MusicPlace.Menus, [])!);
        Assert.Equal([0, 1, 2], Playlist(MusicPlace.ParkDay, [])!);
        var sources = new Dictionary<MusicPlace, MusicSource> { [MusicPlace.Menus] = MusicSource.Of(["b"]) };
        Assert.Equal([1], Playlist(MusicPlace.ParkNight, sources)!);
    }

    [Fact]
    public void Each_place_can_have_its_own_tracks_or_silence()
    {
        var sources = new Dictionary<MusicPlace, MusicSource>
        {
            [MusicPlace.Menus] = MusicSource.Silence,
            [MusicPlace.ParkDay] = MusicSource.Of(["c", "a"]),
            [MusicPlace.ParkNight] = MusicSource.AllTracks,
        };
        Assert.Null(Playlist(MusicPlace.Menus, sources));
        Assert.Equal([0, 2], Playlist(MusicPlace.ParkDay, sources)!);
        Assert.Equal([0, 1, 2], Playlist(MusicPlace.ParkNight, sources)!);
    }

    [Fact]
    public void Tracks_that_left_the_library_fall_back_on_the_default()
    {
        var sources = new Dictionary<MusicPlace, MusicSource>
        {
            [MusicPlace.Menus] = MusicSource.Of(["b"]),
            [MusicPlace.ParkDay] = MusicSource.Of(["gone"]),
        };
        Assert.Equal([1], Playlist(MusicPlace.ParkDay, sources)!);
    }

    [Fact]
    public void Sources_survive_a_round_trip_through_the_preferences()
    {
        foreach (var source in new[] { MusicSource.Default, MusicSource.AllTracks, MusicSource.Silence, MusicSource.Of(["a", "c"]) })
        {
            var back = MusicSource.Parse(source.Serialize());
            Assert.Equal(source.Kind, back.Kind);
            Assert.Equal(source.Tracks, back.Tracks);
        }
        Assert.Equal(MusicSourceKind.Default, MusicSource.Of([]).Kind);
        Assert.Equal("Same as menus", MusicPlaces.Describe(MusicPlace.ParkDay, MusicSource.Default, Library));
        Assert.Equal("Ecruteak", MusicPlaces.Describe(MusicPlace.ParkNight, MusicSource.Of(["c"]), Library));
    }

    [Fact]
    public void Home_boxes_and_bank_follow_the_menus_until_given_their_own_music()
    {
        foreach (var place in new[] { MusicPlace.Home, MusicPlace.Boxes, MusicPlace.Bank })
        {
            Assert.Equal([0, 1, 2], Playlist(place, [])!);
            Assert.Equal("Same as menus", MusicPlaces.Describe(place, MusicSource.Default, Library));
        }
        var sources = new Dictionary<MusicPlace, MusicSource> { [MusicPlace.Menus] = MusicSource.Of(["a"]), [MusicPlace.Bank] = MusicSource.Of(["c"]) };
        Assert.Equal([0], Playlist(MusicPlace.Home, sources)!);
        Assert.Equal([2], Playlist(MusicPlace.Bank, sources)!);
    }

    [Fact]
    public void A_shuffle_place_plays_the_whole_library_shuffled_whatever_the_order()
    {
        var sources = new Dictionary<MusicPlace, MusicSource> { [MusicPlace.Boxes] = MusicSource.Shuffle };
        MusicSource Of(MusicPlace p) => sources.GetValueOrDefault(p, MusicSource.Default);
        Assert.Equal([0, 1, 2], Playlist(MusicPlace.Boxes, sources)!);
        Assert.True(MusicPlaces.Shuffles(MusicPlace.Boxes, Of, MusicOrder.InOrder));
        Assert.False(MusicPlaces.Shuffles(MusicPlace.Home, Of, MusicOrder.InOrder));
        Assert.True(MusicPlaces.Shuffles(MusicPlace.Home, Of, MusicOrder.Shuffle));
        sources[MusicPlace.Menus] = MusicSource.Shuffle;
        Assert.True(MusicPlaces.Shuffles(MusicPlace.Bank, Of, MusicOrder.InOrder));
        Assert.Equal(MusicSourceKind.Shuffle, MusicSource.Parse(MusicSource.Shuffle.Serialize()).Kind);
        Assert.Equal("Shuffle", MusicPlaces.Describe(MusicPlace.Boxes, MusicSource.Shuffle, Library));
    }

    [Fact]
    public void Volume_steps_by_ten_within_bounds_and_full_volume_is_unchanged()
    {
        Assert.Equal(1f, MusicPlaces.Gain(MusicPlaces.DefaultVolume));
        Assert.Equal(0f, MusicPlaces.Gain(0));
        Assert.Equal(0.25f, MusicPlaces.Gain(50));
        Assert.Equal(100, MusicPlaces.StepVolume(100, 1));
        Assert.Equal(0, MusicPlaces.StepVolume(0, -1));
        Assert.Equal(70, MusicPlaces.StepVolume(80, -1));
        Assert.Equal(40, MusicPlaces.StepVolume(33, 1));
        Assert.Equal(100, MusicPlaces.ClampVolume(250));
    }
}
