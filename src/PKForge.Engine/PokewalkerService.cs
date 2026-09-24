using PKForge.Domain;
using PKHeX.Core;

namespace PKForge.Engine;

/// <summary>One Pokéwalker route.</summary>
/// <param name="Index">Course bit 0-26 (PKHeX <c>PokewalkerCourse4</c>).</param>
/// <param name="Unlocked">The save's unlock bit for this course.</param>
/// <param name="AvailableForLanguage">False for courses the game's language never unlocks
/// (SAV4HGSS.GetPossiblePokewalkerCourseUnlock: Rally/Amity Meadow Japanese only, Sightseeing JP/KR).</param>
/// <param name="Encounters">What the course offers, e.g. "Pikachu Lv.15", from PKHeX's walker table.</param>
public sealed record PokewalkerCourse(int Index, string Name, bool Unlocked, bool AvailableForLanguage,
    bool IsSpecial, IReadOnlyList<string> Encounters);

public sealed record PokewalkerState(uint Steps, uint Watts, IReadOnlyList<PokewalkerCourse> Courses);

/// <summary>
/// HeartGold/SoulSilver Pokéwalker, after the Walker tab of PKHeX's WinForms <c>SAV_Misc4</c>:
/// <c>SAV4HGSS.PokewalkerSteps</c>/<c>PokewalkerWatts</c> (General 0xE704/0xE708, NUD maxima 9,999,999)
/// and the 32 course bits at 0xE70C (<c>Get/SetPokewalkerCoursesUnlocked</c>). Course contents come
/// from PKHeX's own legality table (<c>encounter_walker4.pkl</c>, read by
/// <c>EncounterStatic4Pokewalker.GetAll</c>, six slots per course), so what we list is exactly what
/// PKHeX accepts as a legal Pokéwalker catch.
/// </summary>
public static class PokewalkerService
{
    /// <summary>SAV_Misc4 NUD_Steps/NUD_Watts Maximum.</summary>
    public const uint MaxCounter = 9_999_999;

    /// <summary>PokewalkerCourse4.MAX_COUNT: the 27 courses that exist.</summary>
    public const int CourseCount = (int)PokewalkerCourse4.MAX_COUNT;

    /// <summary>Courses from Beyond the Sea (20) on were given out by events, not by watts
    /// (PKHeX PokewalkerCourse4 marks Rally/Sightseeing/Amity Meadow as region exclusives).</summary>
    public const int FirstSpecialCourse = (int)PokewalkerCourse4.BeyondTheSea;

    private static readonly Lazy<EncounterStatic4Pokewalker[]> Table =
        new(() => EncounterStatic4Pokewalker.GetAll(Util.GetBinaryResource("encounter_walker4.pkl")));

    public static bool IsSupported(ISaveEngineSession session) => TryGetSave(session) is not null;

    public static PokewalkerState GetState(ISaveEngineSession session)
    {
        var save = Require(session);
        Span<bool> bits = stackalloc bool[SAV4HGSS.PokewalkerCourseFlagCount];
        save.GetPokewalkerCoursesUnlocked(bits);
        var possible = SAV4HGSS.GetPossiblePokewalkerCourseUnlock(save.Language);
        var names = GameInfo.GetStrings("en").walkercourses;
        var courses = new PokewalkerCourse[CourseCount];
        for (var i = 0; i < CourseCount; i++)
        {
            courses[i] = new PokewalkerCourse(i, i < names.Length ? names[i] : ((PokewalkerCourse4)i).ToString(),
                bits[i], (possible & (1u << i)) != 0, i >= FirstSpecialCourse, GetEncounters(i));
        }
        return new PokewalkerState(save.PokewalkerSteps, save.PokewalkerWatts, courses);
    }

    /// <summary>The six Pokémon a course can hand out, "Species Lv.N", from PKHeX's walker table.</summary>
    public static IReadOnlyList<string> GetEncounters(int course) => Table.Value
        .Where(slot => (int)slot.Course == course)
        .Select(slot => $"{SpeciesName.GetSpeciesName(slot.Species, (int)LanguageID.English)} Lv.{slot.Level}")
        .ToArray();

    public static PokewalkerState SetCounters(ISaveEngineSession session, uint steps, uint watts)
    {
        var save = Require(session);
        save.PokewalkerSteps = Math.Min(steps, MaxCounter);
        save.PokewalkerWatts = Math.Min(watts, MaxCounter);
        return GetState(session);
    }

    public static PokewalkerState SetCourse(ISaveEngineSession session, int course, bool unlocked)
    {
        var save = Require(session);
        ArgumentOutOfRangeException.ThrowIfNegative(course);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(course, CourseCount);
        Span<bool> bits = stackalloc bool[SAV4HGSS.PokewalkerCourseFlagCount];
        save.GetPokewalkerCoursesUnlocked(bits);
        bits[course] = unlocked;
        save.SetPokewalkerCoursesUnlocked(bits);
        return GetState(session);
    }

    /// <summary>Unlocks every course this game's language can unlock
    /// (<c>SAV4HGSS.GetPossiblePokewalkerCourseUnlock</c>). PKHeX's own "all" button writes the
    /// Japanese mask on every save; we keep the save to what its cartridge could have done.</summary>
    public static PokewalkerState UnlockAll(ISaveEngineSession session)
    {
        var save = Require(session);
        save.PokewalkerCoursesSetAll(SAV4HGSS.GetPossiblePokewalkerCourseUnlock(save.Language));
        return GetState(session);
    }

    private static SAV4HGSS? TryGetSave(ISaveEngineSession session) =>
        session is SaveEngineSession engine ? engine.SaveFile as SAV4HGSS : null;

    private static SAV4HGSS Require(ISaveEngineSession session) =>
        TryGetSave(session) ?? throw new NotSupportedException("The Pokéwalker pairs only with HeartGold and SoulSilver.");
}
