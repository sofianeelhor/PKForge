using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>Storage tools listed about twenty tools flat; they are now grouped by what they do, each once.</summary>
public sealed class StorageToolsMenuTests
{
    [Fact]
    public void Everything_allowed_shows_every_tool_once_in_five_groups()
    {
        var groups = StorageToolsMenu.Build(new StorageToolsCapabilities(BoxTools: true, CreateMon: true, BatchEdit: true));
        Assert.Equal(["Organise", "Add", "Edit many", "Find", "Check & share"], groups.Select(g => g.Title));
        var tools = groups.SelectMany(g => g.Rows).Select(r => r.Entry).ToList();
        Assert.Equal(tools.Count, tools.Distinct().Count());
        Assert.Equal(Enum.GetValues<StorageTool>().Length, tools.Count);
    }

    [Fact]
    public void Living_dex_is_hidden_when_no_bundle_fits_the_game()
    {
        var tools = StorageToolsMenu.Build(new StorageToolsCapabilities(BoxTools: true, CreateMon: true, BatchEdit: true, LivingDex: false))
            .SelectMany(g => g.Rows).Select(r => r.Entry).ToList();
        Assert.DoesNotContain(StorageTool.LivingDex, tools);
        Assert.Contains(StorageTool.EggFactory, tools);
    }

    [Fact]
    public void Hardcore_mode_keeps_only_the_tools_that_change_nothing()
    {
        var tools = StorageToolsMenu.Build(new StorageToolsCapabilities(BoxTools: true))
            .SelectMany(g => g.Rows).Select(r => r.Entry).ToList();
        Assert.DoesNotContain(StorageTool.ImportFiles, tools);
        Assert.DoesNotContain(StorageTool.BatchEditor, tools);
        Assert.Contains(StorageTool.LegalityCheck, tools);
        Assert.Contains(StorageTool.DayCare, tools);
    }

    [Fact]
    public void A_romhack_session_hides_the_bulk_box_tools()
    {
        var tools = StorageToolsMenu.Build(new StorageToolsCapabilities(CreateMon: true, BatchEdit: true))
            .SelectMany(g => g.Rows).Select(r => r.Entry).ToList();
        Assert.DoesNotContain(StorageTool.SortBoxes, tools);
        Assert.DoesNotContain(StorageTool.BatchEditor, tools);
        Assert.Contains(StorageTool.Presets, tools);
    }

    [Fact]
    public void Every_tool_has_its_own_icon()
    {
        var icons = StorageToolsMenu.Build(new StorageToolsCapabilities(true, true, true))
            .SelectMany(g => g.Rows).Select(r => r.Icon).ToList();
        Assert.Equal(icons.Count, icons.Distinct().Count());
    }
}
