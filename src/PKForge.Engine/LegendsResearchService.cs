using PKForge.Domain;
using PKHeX.Core;
using static PKHeX.Core.PokedexResearchTaskType8a;

namespace PKForge.Engine;

/// <summary>One research task of a Legends: Arceus species, as the game's Pokédex page lists it.</summary>
/// <param name="Value">The current count (unreported progress included).</param>
/// <param name="Thresholds">The counts that earn each level of the task, lowest first.</param>
/// <param name="ReportedLevel">Levels already reported to the professor (0 when none).</param>
/// <param name="CanSet">False for tasks the save derives from elsewhere (forms obtained, quests, the Arceus task).</param>
public sealed record ResearchTask(int Index, string Label, int Value, IReadOnlyList<int> Thresholds,
    int ReportedLevel, bool CanSet, bool Required, int PointsPerLevel)
{
    public int MaxValue => Thresholds.Count == 0 ? 0 : Thresholds[^1];
    public bool IsMaxed => Value >= MaxValue;
}

/// <summary>A species' research page: its points (100 = research level 10), whether the game counts it complete or perfect, and its tasks.</summary>
public sealed record SpeciesResearch(int Species, int Rate, bool Complete, bool Perfect, int Unreported, IReadOnlyList<ResearchTask> Tasks)
{
    public int Level => Math.Min(10, Rate / 10);
}

/// <summary>
/// Legends: Arceus Pokédex research, after PKHeX WinForms <c>SAV_PokedexLA</c> (task list and Report)
/// and <c>SAV_PokedexResearchEditorLA</c> (forced task values), on <see cref="PokedexSave8a"/>.
/// Task counts only become research levels once reported, as at Professor Laventon's desk, so
/// every "complete"/"perfect" write ends with <see cref="PokedexSave8a.UpdateSpecificReportPoke(ushort)"/>,
/// which also stamps the species' perfect flag, the dex-wide complete/perfect flags and the
/// total research points. Writes change the save in place: the app runs them inside its mutation path.
/// </summary>
public static class LegendsResearchService
{
    public static bool Supports(ISaveEngineSession session) => session is SaveEngineSession { SaveFile: SAV8LA };

    /// <summary>Every species with a research page, in Hisui dex order.</summary>
    public static IReadOnlyList<int> GetSpecies(ISaveEngineSession session)
    {
        _ = Dex(session);
        var list = new List<(ushort Index, int Species)>();
        for (ushort species = 1; species < PokedexSave8a.MAX_SPECIES; species++)
        {
            var index = PokedexSave8a.GetDexIndex(PokedexType8a.Hisui, species);
            if (index != 0) list.Add((index, species));
        }
        return list.OrderBy(x => x.Index).Select(x => x.Species).ToList();
    }

    public static SpeciesResearch Get(ISaveEngineSession session, int species)
    {
        var dex = Dex(session);
        var s = (ushort)species;
        var defs = Tasks(s);
        var tasks = new List<ResearchTask>(defs.Length);
        for (var i = 0; i < defs.Length; i++)
        {
            var t = defs[i];
            dex.GetResearchTaskLevel(s, i, out var reported, out var value, out _);
            tasks.Add(new ResearchTask(i, Label(t), value, t.TaskThresholds.Select(x => (int)x).ToArray(),
                reported - 1, t.Task.CanSetCurrentValue(), t.RequiredForCompletion,
                t.PointsSingle + t.PointsBonus));
        }
        return new SpeciesResearch(species, dex.GetPokeResearchRate(s), dex.IsComplete(s), dex.IsPerfect(s),
            dex.GetUnreportedTaskCount(s), tasks);
    }

