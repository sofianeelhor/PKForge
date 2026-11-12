using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// Edge cases, by force: random parks of 1 to 30 residents drawn from every species (with the
/// lore's special ones often), random natures and personalities, day and night, the player
/// doing anything at any time, residents leaving and arriving mid-moment, needs restored from
/// weeks ago. The park must never throw, never put a Pokémon in a wall or a land walker in the
/// lake, never keep a partner, a ball or a disguise that points at nobody, and keep needs in range.
/// </summary>
public sealed class ParkStressTests
{
    private static readonly ParkMap Home = ParkMap.Park;
    private static readonly int[] Special = [25, 778, 39, 94, 113, 132, 129, 335, 336, 631, 632, 313, 314, 143, 54, 52, 185, 233, 35, 150, 130, 108, 79];
    private static readonly int[][] TypeSets =
    [
        [ParkType.Normal], [ParkType.Water], [ParkType.Fire], [ParkType.Electric], [ParkType.Ghost, ParkType.Poison],
        [ParkType.Grass, ParkType.Poison], [ParkType.Ice], [ParkType.Psychic], [ParkType.Bug, ParkType.Steel], [ParkType.Water, ParkType.Flying],
    ];

    private static ParkLifeResident Random(Random random, int index)
    {
        var species = random.Next(3) == 0 ? Special[random.Next(Special.Length)] : random.Next(1, 1026);
        var types = species switch { 25 => [ParkType.Electric], 129 => [ParkType.Water], 94 => [ParkType.Ghost, ParkType.Poison], _ => TypeSets[random.Next(TypeSets.Length)] };
        return new ParkLifeResident($"r{index}-{species}", $"Mon{index}", species, types,
            (ParkPersonality)random.Next(10), random.Next(4) == 0 ? null : random.Next(25));
    }

    public static IEnumerable<object[]> Many() => Enumerable.Range(100, 20).Select(i => new object[] { i });

    private static ParkReply Do(Action action) { action(); return ParkReply.None; }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    [InlineData(6)] [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)]
    [MemberData(nameof(Many))]
    public void Anything_can_happen_and_the_park_holds(int seed)
    {
        var random = new Random(seed);
        var next = 0;
        var roster = Enumerable.Range(0, random.Next(1, ParkLife.MostResidents + 1)).Select(_ => Random(random, next++)).ToList();
        var life = new ParkLife(Home, roster, seed) { Today = new DateOnly(2026, 10, 6) };
        life.Restore(roster.Select(r => new ParkCareState(r.Id, random.Next(101), random.Next(101), random.Next(101), random.Next(101),
            random.Next(101), DateTimeOffset.Now.AddDays(-random.Next(60)))), DateTimeOffset.Now);
        static int Stable(string text) { var hash = 17; foreach (var letter in text) hash = hash * 31 + letter; return hash & 0x7fffffff; }
        life.Relation = (a, b) => (Stable(a + b) % 200 - 100, Stable(b + a) % 100);
        // Half the parks have the daycare open with some guests, and old friends in the Wilds.
        if (seed % 2 == 0)
        {
            life.DaycareOpen = true;
            life.DaycareGuests = roster.Take(random.Next(0, 5)).Select(r => r.Id).ToArray();
            life.CanBreed = (a, b) => Stable(a + b) % 3 == 0;
            life.OldFriends = [new("f1", "Bulby", 1, 0, false, [ParkType.Grass], ParkPersonality.Gentle, "Lyra", "HeartGold", DateTimeOffset.Now.AddDays(-9), 4),
                new("f2", "Spark", 25, 0, true, [ParkType.Electric], ParkPersonality.Playful, "Red", "Yellow", DateTimeOffset.Now.AddDays(-400))];
        }
        var phases = Enum.GetValues<ParkDayPhase>();

        for (long t = 0; t <= 2 * 60 * 60 * 1000; t += 200)
        {
            if (t % 300_000 == 0) life.Phase = phases[random.Next(phases.Length)];
            if (random.Next(40) == 0 && life.Actors.Count > 0)
            {
                var target = life.Actors.ElementAt(random.Next(life.Actors.Count)).Resident;
                _ = random.Next(13) switch
                {
                    7 => life.StartGame((ParkGameKind)random.Next(5) is var kind && kind == ParkGameKind.Dig ? ParkGameKind.Spar : kind, target.Id),
                    8 => life.StartDig(target.Id, new ParkPoint(random.Next(0, 1008), random.Next(0, 1376))),
                    9 => life.Seek(new ParkPoint(random.Next(0, 1008), random.Next(0, 1376))) ? ParkReply.None : ParkReply.None,
                    10 => Do(() => life.Cheer(target.Id)),
                    11 => Do(() => life.Brush(random.Next(1, 60))),
                    12 => Do(random.Next(2) == 0 ? life.EndGame : life.Block),
                    0 => life.Command(target.Id, ParkCommand.Talk),
                    1 => life.Command(target.Id, ParkCommand.Relax),
                    2 => life.Command(target.Id, ParkCommand.Snack),
                    3 => life.Feed(target.Id, random.Next(5), random.Next(25)),
                    4 => life.Pet(target.Id),
                    5 => life.Fetch(target.Id, new ParkPoint(random.Next(-20, 1030), random.Next(-20, 1400))),
                    _ => life.Command(target.Id, ParkCommand.Play)
                };
            }
            // Residents leave and arrive in the middle of whatever they are doing.
            if (random.Next(600) == 0 && roster.Count > 1) roster.RemoveAt(random.Next(roster.Count));
            if (random.Next(600) == 0 && roster.Count < ParkLife.MostResidents) roster.Add(Random(random, next++));
            if (random.Next(300) == 0) life.SetResidents(roster);
            life.Advance(t);
            life.TakeMoments();

            var ids = life.Actors.Select(a => a.Resident.Id).ToHashSet();
            foreach (var actor in life.Actors)
            {
                if (!actor.Indoors && !actor.InFlight && !actor.Riding)
                    Assert.True(Home.CanOccupy(actor.X, actor.Y, actor.Resident.Traversal, ParkNavigator.Radius - .5f),
                        $"seed {seed}, {t} ms: {actor.Resident.Species} on {Home.TerrainAt(actor.X, actor.Y)} at {actor.X},{actor.Y} ({actor.Activity})");
                Assert.True(actor.PartnerId is null || ids.Contains(actor.PartnerId), $"seed {seed}: partner {actor.PartnerId} is gone");
                foreach (var need in new[] { actor.Care.Hunger, actor.Care.Energy, actor.Care.Fun, actor.Care.Social, actor.Care.Bond })
                    Assert.InRange(need, 0, 100);
            }
            if (life.Ball is { } ball) Assert.True(ids.Contains(ball.OwnerId), $"seed {seed}: the ball's owner left");
            if (life.Game is { } game) Assert.All(game.Players, id => Assert.Contains(id, ids));
            foreach (var actor in life.Actors)
                Assert.True(!actor.InGame && !actor.Hidden || life.Game?.Players.Contains(actor.Resident.Id) == true,
                    $"seed {seed}: {actor.Resident.Id} is still playing a game that is over (game {life.Game?.Kind} {life.Game?.Stage} players {string.Join(",", life.Game?.Players ?? [])}, hidden {actor.Hidden} ingame {actor.InGame}, t {t})");
            Assert.True(life.Effects.Count < 200);
        }
    }
}
