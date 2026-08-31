using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Regression tests for two classes of critical bugs: PKHeX slot setters silently
/// marking the dex, bumping trainer records, and rewriting handler data on every
/// write; and PID-coupled edits (Gen 3-5 nature/gender/ability) landing as silent
/// no-ops or PID-desynced (illegal) data.
/// </summary>
public sealed class SurgicalWriteAndPidCouplingTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static IEnumerable<string> Corpus(string pattern) =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "external", "PKHeX", "Tests", "PKHeX.Core.Tests"),
            pattern, SearchOption.AllDirectories);

    /// <summary>First corpus specimen the predicate accepts, opened as an entity session.</summary>
    private static SaveEngineSession OpenFirst(string pattern, Func<EntityDetail, bool> usable)
    {
        var engine = new SaveEngine();
        foreach (var file in Corpus(pattern))
        {
            SaveEngineSession? session = null;
            try
            {
                session = engine.OpenEntitySession(File.ReadAllBytes(file), "test") as SaveEngineSession;
                if (session is null) continue;
                var detail = session.ReadEntity(0, 0);
                if (!usable(detail)) { session.Dispose(); session = null; continue; }
                return session;
            }
            catch
            {
                session?.Dispose();
            }
        }
        throw new InvalidOperationException($"No usable {pattern} specimen in the corpus.");
    }

    private static bool AllowsBothGenders(int species)
    {
        var ratio = PersonalTable.B2W2[(ushort)species].Gender;
        return !PersonalInfo.IsSingleGender(ratio) && ratio != PersonalInfo.RatioMagicGenderless;
    }

    [Fact]
    public void SlotEditsNeverTouchDexRecordsOrHandlers()
    {
        var engine = new SaveEngine();
        using var session = (SaveEngineSession)engine.OpenBlankSession(7);

        var imported = Corpus("*.pk7").Select(File.ReadAllBytes).FirstOrDefault(bytes => session.ImportSlot(0, 0, bytes));
        Assert.NotNull(imported);
        var detail = session.ReadEntity(0, 0);
        Assert.NotEqual(0, detail.Species);

        session.SetDexEntry(detail.Species, seen: false, caught: false);
        session.ApplyEdit(0, 0, new EntityEdit(OriginalTrainer: "ForeignOT"));

        var save = session.SaveFile;
        var arranged = save.GetBoxSlotAtIndex(0, 0);
        arranged.HandlingTrainerName = "SentinelHT";
        arranged.RefreshChecksum();
        save.SetBoxSlotAtIndex(arranged, 0, 0, EntityImportSettings.None);

        var recordsBefore = session.GetTrainerRecords().Records.Select(r => r.Value).ToArray();

        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "Edited"));
        session.SortBoxes(SortCriteria.DexNumber);
        session.BatchApply(["Level=55"]);

        var after = save.GetBoxSlotAtIndex(0, 0);
        Assert.Equal("Edited", after.Nickname);
        Assert.Equal(55, after.CurrentLevel);
        Assert.Equal("SentinelHT", after.HandlingTrainerName);
        Assert.False(session.GetDexEntry(detail.Species).Seen);
        Assert.False(session.GetDexEntry(detail.Species).Caught);
        Assert.Equal(recordsBefore, session.GetTrainerRecords().Records.Select(r => r.Value).ToArray());
    }

    [Fact]
    public void GenderEditLandsOnGen3AndKeepsNature()
    {
        using var session = OpenFirst("*.pk3", d => d.Species != 0 && AllowsBothGenders(d.Species) && d.Gender is 0 or 1);
        var before = session.ReadEntity(0, 0);
        var other = before.Gender == 0 ? 1 : 0;

        session.ApplyEdit(0, 0, new EntityEdit(Gender: other));

        var after = session.ReadEntity(0, 0);
        Assert.Equal(other, after.Gender);
        Assert.Equal(before.Nature, after.Nature);
    }

    [Fact]
    public void AbilityEditLandsOnGen3()
    {
        var engine = new SaveEngine();
        foreach (var file in Corpus("*.pk3"))
        {
            SaveEngineSession? session = null;
            try
            {
                session = engine.OpenEntitySession(File.ReadAllBytes(file), "g3") as SaveEngineSession;
                if (session is null) continue;
                var before = session.ReadEntity(0, 0);
                var choices = session.GetAbilityChoices(before.Species, before.Form);
                if (before.Species == 0 || choices.Count < 2)
                {
                    session.Dispose();
                    continue;
                }
                var other = choices.FirstOrDefault(a => a != before.Ability);
                if (other == 0)
                {
                    session.Dispose();
                    continue;
                }

                session.ApplyEdit(0, 0, new EntityEdit(Ability: other));
                Assert.Equal(other, session.ReadEntity(0, 0).Ability);
                session.Dispose();
                return;
            }
            catch
            {
                session?.Dispose();
            }
        }
        Assert.Fail("No dual-ability pk3 specimen in the corpus.");
    }

    [Theory]
    [InlineData("*.pk3")]
    [InlineData("*.pk4")]
    public void NatureEditKeepsShinyState(string pattern)
    {
        using var session = OpenFirst(pattern, d => d.Species != 0);
        session.ApplyEdit(0, 0, new EntityEdit(IsShiny: true));
        Assert.True(session.ReadEntity(0, 0).IsShiny);

        var before = session.ReadEntity(0, 0);
        var target = (before.Nature + 7) % 25;
        session.ApplyEdit(0, 0, new EntityEdit(Nature: target));

        var after = session.ReadEntity(0, 0);
        Assert.Equal(target, after.Nature);
        Assert.True(after.IsShiny);
    }

    [Theory]
    [InlineData("*.pk4")]
    [InlineData("*.pk5")]
    public void GenderEditLandsAndStaysPidConsistentOnGen4And5(string pattern)
    {
        using var session = OpenFirst(pattern, d => d.Species != 0 && AllowsBothGenders(d.Species) && d.Gender is 0 or 1);
        var before = session.ReadEntity(0, 0);
        var other = before.Gender == 0 ? 1 : 0;

        session.ApplyEdit(0, 0, new EntityEdit(Gender: other));

        var entity = session.SaveFile.GetBoxSlotAtIndex(0, 0);
        Assert.Equal(other, entity.Gender);
        Assert.Equal(entity.Gender, EntityGender.GetFromPIDAndRatio(entity.PID, PersonalTable.B2W2[entity.Species].Gender));
    }

    [Fact]
    public void ShinyGenderEditNeverFailsByChance()
    {
        // The old random PID search needed shiny + nature + ability + a rare gender at
        // once (~1 in 3.3M per attempt) and could exhaust 5M attempts about a fifth of
        // the time. The guided construction pins everything but nature, so repeated
        // shiny gender flips must always land.
        using var session = OpenFirst("*.pk4", d => d.Species != 0 && AllowsBothGenders(d.Species) && d.Gender is 0 or 1);
        session.ApplyEdit(0, 0, new EntityEdit(IsShiny: true));
        var start = session.ReadEntity(0, 0);
        Assert.True(start.IsShiny);
        var other = start.Gender == 0 ? 1 : 0;

        for (var round = 0; round < 25; round++)
        {
            session.ApplyEdit(0, 0, new EntityEdit(Gender: other));
            var flipped = session.ReadEntity(0, 0);
            Assert.Equal(other, flipped.Gender);
            Assert.True(flipped.IsShiny);

            session.ApplyEdit(0, 0, new EntityEdit(Gender: (byte)start.Gender));
            var restored = session.ReadEntity(0, 0);
            Assert.Equal(start.Gender, restored.Gender);
            Assert.True(restored.IsShiny);
        }
    }

    [Fact]
    public void BatchNatureKeepsShinyStateOnGen3()
    {
        using var session = OpenFirst("*.pk3", d => d.Species != 0);
        session.ApplyEdit(0, 0, new EntityEdit(IsShiny: true));

        var touched = session.BatchApply(["Nature=11"]);

        Assert.Equal(1, touched);
        var after = session.ReadEntity(0, 0);
        Assert.Equal(11, after.Nature);
        Assert.True(after.IsShiny);
    }
}