    /// <summary>Forces one task's count (never reported on its own: the player reports in game, or with <see cref="Report"/>).</summary>
    public static SpeciesResearch SetTaskValue(ISaveEngineSession session, int species, int taskIndex, int value)
    {
        var dex = Dex(session);
        var defs = Tasks((ushort)species);
        if ((uint)taskIndex >= defs.Length) throw new ArgumentOutOfRangeException(nameof(taskIndex));
        var task = defs[taskIndex];
        if (!task.Task.CanSetCurrentValue())
            throw new InvalidOperationException("This task follows the save's own progress and can't be set directly.");
        dex.SetResearchTaskProgressByForce((ushort)species, task, Math.Clamp(value, 0, PokedexConstants8a.MaxPokedexResearchPoints));
        return Get(session, species);
    }

    /// <summary>Reports the species' pending progress, as the game's professor does.</summary>
    public static SpeciesResearch Report(ISaveEngineSession session, int species)
    {
        Dex(session).UpdateSpecificReportPoke((ushort)species);
        return Get(session, species);
    }

    /// <summary>
    /// Research level 10: the required tasks reach their first level, then more levels are added
    /// in the page's order until the reported points reach 100. Counts never go down.
    /// </summary>
    public static SpeciesResearch Complete(ISaveEngineSession session, int species)
    {
        var dex = Dex(session);
        var s = (ushort)species;
        var defs = Tasks(s);
        var page = Get(session, species);
        if (page.Complete) return page;

        var levels = page.Tasks.Select(t => LevelOf(t.Value, t.Thresholds)).ToArray();
        var points = page.Tasks.Sum(t => levels[t.Index] * t.PointsPerLevel);
        void Raise(int i)
        {
            var t = page.Tasks[i];
            if (!t.CanSet || levels[i] >= t.Thresholds.Count) return;
            levels[i]++;
            points += t.PointsPerLevel;
            dex.SetResearchTaskProgressByForce(s, defs[i], t.Thresholds[levels[i] - 1]);
        }

        foreach (var t in page.Tasks)
            if (t.Required && levels[t.Index] == 0) Raise(t.Index);
        if (defs.Any(t => t.Task == ObtainForms)) SetAllFormsObtained(dex, s);
        for (var pass = 0; points < 100 && pass < 5; pass++)
            foreach (var t in page.Tasks)
                if (points < 100) Raise(t.Index);
        return Report(session, species);
    }

    /// <summary>Every task at its last threshold, every form obtained, then reported: the game's "perfect" mark.</summary>
    public static SpeciesResearch Perfect(ISaveEngineSession session, int species)
    {
        MaxTasks(Dex(session), (ushort)species);
        return Report(session, species);
    }

    /// <summary><see cref="Perfect"/> for every research species, reported in one report. Returns how many are now perfect.</summary>
    public static int PerfectAll(ISaveEngineSession session)
    {
        var dex = Dex(session);
        var all = GetSpecies(session);
        foreach (var species in all) MaxTasks(dex, (ushort)species);
        dex.UpdateAllReportPoke();
        // PKHeX's all-species report stops one short of the last species (Enamorus): report the rest.
        foreach (var species in all)
            if (dex.GetUnreportedTaskCount((ushort)species) > 0) dex.UpdateSpecificReportPoke((ushort)species);
        return all.Count(x => dex.IsPerfect((ushort)x));
    }

    private static void MaxTasks(PokedexSave8a dex, ushort species)
    {
        foreach (var task in Tasks(species))
        {
            if (task.Task == ObtainForms) { SetAllFormsObtained(dex, species); continue; }
            if (!task.Task.CanSetCurrentValue() || task.TaskThresholds.Length == 0) continue;
            dex.GetResearchTaskProgressByForce(species, task.Task, task.Index, out var current);
            var max = task.TaskThresholds[^1];
            if (current < max) dex.SetResearchTaskProgressByForce(species, task, max);
        }
    }

