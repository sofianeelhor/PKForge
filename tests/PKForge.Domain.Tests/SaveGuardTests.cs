using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// Hardcore mode's decision table: off allows everything; on keeps the management
/// actions (move, release, export, backup) and refuses every edit, fabrication and copy.
/// </summary>
public sealed class SaveGuardTests
{
    private static readonly SaveAction[] Management =
        [SaveAction.Move, SaveAction.Release, SaveAction.ExportFile, SaveAction.Backup];

    public static TheoryData<SaveAction> AllActions()
    {
        var data = new TheoryData<SaveAction>();
        foreach (var action in Enum.GetValues<SaveAction>()) data.Add(action);
        return data;
    }

    public static TheoryData<SaveAction> DataChangingActions()
    {
        var data = new TheoryData<SaveAction>();
        foreach (var action in Enum.GetValues<SaveAction>().Except(Management)) data.Add(action);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllActions))]
    public void Off_allows_every_action(SaveAction action)
    {
        Assert.True(SaveGuard.Off.Allows(action));
        Assert.False(SaveGuard.Off.Blocks(action));
    }

    [Theory]
    [InlineData(SaveAction.Move)]
    [InlineData(SaveAction.Release)]
    [InlineData(SaveAction.ExportFile)]
    [InlineData(SaveAction.Backup)]
    public void Hardcore_keeps_management(SaveAction action) =>
        Assert.True(SaveGuard.Hardcore.Allows(action));

    [Theory]
    [MemberData(nameof(DataChangingActions))]
    public void Hardcore_refuses_every_data_change(SaveAction action)
    {
        Assert.False(SaveGuard.Hardcore.Allows(action));
        Assert.True(SaveGuard.Hardcore.Blocks(action));
    }

    [Fact]
    public void Hardcore_moves_but_never_copies()
    {
        var guard = SaveGuard.Hardcore;
        Assert.True(guard.CanMove);
        Assert.False(guard.CanDuplicate);
        Assert.True(guard.CanMoveOnly);
        Assert.False(SaveGuard.Off.CanMoveOnly);
    }

    [Fact]
    public void Hardcore_properties_match_the_table()
    {
        var guard = SaveGuard.Hardcore;
        Assert.False(guard.CanEditMon);
        Assert.False(guard.CanCreateMon);
        Assert.False(guard.CanBatchEdit);
        Assert.False(guard.CanInjectEvent);
        Assert.False(guard.CanEditInventory);
        Assert.False(guard.CanEditTrainer);
        Assert.False(guard.CanEditDex);
        Assert.False(guard.CanEditWorld);
        Assert.False(guard.CanWriteRawBytes);
        Assert.False(guard.CanRepairRtc);
        Assert.True(guard.CanRelease);
        Assert.True(guard.CanExport);
        Assert.True(guard.CanBackup);
    }

    [Fact]
    public void Hardcore_refuses_world_state_edits_and_off_allows_them()
    {
        Assert.True(SaveGuard.Hardcore.Blocks(SaveAction.EditWorld));
        Assert.True(SaveGuard.Off.CanEditWorld);
    }

    [Fact]
    public void For_maps_the_setting_to_the_shared_guards()
    {
        Assert.Same(SaveGuard.Hardcore, SaveGuard.For(true));
        Assert.Same(SaveGuard.Off, SaveGuard.For(false));
        Assert.True(SaveGuard.For(true).IsHardcore);
        Assert.False(SaveGuard.For(false).IsHardcore);
    }
}
