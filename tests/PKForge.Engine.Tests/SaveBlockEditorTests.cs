using PKForge.Engine;
using System.Reflection;
using PKHeX.Core;
using Xunit;

namespace PKForge.Engine.Tests;

/// <summary>Gen 8/9 SCBlock editor on blank saves: listing, bool flips, typed numeric writes,
/// and refusal of anything that would not fit the block's own type.</summary>
public sealed class SaveBlockEditorTests
{
    /// <summary>PKHeX's blank Switch saves carry every block untyped (<see cref="SCTypeCode.None"/>),
    /// so each test types a few of them the way a real save stores them. A boolean block has no
    /// payload and blank blocks all have one, so the first block is swapped (same key, same slot,
    /// keeping the accessor's key order) for a data-less Bool1 built through PKHeX's own
    /// internal constructor.</summary>
    private static SaveEngineSession Blank(GameVersion version)
    {
        var save = BlankSaveFile.Get(version, "PKForge", LanguageID.English);
        var blocks = ((ISCBlockArray)save).AllBlocks;
        var boolean = (SCBlock)Activator.CreateInstance(typeof(SCBlock),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [blocks[0].Key, SCTypeCode.Bool1], null)!;
        ((IList<SCBlock>)blocks)[0] = boolean;
        blocks.First(b => b.Type == SCTypeCode.None && b.Data.Length == 4).ChangeStoredType(SCTypeCode.UInt32);
        blocks.First(b => b.Type == SCTypeCode.None && b.Data.Length == 4).ChangeStoredType(SCTypeCode.Single);
        return new SaveEngineSession(save, null);
    }

    [Theory]
    [InlineData(GameVersion.SW)]
    [InlineData(GameVersion.PLA)]
    [InlineData(GameVersion.SL)]
    public void ListsBlocksWithEditableSubset(GameVersion version)
    {
        using var session = Blank(version);
        Assert.True(SaveBlockEditorService.IsSupported(session));
        var blocks = SaveBlockEditorService.GetBlocks(session);
        Assert.NotEmpty(blocks);
        Assert.Contains(blocks, b => b.Editable && b.Type == "Bool");
        Assert.Contains(blocks, b => b.Editable && b.Type != "Bool");
        Assert.All(blocks.Where(b => b.Type is "Object" or "None" || b.Type.EndsWith("[]")), b => Assert.False(b.Editable));
    }

    [Fact]
    public void BoolBlockFlipsAndPersistsInTheSave()
    {
        using var session = Blank(GameVersion.SL);
        var entry = SaveBlockEditorService.GetBlocks(session).First(b => b.Editable && b.Type == "Bool");
        var target = entry.Value != "true";
        Assert.True(SaveBlockEditorService.SetBool(session, entry.Key, target).Success);

        var block = ((ISCBlockArray)session.SaveFile).Accessor.GetBlock(entry.Key);
        Assert.Equal(target ? SCTypeCode.Bool2 : SCTypeCode.Bool1, block.Type);
        Assert.Equal(target ? "true" : "false", SaveBlockEditorService.GetBlocks(session).Single(b => b.Key == entry.Key).Value);
    }

    [Fact]
    public void NumericBlockTakesItsOwnTypeOnly()
    {
        using var session = Blank(GameVersion.SL);
        var entry = SaveBlockEditorService.GetBlocks(session).First(b => b.Type == nameof(SCTypeCode.UInt32));
        Assert.True(SaveBlockEditorService.SetNumber(session, entry.Key, "123456").Success);
        Assert.Equal(123456u, ((ISCBlockArray)session.SaveFile).Accessor.GetBlock(entry.Key).GetValue());

        Assert.False(SaveBlockEditorService.SetNumber(session, entry.Key, "-1").Success);
        Assert.False(SaveBlockEditorService.SetNumber(session, entry.Key, "4294967296").Success);
        Assert.False(SaveBlockEditorService.SetNumber(session, entry.Key, "abc").Success);
        Assert.False(SaveBlockEditorService.SetBool(session, entry.Key, true).Success);
        Assert.Equal(123456u, ((ISCBlockArray)session.SaveFile).Accessor.GetBlock(entry.Key).GetValue());
    }

    [Fact]
    public void FloatBlocksRefuseNonFiniteValues()
    {
        using var session = Blank(GameVersion.SL);
        var entry = SaveBlockEditorService.GetBlocks(session).First(b => b.Type == nameof(SCTypeCode.Single));
        Assert.False(SaveBlockEditorService.SetNumber(session, entry.Key, "NaN").Success);
        Assert.True(SaveBlockEditorService.SetNumber(session, entry.Key, "1.5").Success);
        Assert.Equal(1.5f, ((ISCBlockArray)session.SaveFile).Accessor.GetBlock(entry.Key).GetValue());
    }

    [Fact]
    public void PayloadBlocksAndOldSavesAreRefused()
    {
        using var session = Blank(GameVersion.SL);
        var obj = SaveBlockEditorService.GetBlocks(session).First(b => b.Type == "None" && b.Size > 8);
        Assert.False(SaveBlockEditorService.SetNumber(session, obj.Key, "1").Success);
        Assert.False(SaveBlockEditorService.SetBool(session, obj.Key, true).Success);

        using var gen7 = new SaveEngineSession(BlankSaveFile.Get(GameVersion.SN, "PKForge", LanguageID.English), null);
        Assert.False(SaveBlockEditorService.IsSupported(gen7));
        Assert.Empty(SaveBlockEditorService.GetBlocks(gen7));
    }
}