    /// <summary>Marks each form's every gender variant obtained (plain, not shiny or alpha), the way
    /// <see cref="PokedexSave8a.GetObtainedFormCounts"/> counts them; existing flags are kept.</summary>
    private static void SetAllFormsObtained(PokedexSave8a dex, ushort species)
    {
        var forms = PersonalTable.LA[species].FormCount;
        for (byte form = 0; form < forms; form++)
        {
            var index = PokedexConstants8a.PokemonInfoIds.BinarySearch((ushort)(species | (form << 11)));
            if (index < 0) continue;
            var pack = PokedexConstants8a.PokemonInfoGenders[index];
            byte bits = 0;
            if ((pack & 0b01_1101) != 0) bits |= 1; // first gender / genderless / either
            if ((pack & 0b10_0010) != 0) bits |= 2; // second gender
            if (bits == 0) continue;
            dex.SetPokeHasBeenUpdated(species);
            dex.SetPokeObtainFlags(species, form, (byte)(dex.GetPokeObtainFlags(species, form) | bits));
        }
    }

    private static int LevelOf(int value, IReadOnlyList<int> thresholds)
    {
        var level = 0;
        while (level < thresholds.Count && value >= thresholds[level]) level++;
        return level;
    }

    private static PokedexResearchTask8a[] Tasks(ushort species)
    {
        var index = PokedexSave8a.GetDexIndex(PokedexType8a.Hisui, species);
        if (index == 0) throw new ArgumentOutOfRangeException(nameof(species), "This species has no research page.");
        return PokedexConstants8a.ResearchTasks[index - 1];
    }

    private static PokedexSave8a Dex(ISaveEngineSession session) =>
        session is SaveEngineSession { SaveFile: SAV8LA la }
            ? la.PokedexSave
            : throw new InvalidOperationException("Research tasks exist only in Legends: Arceus saves.");

    private static string Label(PokedexResearchTask8a t) => t.Task switch
    {
        Catch => "Number caught",
        UseMove => $"Times you've seen it use {Name(GameInfo.Strings.movelist, t.Move)}",
        DefeatWithMoveType => $"Number defeated with {Name(GameInfo.Strings.types, (int)t.Type)}-type moves",
        Defeat => "Number defeated",
        Evolve => "Times you've seen it evolve",
        CatchAlpha => "Number of alpha specimens caught",
        CatchLarge => "Number of large specimens caught",
        CatchSmall => "Number of small specimens caught",
        CatchHeavy => "Number of heavy specimens caught",
        CatchLight => "Number of light specimens caught",
        CatchAtTime => $"Number caught {t.TimeOfDay switch
        {
            PokedexTimeOfDay8a.Morning => "in the morning",
            PokedexTimeOfDay8a.Midday => "at midday",
            PokedexTimeOfDay8a.Evening => "in the evening",
            PokedexTimeOfDay8a.Night => "at night",
            _ => "during daylight hours",
        }}",
        CatchSleeping => "Number caught while asleep",
        CatchInAir => "Number caught while in the air",
        CatchNotSpotted => "Number caught without being spotted",
        GiveFood => "Times you've given it food",
        StunWithItems => "Times you've stunned it with items",
        ScareWithScatterBang => "Times you've scared it off with a Scatter Bang",
        LureWithPokeshiDoll => "Times you've lured it with a Pokéshi Doll",
        UseStrongStyleMove => "Times you've seen it use a strong style move",
        UseAgileStyleMove => "Times you've seen it use an agile style move",
        LeapFromTrees => "Times you've seen it leap out of a tree",
        LeapFromLeaves => "Times you've seen it leap out of a pile of leaves",
        LeapFromSnow => "Times you've seen it leap out of a pile of snow",
        LeapFromOre => "Times you've seen it leap out of an ore deposit",
        LeapFromTussocks => "Times you've seen it leap out of a tussock",
        ObtainForms => "Number of different forms you've obtained",
        PartOfArceus => "Investigate the Arceus legend",
        SpeciesQuest => "Complete its request",
        _ => t.Task.ToString(),
    };

    private static string Name(IReadOnlyList<string> list, int index) =>
        (uint)index < list.Count && list[index].Length != 0 ? list[index] : $"#{index}";
}
