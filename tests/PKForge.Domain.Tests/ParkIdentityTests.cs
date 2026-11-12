using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>One of each Pokémon in the park, friendships kept by who it is, an auto-fill size
/// up to the park's limit, and photos that can be deleted.</summary>
public sealed class ParkIdentityTests
{
    private sealed record Mon(string Id, string Soul);

    [Fact]
    public void The_identity_follows_the_pokemon_not_its_slot()
    {
        Assert.Equal(ParkIdentity.Soul(0x1234, 0xABCD, "Red", 1), ParkIdentity.Soul(0x1234, 0xABCD, "Red", 1));
        Assert.NotEqual(ParkIdentity.Soul(0x1234, 0xABCD, "Red", 1), ParkIdentity.Soul(0x1235, 0xABCD, "Red", 1));
        // Older formats have no EC: the trainer tells two equal PIDs apart.
        Assert.NotEqual(ParkIdentity.Soul(0x1234, null, "Red", 1), ParkIdentity.Soul(0x1234, null, "Blue", 1));
        Assert.StartsWith("soul:", ParkIdentity.Soul(1, null, null!, 0));
    }

    [Fact]
    public void A_pokemon_that_leaves_and_comes_back_keeps_its_friends()
    {
        var pika = ParkIdentity.Soul(25, 25, "Ash", 1);
        var bulba = ParkIdentity.Soul(1, 1, "Ash", 1);
        var kept = new[] { new ParkRelationship(pika, bulba, Friendship: 70, Rivalry: 5) };

        // It came back from another slot, so its visit id is new.
        var today = new Dictionary<string, string> { ["save:doc:3:7"] = pika, ["bank:abc"] = bulba };
        var relations = ParkIdentity.Relations(kept, today);

        Assert.Equal((70, 5), relations[ParkRelationship.KeyFor("save:doc:3:7", "bank:abc")]);
        Assert.Single(relations);
    }

    [Fact]
    public void Friendships_kept_by_visit_move_onto_the_identity()
    {
        var souls = new Dictionary<string, string> { ["save:a:0:1"] = "soul:p", ["save:a:0:1:FF"] = "soul:p", ["bank:b"] = "soul:b" };
        string SoulOf(string id) => souls.GetValueOrDefault(id, id);
        var old = new[]
        {
            new ParkRelationship("save:a:0:1", "bank:b", Friendship: 40, SharedMoments: 2),
            // The same Pokémon was in twice (auto-filled and invited): the closer friendship wins.
            new ParkRelationship("save:a:0:1:FF", "bank:b", Friendship: 90, SharedMoments: 9),
            new ParkRelationship("bank:gone", "bank:b", Friendship: 10),
        };

        var rekeyed = ParkIdentity.Rekey(old, SoulOf);

        Assert.Equal(2, rekeyed.Length);
        Assert.Equal(90, rekeyed.Single(r => r.Key == ParkRelationship.KeyFor("soul:p", "soul:b")).Friendship);
        Assert.Contains(rekeyed, r => r.Key == ParkRelationship.KeyFor("bank:gone", "soul:b"));
        Assert.Equal(rekeyed, ParkIdentity.Rekey(rekeyed, SoulOf));
    }

    [Fact]
    public void Auto_fill_never_picks_the_same_pokemon_twice()
    {
        // The same Pokémon reached from the save and from the Bank.
        var candidates = new[] { new Mon("save:1", "soul:a"), new Mon("bank:1", "soul:a"), new Mon("save:2", "soul:b") };
        for (var seed = 0; seed < 50; seed++)
        {
            var picked = ParkSelection.SelectBalanced(candidates, m => m.Soul, m => m.Id[..4], 3, new Random(seed));
            Assert.Equal(picked.Count, picked.Select(m => m.Soul).Distinct().Count());
            Assert.Equal(2, picked.Count);
        }
    }

