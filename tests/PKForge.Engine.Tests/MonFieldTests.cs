using PKForge.Domain;
using PKForge.Engine;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// Per-Pokémon fields: form, form argument, mint, shiny type / PID / EC, OT gender and the
/// handling trainer, memories and Technical Records. Each field round-trips through the
/// exported entity bytes, stays legal where PKHeX says it should, and is absent on formats
/// that do not store it.
/// </summary>
public sealed class MonFieldTests
{
    private const ushort Pikachu = 25, Venusaur = 3, Rotom = 479, Alcremie = 869, Yamask = 562, Furfrou = 676, Wurmple = 265;

    private static SaveEngineSession Legal(int generation, int species, int form = 0)
    {
        var session = (SaveEngineSession)new SaveEngine().OpenBlankSession(generation);
        var outcome = new LegalizerService().Generate(session, 0, 0,
            new GenerationRequest(species, 50, Shiny: false, Nature: null, Ability: null, Ball: null, Moves: null, Form: form));
        Assert.True(outcome.Success, outcome.Message);
        AssertLegal(session);
        return session;
    }

    /// <summary>A bare mon in a blank save of its own game, so PB8/PK6 are never re-detected as PK8/PK7.</summary>
    private static SaveEngineSession Seed(PKM mon, ushort species = Pikachu, byte form = 0)
    {
        mon.Species = species;
        mon.Form = form;
        mon.CurrentLevel = 30;
        mon.RefreshChecksum();
        var save = BlankSaveFile.Get(mon.Version, "Sof", LanguageID.English);
        save.SetBoxSlotAtIndex(mon, 0, 0, EntityImportSettings.None);
        var session = new SaveEngineSession(save, null);
        Assert.Equal(mon.GetType(), session.GetEntity(0, 0).GetType());
        return session;
    }

    /// <summary>The stored bytes, re-parsed: proof the write reached the slot.</summary>
    private static PKM Stored(SaveEngineSession session) =>
        EntityFormat.GetFromBytes(session.ExportSlot(0, 0).Data, session.GetEntity(0, 0).Context)!;

    private static void AssertLegal(SaveEngineSession session)
    {
        var la = new LegalityAnalysis(session.GetEntity(0, 0));
        Assert.True(la.Valid, la.Report());
    }

    // ── Form ──

    [Fact]
    public void RotomFormChangeSwapsTheApplianceMoveAndStaysLegal()
    {
        using var session = Legal(9, Rotom);
        var info = MonFieldService.GetForm(session, 0, 0);
        Assert.NotNull(info);
        Assert.True(info.Choices.Count >= 6);

        var heat = MonFieldService.SetForm(session, 0, 0, 1);
        Assert.True(heat.Changed);
        Assert.Null(heat.Warning);
        var stored = Stored(session);
        Assert.Equal(1, stored.Form);
        Assert.True(stored.HasMove((ushort)Move.Overheat));
        AssertLegal(session);

        MonFieldService.SetForm(session, 0, 0, 2);
        stored = Stored(session);
        Assert.True(stored.HasMove((ushort)Move.HydroPump));
        Assert.False(stored.HasMove((ushort)Move.Overheat));
        AssertLegal(session);

        MonFieldService.SetForm(session, 0, 0, 0);
        stored = Stored(session);
        Assert.Equal(0, stored.Form);
        Assert.False(stored.HasMove((ushort)Move.HydroPump));
        AssertLegal(session);
    }

    [Fact]
    public void BattleOnlyFormsAreFlaggedAndWarned()
    {
        using var session = Legal(7, Venusaur);
        var info = MonFieldService.GetForm(session, 0, 0)!;
        Assert.True(info.Choices[1].BattleOnly);
        var mega = MonFieldService.SetForm(session, 0, 0, 1);
        Assert.True(mega.Changed);
        Assert.NotNull(mega.Warning);
        Assert.Equal(1, Stored(session).Form);
    }

