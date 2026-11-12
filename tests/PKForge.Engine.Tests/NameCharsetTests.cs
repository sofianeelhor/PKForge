using PKForge.Domain;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>
/// QA 2026-11-12 issue 2: a Latin nickname typed on a Japanese Emerald was written as an empty
/// nickname with the nicknamed flag still set. Names are now mapped to the save's charset
/// (half-width ↔ full-width) or refused before anything is written.
/// </summary>
public sealed class NameCharsetTests
{
    private static SaveEngineSession Session(GameVersion version, LanguageID language, string ot = "Sof")
    {
        var save = BlankSaveFile.Get(version, ot, language);
        var probe = save.BlankPKM;
        probe.Species = (ushort)Species.Abra;
        probe.Language = (int)language;
        probe.CurrentLevel = 10;
        probe.ClearNickname();
        probe.OriginalTrainerName = save.OT;
        probe.RefreshChecksum();
        save.SetBoxSlotAtIndex(probe, 0, 0, EntityImportSettings.None);
        return new SaveEngineSession(save, version.ToString());
    }

    [Fact]
    public void JapaneseGen3MapsLatinNicknameToFullWidth()
    {
        using var session = Session(GameVersion.E, LanguageID.Japanese, "ソフ");
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "TEST"));
        var pk = session.SaveFile.GetBoxSlotAtIndex(0, 0);
        Assert.Equal("ＴＥＳＴ", pk.Nickname);
        Assert.True(pk.IsNicknamed);
    }

    [Fact]
    public void JapaneseGen3RefusesAccentsWithoutTouchingTheMon()
    {
        using var session = Session(GameVersion.E, LanguageID.Japanese, "ソフ");
        var before = session.SaveFile.GetBoxSlotAtIndex(0, 0).Nickname;
        var error = Assert.Throws<NameNotStorableException>(() =>
            session.ApplyEdit(0, 0, new EntityEdit(Nickname: "Léa", Level: 40)));
        Assert.Contains("“é”", error.Message);
        Assert.Contains("Japanese", error.Message);
        var pk = session.SaveFile.GetBoxSlotAtIndex(0, 0);
        Assert.Equal(before, pk.Nickname);
        Assert.False(pk.IsNicknamed);
        Assert.Equal(10, pk.CurrentLevel); // refused before any field was written
    }

    [Fact]
    public void InternationalGen3MapsFullWidthToHalfWidth()
    {
        using var session = Session(GameVersion.FR, LanguageID.English);
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "Ｂｒｕｈ"));
        Assert.Equal("Bruh", session.SaveFile.GetBoxSlotAtIndex(0, 0).Nickname);
    }

    [Fact]
    public void InternationalGen3RefusesKanji()
    {
        using var session = Session(GameVersion.FR, LanguageID.English);
        var error = Assert.Throws<NameNotStorableException>(() => session.ApplyEdit(0, 0, new EntityEdit(Nickname: "犬")));
        Assert.Contains("“犬”", error.Message);
    }

    [Theory]
    [InlineData(GameVersion.RD, LanguageID.English, "Sparky")]
    [InlineData(GameVersion.C, LanguageID.English, "Sparky")]
    [InlineData(GameVersion.FR, LanguageID.French, "Éclair")]
    [InlineData(GameVersion.Pt, LanguageID.English, "Sparky")]
    [InlineData(GameVersion.Pt, LanguageID.Japanese, "ピカ")]
    [InlineData(GameVersion.B, LanguageID.French, "Éclair")]
    [InlineData(GameVersion.SN, LanguageID.English, "Sparky")]
    public void OrdinaryNamesPassUnchanged(GameVersion version, LanguageID language, string name)
    {
        using var session = Session(version, language);
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: name));
        Assert.Equal(name, NameCharset.ToHalfWidth(session.SaveFile.GetBoxSlotAtIndex(0, 0).Nickname));
    }

    [Fact]
    public void GameBoyInternationalRefusesKana()
    {
        using var session = Session(GameVersion.RD, LanguageID.English);
        Assert.Throws<NameNotStorableException>(() => session.ApplyEdit(0, 0, new EntityEdit(Nickname: "ピカ")));
    }

    [Fact]
    public void TooLongNamesAreRefusedNotCut()
    {
        using var session = Session(GameVersion.FR, LanguageID.English);
        var error = Assert.Throws<NameNotStorableException>(() =>
            session.ApplyEdit(0, 0, new EntityEdit(Nickname: "Abcdefghijk")));
        Assert.Contains("10 characters", error.Message);
    }

    [Fact]
    public void JapaneseGen3TrainerNameIsMappedBeforeWriting()
    {
        using var session = Session(GameVersion.E, LanguageID.Japanese, "ソフ");
        var trainer = session.GetTrainer();
        session.SetTrainer(trainer with { Name = "SOF" });
        Assert.Equal("ＳＯＦ", session.SaveFile.OT);

        Assert.Throws<NameNotStorableException>(() => session.SetTrainer(trainer with { Name = "Zoé" }));
        Assert.Equal("ＳＯＦ", session.SaveFile.OT);
    }

    [Fact]
    public void JapaneseGen3OriginalTrainerEditIsMapped()
    {
        using var session = Session(GameVersion.E, LanguageID.Japanese, "ソフ");
        session.ApplyEdit(0, 0, new EntityEdit(OriginalTrainer: "Ash"));
        Assert.Equal("Ａｓｈ", session.SaveFile.GetBoxSlotAtIndex(0, 0).OriginalTrainerName);
    }

    [Fact]
    public void JapaneseGen3BoxNameIsMappedOrRefused()
    {
        using var session = Session(GameVersion.E, LanguageID.Japanese, "ソフ");
        BoxLayoutService.Rename(session, 0, "Team 1");
        Assert.Equal("Ｔｅａｍ　１", session.GetBoxName(0));
        Assert.Throws<NameNotStorableException>(() => BoxLayoutService.Rename(session, 0, "Équipe"));
        Assert.Equal("Ｔｅａｍ　１", session.GetBoxName(0));
    }

    [Fact]
    public void HackEditsUseTheInternationalGen3Charset()
    {
        var edit = NameCharset.FitGen3Edit(new EntityEdit(Nickname: "Ｂｒｕｈ", OriginalTrainer: "Sof"));
        Assert.Equal("Bruh", edit.Nickname);
        Assert.Equal("Sof", edit.OriginalTrainer);
        Assert.Throws<NameNotStorableException>(() => NameCharset.FitGen3Edit(new EntityEdit(Nickname: "犬")));
    }

    [Fact]
    public void EmptyNicknameStillClearsIt()
    {
        using var session = Session(GameVersion.E, LanguageID.Japanese, "ソフ");
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: "TEST"));
        session.ApplyEdit(0, 0, new EntityEdit(Nickname: ""));
        Assert.False(session.SaveFile.GetBoxSlotAtIndex(0, 0).IsNicknamed);
    }
}
