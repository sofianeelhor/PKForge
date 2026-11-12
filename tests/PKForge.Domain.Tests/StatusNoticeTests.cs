using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The box browser never showed its status line, so a refused move or a finished save said
/// nothing at all. The line is now shown as a notice, and its tone comes from the sentence.
/// </summary>
public sealed class StatusNoticeTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ready")]
    [InlineData("READY")]
    [InlineData("Ready · Hardcore mode is on")]
    [InlineData("Applying edit…")]
    [InlineData("Validating, backing up, writing…")]
    [InlineData("Editor focus - Species · A use · B box")]
    [InlineData("Box manager - A hold · L/R browse · Y mark box · X actions")]
    [InlineData("Holding box 03 - L/R swap · A drop")]
    [InlineData("MULTI-SELECT - 3 MARKED")]
    [InlineData("Carrying 3 Pokémon. L/R change box, A sets them down from the cursor, B puts them back.")]
    [InlineData("Put back. 3 still marked.")]
    [InlineData("Moved.")]
    [InlineData("Moved 3 Pokémon from box 1 slot 22 on.")]
    [InlineData("MULTI-SELECT - MARKING 4 · RELEASE TO APPLY")]
    public void IdleLinesHintsAndWorkInProgressStayQuiet(string? status) =>
        Assert.Equal(NoticeTone.Quiet, StatusNotice.ToneOf(status));

    [Theory]
    [InlineData("Saved.")]
    [InlineData("Restored.")]
    public void AWriteThatHappenedIsGoodNews(string status) =>
        Assert.Equal(NoticeTone.Success, StatusNotice.ToneOf(status));

    // A write no longer names its restore point: the screen says it wrote, and that is the good news.
    [Theory]
    [InlineData("Random team of 6 placed in the boxes.")]
    [InlineData("Copied 1 Pokémon to the Bank.")]
    [InlineData("Moved 3 Pokémon to the Bank.")]
    public void AReportedWriteIsGoodNewsWithoutNamingItsRestorePoint(string status) =>
        Assert.Equal(NoticeTone.Success, StatusNotice.ToneOf(status, wrote: true));

    [Fact]
    public void ARefusalInsideAWriteReportStillReadsAsOne() =>
        Assert.Equal(NoticeTone.Problem, StatusNotice.ToneOf("Legalized 2 Pokémon. 1 could not be fixed.", wrote: true));

    [Fact]
    public void ShortNoticesLeaveSoonerAndRefusalsStayLonger()
    {
        var saved = StatusNotice.HoldMs("Saved.", NoticeTone.Success);
        var longer = StatusNotice.HoldMs("Pikachu joined HeartGold (box 3). It kept its moves and its ball.", NoticeTone.Success);
        var refusal = StatusNotice.HoldMs("Not saved: the file is gone.", NoticeTone.Problem);
        Assert.InRange(saved, 1400, 1700);
        Assert.True(longer > saved);
        Assert.True(refusal >= 2800);
        Assert.True(StatusNotice.HoldMs(new string('x', 500), NoticeTone.Info) <= 4000);
    }

    [Theory]
    [InlineData("Write aborted: Box 1, slot 4 changed although this edit did not target it.")]
    [InlineData("Move aborted: the save was closed before the move was written.")]
    [InlineData("No free slot from box 3 slot 1 onward.")]
    [InlineData("Not enough empty box slots (6 needed).")]
    [InlineData("The party is full. Move a Pokémon to a box first, or roll into the boxes.")]
    [InlineData("No empty slot in the connected game.")]
    [InlineData("Pikachu cannot go to the connected game. The species is not in this game.")]
    [InlineData("Box 2, slot 5 is no longer empty.")]
    [InlineData("Nothing to save.")]
    [InlineData("No save connected.")]
    public void ARefusalIsAProblemThePlayerMustSee(string status) =>
        Assert.Equal(NoticeTone.Problem, StatusNotice.ToneOf(status));

    [Theory]
    [InlineData("Nothing changed - no write, no restore point.")]
    [InlineData("No changes to save.")]
    [InlineData("Pokédex editor closed.")]
    public void EverythingElseIsPlainInformation(string status) =>
        Assert.Equal(NoticeTone.Info, StatusNotice.ToneOf(status));
}