    [Fact]
    public void Gen3UnownLetterIsRerolledIntoThePidKeepingTheNature()
    {
        using var session = Seed(new PK3 { Version = GameVersion.E, PID = 0x0000_0000 }, 201);
        var nature = session.GetEntity(0, 0).Nature;
        var result = MonFieldService.SetForm(session, 0, 0, 7); // H
        Assert.True(result.Changed);
        var stored = Stored(session);
        Assert.Equal(7, stored.Form);
        Assert.Equal(7, EntityPID.GetUnownForm3(stored.PID));
        Assert.Equal(nature, stored.Nature);
        Assert.False(stored.IsShiny);
    }

    [Fact]
    public void SingleFormSpeciesHaveNoFormField()
    {
        using var session = Legal(8, 133); // Eevee: one form outside Let's Go
        Assert.Null(MonFieldService.GetForm(session, 0, 0));
        Assert.Throws<InvalidOperationException>(() => MonFieldService.SetForm(session, 0, 0, 1));
    }

    // ── Form argument ──

    [Fact]
    public void AlcremieSweetRoundTripsAsANamedArgument()
    {
        using var session = Seed(new PK8 { Version = GameVersion.SW }, Alcremie);
        var arg = MonFieldService.GetFormArgument(session, 0, 0);
        Assert.NotNull(arg);
        Assert.Equal(FormArgumentKind.Named, arg.Kind);
        Assert.Equal(arg.Names.Count - 1, (int)arg.Max);
        MonFieldService.SetFormArgument(session, 0, 0, 3);
        Assert.Equal(3u, ((IFormArgument)Stored(session)).FormArgument);
        MonFieldService.SetFormArgument(session, 0, 0, 99);
        Assert.Equal(arg.Max, ((IFormArgument)Stored(session)).FormArgument);
    }

    [Fact]
    public void GalarianYamaskDamageIsARawArgumentClampedToPkhexMax()
    {
        using var session = Seed(new PK8 { Version = GameVersion.SW }, Yamask, form: 1);
        var arg = MonFieldService.GetFormArgument(session, 0, 0)!;
        Assert.Equal(FormArgumentKind.Raw, arg.Kind);
        Assert.Equal(9999u, arg.Max);
        MonFieldService.SetFormArgument(session, 0, 0, 49);
        Assert.Equal(49u, ((IFormArgument)Stored(session)).FormArgument);
    }

    [Fact]
    public void FurfrouTrimTimerUsesPkhexTripleSemanticsInTheGen6Party()
    {
        var mon = new PK6 { Version = GameVersion.X, Species = Furfrou, Form = 1, CurrentLevel = 30 };
        mon.RefreshChecksum();
        var save = BlankSaveFile.Get(GameVersion.X, "Sof", LanguageID.English);
        save.SetPartySlotAtIndex(mon, 0, EntityImportSettings.None);
        using var session = new SaveEngineSession(save, null);

        var arg = MonFieldService.GetFormArgument(session, -1, 0)!;
        Assert.Equal(FormArgumentKind.TripleParty, arg.Kind);
        MonFieldService.SetFormArgument(session, -1, 0, 3);
        var f = (IFormArgument)session.GetEntity(-1, 0);
        Assert.Equal(3, f.FormArgumentRemain);
        Assert.Equal(2, f.FormArgumentElapsed); // max 5 - 3 remaining

        // Boxed Gen 6 data has no party block: the timer is refused rather than silently lost.
        using var boxed = Seed(new PK6 { Version = GameVersion.X }, Furfrou, form: 1);
        Assert.Throws<InvalidOperationException>(() => MonFieldService.SetFormArgument(boxed, 0, 0, 3));
    }

    [Fact]
    public void FormArgumentIsHiddenWhereAbsent()
    {
        using var pikachu = Seed(new PK8 { Version = GameVersion.SW });
        Assert.Null(MonFieldService.GetFormArgument(pikachu, 0, 0));
        using var gen5 = Seed(new PK5 { Version = GameVersion.B }, Furfrou);
        Assert.Null(MonFieldService.GetFormArgument(gen5, 0, 0));
    }

