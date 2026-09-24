using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;
using Xunit.Abstractions;

namespace PKForge.Engine.Tests;

/// <summary>
/// The suggest buttons, per-move verdicts and the grouped report, proven against the PKHeX
/// legal corpus: every legal sample is broken one field at a time, the matching suggestion
/// is previewed and applied, and a fresh LegalityAnalysis judges the result.
/// </summary>
public sealed class LegalityAssistTests(ITestOutputHelper output)
{
    private static readonly LegalityAssistService Assist = LegalityAssistService.Shared;

    internal static string LegalRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PKForge.sln")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "external", "PKHeX", "Tests", "PKHeX.Core.Tests", "Legality", "Legal");
    }

    internal static readonly (string Name, Func<PKM, bool> Break, LegalityFix Fix, double MinRate)[] Breakers =
    [
        ("met location cleared", pk => { if (pk.Format < 3) return false; pk.MetLocation = 0; return true; }, LegalityFix.MetInfo, 0.90),
        ("wrong ball", pk => { if (pk.Format < 3) return false; pk.Ball = (byte)(pk.Ball == (byte)Ball.Master ? Ball.Cherish : Ball.Master); return true; }, LegalityFix.Ball, 0.90),
        ("relearn Sketch", pk => { if (pk.Format < 6) return false; pk.SetRelearnMoves([(ushort)Move.Sketch, 0, 0, 0]); return true; }, LegalityFix.RelearnMoves, 0.95),
        ("move 1 Sketch", pk => { pk.Move1 = (ushort)Move.Sketch; return true; }, LegalityFix.CurrentMoves, 0.95),
        ("EVs over 510", pk => { if (pk.Format < 3) return false; pk.SetEVs([252, 252, 252, 0, 0, 0]); return true; }, LegalityFix.EffortValues, 1.0),
    ];

    /// <summary>Legal corpus mons (Gen 3+), each opened in its own entity session.</summary>
    private static IEnumerable<(string Name, ISaveEngineSession Session, PKM Pk)> LegalCorpus()
    {
        var engine = new SaveEngine();
        foreach (var file in Directory.EnumerateFiles(LegalRoot(), "*.*", SearchOption.AllDirectories)
                     .Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetExtension(f), @"^\.(pk[3-9]|pb8|pa8)$"))
                     // Enumeration order is the file system's (sorted on APFS, not on ext4): pin it so
                     // every machine picks the same sample.
                     .Order(StringComparer.Ordinal))
        {
            var session = engine.OpenEntitySession(File.ReadAllBytes(file), "corpus");
            if (session is null) continue;
            var pk = ((SaveEngineSession)session).GetEntity(0, 0);
            if (!new LegalityAnalysis(pk).Valid) { session.Dispose(); continue; }
            yield return (Path.GetFileName(file), session, pk);
        }
    }

    private static bool ForbidsMasterBall(PKM pk)
    {
        var trial = pk.Clone();
        trial.Ball = (byte)Ball.Master;
        trial.RefreshChecksum();
        return new LegalityAnalysis(trial).Results.Any(r => r.Identifier == CheckIdentifier.Ball && !r.Valid);
    }

    private static void Put(ISaveEngineSession session, PKM pk) =>
        ((SaveEngineSession)session).SaveFile.SetBoxSlotAtIndex(pk, 0, 0, EntityImportSettings.None);

    [Fact]
    public void SuggestionsRestoreLegalityOnTheBrokenCorpus()
    {
        var tried = Breakers.ToDictionary(b => b.Name, _ => 0);
        var fixedCount = Breakers.ToDictionary(b => b.Name, _ => 0);
        var mons = 0;
        foreach (var (name, session, pk) in LegalCorpus())
        {
            using (session)
            {
                mons++;
                foreach (var (label, breaker, fix, _) in Breakers)
                {
                    var broken = pk.Clone();
                    if (!breaker(broken)) continue;
                    broken.RefreshChecksum();
                    Put(session, broken);
                    if (new LegalityAnalysis(broken).Valid) continue; // that change was legal for this mon
                    tried[label]++;

                    var single = Assist.Preview(session, 0, 0, fix);
                    var all = Assist.Preview(session, 0, 0, LegalityFix.AllSafe);
                    // "Fix all safe" never ends worse than the one targeted suggestion.
                    if (single.ValidAfter) Assert.True(all.ValidAfter, $"{name} / {label}: all-safe lost a fix the single suggestion makes");
                    Assert.True(!all.Available || all.ProblemsAfter < all.ProblemsBefore, $"{name} / {label}: all-safe kept a non-improving step");

                    if (single.Available)
                    {
                        var outcome = Assist.Apply(session, single);
                        Assert.True(outcome.Success, outcome.Message);
                        var written = ((SaveEngineSession)session).GetEntity(0, 0);
                        // What was previewed is exactly what was written, and its verdict holds.
                        Assert.True(written.Data[..written.SIZE_STORED].SequenceEqual(single.Candidate.Span[..written.SIZE_STORED]),
                            $"{name} / {label}: applied bytes differ from the preview");
                        Assert.Equal(single.ValidAfter, new LegalityAnalysis(written).Valid);
                        if (single.ValidAfter) fixedCount[label]++;
                    }
                    Put(session, pk);
                }
            }
        }

        Assert.True(mons >= 80, $"only {mons} legal corpus mons found");
        foreach (var (label, _, _, minRate) in Breakers)
        {
            var rate = tried[label] == 0 ? 1 : fixedCount[label] / (double)tried[label];
            output.WriteLine($"{label}: {fixedCount[label]}/{tried[label]} restored to legal");
            Assert.True(tried[label] > 20, $"{label}: too few samples ({tried[label]})");
            Assert.True(rate >= minRate, $"{label}: only {fixedCount[label]}/{tried[label]} restored");
        }
    }

    [Fact]
    public void ApplyRefusesAPreviewOfAnOlderState()
    {
        var (_, session, pk) = LegalCorpus().First(m => m.Pk.Format >= 6);
        using (session)
        {
            var broken = pk.Clone();
            broken.Move1 = (ushort)Move.Sketch;
            broken.RefreshChecksum();
            Put(session, broken);
            var preview = Assist.Preview(session, 0, 0, LegalityFix.CurrentMoves);
            Assert.True(preview.Available, preview.Unavailable);

            session.ApplyEdit(0, 0, new EntityEdit(Level: Math.Min(100, broken.CurrentLevel + 1)));
            var outcome = Assist.Apply(session, preview);
            Assert.False(outcome.Success);
            Assert.Equal((ushort)Move.Sketch, ((SaveEngineSession)session).GetEntity(0, 0).Move1);
        }
    }

    [Fact]
    public void MoveLegalityFlagsExactlyTheBrokenSlots()
    {
        var (name, session, pk) = LegalCorpus().First(m => m.Pk.Format >= 6 && m.Pk.Move2 != 0 && m.Pk.Species != (ushort)Species.Smeargle);
        using (session)
        {
            var clean = Assist.GetMoveLegality(session, 0, 0);
            Assert.True(clean.Supported);
            Assert.All(clean.Moves, m => Assert.True(m.Valid, $"{name}: {m.Reason}"));
            Assert.All(clean.Moves.Where(m => m.Move != 0), m => Assert.False(string.IsNullOrWhiteSpace(m.Reason)));

            var broken = pk.Clone();
            broken.Move2 = (ushort)Move.Sketch;
            broken.RefreshChecksum();
            Put(session, broken);
            var verdict = Assist.GetMoveLegality(session, 0, 0);
            Assert.False(verdict.Move(1)!.Valid);
            Assert.Equal((int)Move.Sketch, verdict.Move(1)!.Move);
            Assert.True(verdict.Move(0)!.Valid);
            Assert.NotEqual("Legal", verdict.Move(1)!.Reason);

            // A bad relearn slot is flagged on its own; the current moves keep their verdicts.
            var relearn = pk.Clone();
            relearn.SetRelearnMoves([(ushort)Move.Sketch, 0, 0, 0]);
            relearn.RefreshChecksum();
            Put(session, relearn);
            var relearnVerdict = Assist.GetMoveLegality(session, 0, 0);
            Assert.False(relearnVerdict.RelearnAt(0)!.Valid);
            Assert.All(relearnVerdict.Moves, m => Assert.True(m.Valid, m.Reason));
        }
    }

    [Fact]
    public void ReportGroupsFailuresAndOffersTheMatchingFix()
    {
        // A sample whose encounter really forbids the Master Ball: for some encounters it is legal,
        // and those would never produce the Ball failure this test is about.
        var (_, session, pk) = LegalCorpus().First(m => m.Pk.Format >= 6 && m.Pk.Ball != (byte)Ball.Master && ForbidsMasterBall(m.Pk));
        using (session)
        {
            var clean = Assist.GetReport(session, 0, 0);
            Assert.True(clean.Valid);
            Assert.Empty(clean.Fixes);

            var broken = pk.Clone();
            broken.Ball = (byte)Ball.Master;
            broken.Move1 = (ushort)Move.Sketch;
            broken.RefreshChecksum();
            Put(session, broken);
            var report = Assist.GetReport(session, 0, 0);
            Assert.False(report.Valid);
            var ball = Assert.Single(report.Groups, g => g.Key == nameof(CheckIdentifier.Ball));
            Assert.Equal(CheckSeverity.Invalid, ball.Severity);
            Assert.Equal(LegalityFix.Ball, ball.Fix);
            Assert.DoesNotContain(ball.Lines, l => l.Text.StartsWith("Invalid:", StringComparison.Ordinal));
            Assert.Contains(LegalityFix.CurrentMoves, report.Fixes);
            Assert.True(report.Groups.TakeWhile(g => !g.Valid).Count() >= 2, "invalid groups come first");

            var all = Assist.Preview(session, 0, 0, LegalityFix.AllSafe);
            Assert.True(all.ValidAfter, all.Outcome);
            Assert.Contains(all.Changes, c => c.Field == "Ball");
            Assert.Contains(all.Changes, c => c.Field == "Move 1");
        }
    }

    [Fact]
    public void EmptySlotsAndForgedPreviewsWriteNothing()
    {
        using var session = new SaveEngine().OpenBlankSession(8);
        var preview = Assist.Preview(session, 0, 5, LegalityFix.MetInfo);
        Assert.False(preview.Available);
        Assert.False(Assist.Apply(session, preview).Success);
        var forged = preview with { Available = true, Basis = new byte[8], Candidate = new byte[8] };
        Assert.False(Assist.Apply(session, forged).Success);
        Assert.True(session.ReadEntity(0, 5).IsEmpty);
    }
}