    [Fact]
    public void Topping_up_keeps_everyone_and_adds_only_new_pokemon()
    {
        var current = new[] { new Mon("save:1", "soul:a"), new Mon("bank:9", "soul:z") };
        var candidates = Enumerable.Range(0, 40).Select(i => new Mon($"bank:{i}", $"soul:{i}"))
            .Append(new Mon("bank:copy", "soul:a")).ToArray();

        var filled = ParkSelection.TopUp(current, candidates, m => m.Soul, 10, new Random(3));

        Assert.Equal(10, filled.Count);
        Assert.Equal(current, filled.Take(2));
        Assert.Equal(10, filled.Select(m => m.Soul).Distinct().Count());
        Assert.DoesNotContain(filled, m => m.Id == "bank:copy");
    }

    [Fact]
    public void A_smaller_size_sends_nobody_away()
    {
        var current = Enumerable.Range(0, 8).Select(i => new Mon($"save:{i}", $"soul:{i}")).ToArray();
        Assert.Equal(current, ParkSelection.TopUp(current, [new Mon("bank:x", "soul:x")], m => m.Soul, 3));
    }

    [Fact]
    public void Auto_fill_can_bring_up_to_thirty()
    {
        Assert.Equal(30, ParkLife.MostResidents);
        var candidates = Enumerable.Range(0, 50).Select(i => new Mon($"bank:{i}", $"soul:{i}")).ToArray();
        Assert.Equal(30, ParkSelection.Select(candidates, m => m.Soul, true, 30, [], new Random(1)).Count);
        Assert.Equal(30, ParkSelection.SelectBalanced(candidates, m => m.Soul, m => "bank", 99, new Random(1)).Count);
        Assert.Equal(30, ParkSelection.TopUp([], candidates, m => m.Soul, 30, new Random(1)).Count);
    }

    [Fact]
    public void A_full_park_of_thirty_lives_without_trouble()
    {
        var roster = Enumerable.Range(0, ParkLife.MostResidents).Select(i => new ParkLifeResident($"r{i}", $"Mon{i}", 1 + i * 13,
            [ParkType.Normal], (ParkPersonality)(i % 10), i % 25)).ToList();
        var life = new ParkLife(ParkMap.Park, roster, 7) { Today = new DateOnly(2026, 10, 6) };
        for (long t = 0; t <= 20 * 60 * 1000; t += 200) { life.Advance(t); life.TakeMoments(); }
        Assert.Equal(ParkLife.MostResidents, life.Actors.Count(a => !ParkLife.IsVisitor(a.Resident.Id)));
    }

    [Fact]
    public void A_photo_can_be_deleted_with_its_caption()
    {
        var folder = Path.Combine(Path.GetTempPath(), "pkforge-album-" + Guid.NewGuid().ToString("N"));
        try
        {
            var album = new ParkAlbumStore(folder);
            album.Save([1, 2, 3], "First", new DateTime(2026, 1, 1, 10, 0, 0));
            album.Save([4, 5, 6], "Second", new DateTime(2026, 1, 2, 10, 0, 0));
            var photos = album.List();
            Assert.Equal(["Second", "First"], photos.Select(p => p.Caption));

            Assert.True(album.Delete(photos[0]));

            Assert.Equal(["First"], album.List().Select(p => p.Caption));
            Assert.False(File.Exists(Path.ChangeExtension(photos[0].Path, ".txt")));
            Assert.False(album.Delete(photos[0]));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Deleting_never_touches_a_file_outside_the_album()
    {
        var root = Path.Combine(Path.GetTempPath(), "pkforge-album-" + Guid.NewGuid().ToString("N"));
        var outside = Path.Combine(root, "other.jpg");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(outside, [1]);
            var album = new ParkAlbumStore(Path.Combine(root, "album"));
            Assert.False(album.Delete(new ParkAlbumStore.Photo(outside, "", DateTime.Now)));
            Assert.True(File.Exists(outside));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