    // ── Mints ──

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void MintChangesStatsOnlyAndStaysLegal(int generation)
    {
        using var session = Legal(generation, Pikachu);
        var nature = session.GetEntity(0, 0).Nature;
        var target = nature == Nature.Adamant ? Nature.Modest : Nature.Adamant;
        var preview = MonFieldService.PreviewStatNature(session, 0, 0)!;
        MonFieldService.SetStatNature(session, 0, 0, (int)target);

        var stored = Stored(session);
        Assert.Equal(nature, stored.Nature);
        Assert.Equal(target, stored.StatAlignment);
        Assert.Equal(preview.StatsFor((int)target), session.ReadEntity(0, 0).Stats);
        AssertLegal(session);
        Assert.Equal((int)target, new MonSummaryService().Build(session, 0, 0)!.Fields!.StatNature);
    }

    [Fact]
    public void MintIsHiddenBeforeGen8()
    {
        using var session = Legal(7, Pikachu);
        Assert.Null(MonFieldService.GetStatNature(session, 0, 0));
        Assert.Null(MonFieldService.PreviewStatNature(session, 0, 0));
        Assert.Throws<InvalidOperationException>(() => MonFieldService.SetStatNature(session, 0, 0, 3));
    }

    // ── Shiny type, PID, EC ──

    [Fact]
    public void SwordShieldShinyStarAndSquareAreDistinct()
    {
        using var session = Legal(8, Pikachu);
        Assert.True(MonFieldService.GetShiny(session, 0, 0).SupportsKind);
        Assert.True(MonFieldService.SetShinyKind(session, 0, 0, ShinyKind.Square));
        Assert.Equal(0, Stored(session).ShinyXor);
        Assert.Equal(ShinyKind.Square, MonFieldService.GetShiny(session, 0, 0).Kind);
        AssertLegal(session);

        Assert.True(MonFieldService.SetShinyKind(session, 0, 0, ShinyKind.Star));
        Assert.Equal(1, Stored(session).ShinyXor);
        Assert.Equal(ShinyKind.Star, MonFieldService.GetShiny(session, 0, 0).Kind);
        AssertLegal(session);

        Assert.True(MonFieldService.SetShinyKind(session, 0, 0, ShinyKind.None));
        Assert.False(Stored(session).IsShiny);
    }

    [Fact]
    public void ScarletVioletHasNoSquareShinies()
    {
        using var session = Legal(9, Pikachu);
        Assert.False(MonFieldService.GetShiny(session, 0, 0).SupportsKind);
        MonFieldService.SetShinyKind(session, 0, 0, ShinyKind.Square);
        Assert.True(Stored(session).IsShiny);
    }

    [Fact]
    public void RawPidInGen4ResyncsGenderAndReportsLinkedTraits()
    {
        using var session = Seed(new PK4 { Version = GameVersion.D, Gender = 0 }, Pikachu);
        var shiny = MonFieldService.GetShiny(session, 0, 0);
        Assert.True(shiny.PidLinked);
        Assert.Null(shiny.EncryptionConstant);

        const uint femalePid = 0x1234_5600; // low byte 0x00 < Pikachu's ratio: female
        var result = MonFieldService.SetPid(session, 0, 0, femalePid);
        Assert.True(result.Changed);
        var stored = Stored(session);
        Assert.Equal(femalePid, stored.PID);
        Assert.Equal(1, stored.Gender);
        Assert.Equal((Nature)(femalePid % 25), stored.Nature);
        Assert.Contains(result.Changes, c => c.StartsWith("Gender", StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => MonFieldService.SetEncryptionConstant(session, 0, 0, 1));
    }

    [Fact]
    public void EncryptionConstantRoundTripsAndReportsWurmpleBranch()
    {
        using var session = Seed(new PK8 { Version = GameVersion.SW, EncryptionConstant = 0x0000_0001 }, Wurmple);
        var result = MonFieldService.SetEncryptionConstant(session, 0, 0, 0x0009_0000); // 9 % 10 / 5 = 1
        Assert.Equal(0x0009_0000u, Stored(session).EncryptionConstant);
        Assert.Contains(result.Changes, c => c.Contains("Wurmple", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0x1A2b3C4d", true, 0x1A2B3C4Du)]
    [InlineData("  ffffffff ", true, 0xFFFFFFFFu)]
    [InlineData("123456789", false, 0u)]
    [InlineData("xyz", false, 0u)]
    [InlineData("", false, 0u)]
    public void HexInputIsValidated(string text, bool ok, uint expected)
    {
        Assert.Equal(ok, MonFieldService.TryParseHex(text, out var value));
        Assert.Equal(expected, value);
    }

    // ── OT gender and handler ──

    [Fact]
    public void HandlerFieldsRoundTripOnPk8()
    {
        using var session = Seed(new PK8 { Version = GameVersion.SW, OriginalTrainerName = "Sof" });
        MonFieldService.ApplyTrainerEdit(session, 0, 0, new TrainerFieldsEdit(
            OtGender: 1, OtFriendship: 120, HandlerName: "Leon", HandlerGender: 0, HandlerLanguage: (int)LanguageID.French,
            HandlerFriendship: 77, CurrentHandler: 1));
        var stored = (PK8)Stored(session);
        Assert.Equal(1, stored.OriginalTrainerGender);
        Assert.Equal(120, stored.OriginalTrainerFriendship);
        Assert.Equal("Leon", stored.HandlingTrainerName);
        Assert.Equal((byte)LanguageID.French, stored.HandlingTrainerLanguage);
        Assert.Equal(77, stored.HandlingTrainerFriendship);
        Assert.Equal(1, stored.CurrentHandler);
        Assert.Equal(77, stored.CurrentFriendship);

        var fields = new MonSummaryService().Build(session, 0, 0)!.Fields!;
        Assert.Equal("Leon", fields.HandlerName);
        Assert.True(fields.WithHandler);

        // Clearing the name returns it to the OT and forgets the handler.
        MonFieldService.ApplyTrainerEdit(session, 0, 0, new TrainerFieldsEdit(HandlerName: ""));
        stored = (PK8)Stored(session);
        Assert.Equal(0, stored.CurrentHandler);
        Assert.Equal(0, stored.HandlingTrainerLanguage);
        Assert.Throws<InvalidOperationException>(() => MonFieldService.ApplyTrainerEdit(session, 0, 0, new TrainerFieldsEdit(CurrentHandler: 1)));
        Assert.Throws<ArgumentException>(() => MonFieldService.ApplyTrainerEdit(session, 0, 0, new TrainerFieldsEdit(HandlerName: "ThirteenChars")));
    }

    [Fact]
    public void Gen3KeepsOtGenderButNoHandler()
    {
        using var session = Seed(new PK3 { Version = GameVersion.E, OriginalTrainerName = "SOF" });
        var fields = MonFieldService.GetTrainers(session, 0, 0);
        Assert.NotNull(fields.OtGender);
        Assert.False(fields.HasHandler);
        Assert.Empty(MonFieldService.GetHandlerLanguageChoices(session, 0, 0));
        MonFieldService.ApplyTrainerEdit(session, 0, 0, new TrainerFieldsEdit(OtGender: 1));
        Assert.Equal(1, Stored(session).OriginalTrainerGender);
        Assert.Throws<InvalidOperationException>(() => MonFieldService.ApplyTrainerEdit(session, 0, 0, new TrainerFieldsEdit(HandlerName: "X")));
    }

    [Fact]
    public void Pk7HasHandlerButNoHandlerLanguage()
    {
        using var session = Legal(7, Pikachu);
        var fields = MonFieldService.GetTrainers(session, 0, 0);
        Assert.True(fields.HasHandler);
        Assert.Null(fields.HandlerLanguage);
        Assert.Throws<InvalidOperationException>(() => MonFieldService.ApplyTrainerEdit(session, 0, 0, new TrainerFieldsEdit(HandlerLanguage: 2)));
    }

    // ── Memories ──

    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    public void OtMemoryRoundTripsWithItsSentence(int generation)
    {
        using var session = Legal(generation, Pikachu);
        var field = MonFieldService.GetMemory(session, 0, 0, handler: false)!;
        Assert.True(field.Editable);

        var options = MonFieldService.GetMemoryOptions(session, 0, 0, handler: false, memory: 5);
        Assert.Contains(options.Memories, m => m.Id == 5 && m.Legal);
        Assert.Equal("Item", options.ArgumentCategory);
        var item = options.Arguments.First(a => a.Id != 0);
        var intensity = options.Intensities.First(i => i.Legal);
        var feeling = options.Feelings.First(f => f.Legal);

        MonFieldService.SetMemory(session, 0, 0, new MemoryEdit(false, 5, intensity.Id, feeling.Id, item.Id));
        var stored = (IMemoryOT)Stored(session);
        Assert.Equal(5, stored.OriginalTrainerMemory);
        Assert.Equal(item.Id, stored.OriginalTrainerMemoryVariable);
        Assert.Equal(intensity.Id, stored.OriginalTrainerMemoryIntensity);
        Assert.Equal(feeling.Id, stored.OriginalTrainerMemoryFeeling);
        var text = MonFieldService.GetMemory(session, 0, 0, handler: false)!.Text;
        Assert.Contains(item.Name, text);
        Assert.Contains(session.GetEntity(0, 0).Nickname, text);

        // "No memory" clears the dependent values, as PKHeX's editor does.
        MonFieldService.SetMemory(session, 0, 0, new MemoryEdit(false, 0, 3, 3, 3));
        stored = (IMemoryOT)Stored(session);
        Assert.Equal(0, stored.OriginalTrainerMemoryVariable + stored.OriginalTrainerMemoryIntensity + stored.OriginalTrainerMemoryFeeling);
    }

    [Fact]
    public void HandlerMemoryNeedsAHandler()
    {
        using var session = Legal(8, Pikachu);
        MonFieldService.ApplyTrainerEdit(session, 0, 0, new TrainerFieldsEdit(HandlerName: ""));
        var field = MonFieldService.GetMemory(session, 0, 0, handler: true)!;
        Assert.False(field.Editable);
        Assert.Throws<InvalidOperationException>(() => MonFieldService.SetMemory(session, 0, 0, new MemoryEdit(true, 4, 1, 1, 9)));
    }

    [Fact]
    public void MemoriesAreHiddenBeforeGen6()
    {
        using var session = Legal(5, Pikachu);
        Assert.Null(MonFieldService.GetMemory(session, 0, 0, handler: false));
        Assert.Null(MonFieldService.GetMemory(session, 0, 0, handler: true));
        Assert.Null(new MonSummaryService().Build(session, 0, 0)!.Fields!.OtMemory);
    }

    // ── Technical Records ──

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void GiveAllLegalRecordsStaysLegalAndClearEmpties(int generation)
    {
        using var session = Legal(generation, Pikachu);
        var records = MonFieldService.GetTechRecords(session, 0, 0);
        Assert.NotNull(records);
        Assert.Contains(records, r => r.Permitted);
        Assert.All(records, r => Assert.False(string.IsNullOrEmpty(r.Name)));

        var count = MonFieldService.SetAllLegalTechRecords(session, 0, 0);
        Assert.True(count > 0);
        Assert.Equal(count, MonFieldService.GetTechRecords(session, 0, 0)!.Count(r => r.Learned));
        Assert.All(MonFieldService.GetTechRecords(session, 0, 0)!.Where(r => r.Learned), r => Assert.True(r.Permitted));
        AssertLegal(session);

        Assert.Equal(0, MonFieldService.ClearTechRecords(session, 0, 0));
        Assert.False(((ITechRecord)Stored(session)).GetMoveRecordFlagAny());

        var one = records.First(r => r.Permitted);
        MonFieldService.SetTechRecord(session, 0, 0, one.Index, true);
        Assert.True(((ITechRecord)Stored(session)).GetMoveRecordFlag(one.Index));
        AssertLegal(session);
        Assert.Equal(1, new MonSummaryService().Build(session, 0, 0)!.Fields!.TechRecordCount);
    }

    [Fact]
    public void TechRecordsAreHiddenWhereTheFormatHasNone()
    {
        using var bdsp = Seed(new PB8 { Version = GameVersion.BD });
        Assert.Null(MonFieldService.GetTechRecords(bdsp, 0, 0));
        Assert.Throws<InvalidOperationException>(() => MonFieldService.SetAllLegalTechRecords(bdsp, 0, 0));
        using var legends = Seed(new PA8 { Version = GameVersion.PLA }); // its record interface is the Move Shop
        Assert.Null(MonFieldService.GetTechRecords(legends, 0, 0));
        using var gen7 = Seed(new PK7 { Version = GameVersion.SN });
        Assert.Null(MonFieldService.GetTechRecords(gen7, 0, 0));
    }
}
