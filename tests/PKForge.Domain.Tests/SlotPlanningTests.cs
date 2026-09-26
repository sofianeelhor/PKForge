using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

public sealed class SlotPlanningTests
{
    private static SlotSummary Slot(int box, int slot, int? species = null) => new(box, slot, species, null, false, true);

    private static List<SlotSummary> Boxes(int count, params (int Box, int Slot)[] taken) =>
        [.. from box in Enumerable.Range(-1, count + 1)
            from slot in Enumerable.Range(0, box < 0 ? 6 : 30)
            select Slot(box, slot, taken.Contains((box, slot)) ? 25 : null)];

    [Fact]
    public void FreeSlotsStartAtTheChosenSlotAndSkipTheTakenOnes()
    {
        var slots = Boxes(10, (7, 6), (7, 8));
        var free = SlotPlanning.FreeSlotsFrom(slots, new SlotRef(7, 6)).Take(4).ToList();
        Assert.Equal([new SlotRef(7, 7), new SlotRef(7, 9), new SlotRef(7, 10), new SlotRef(7, 11)], free);
    }

    [Fact]
    public void AFullTailRunsIntoTheNextBoxesButNeverBack()
    {
        var slots = Boxes(3, [.. Enumerable.Range(20, 10).Select(s => (1, s))]);
        var free = SlotPlanning.FreeSlotsFrom(slots, new SlotRef(1, 20)).ToList();
        Assert.Equal(new SlotRef(2, 0), free[0]);
        Assert.All(free, s => Assert.True(s.Box >= 2));
        Assert.Empty(SlotPlanning.FreeSlotsFrom(Boxes(3, [.. Enumerable.Range(0, 30).Select(s => (2, s))]), new SlotRef(2, 0)));
    }

    [Fact]
    public void NoStartMeansTheFirstFreeBoxSlotAndThePartyIsNeverUsed()
    {
        var slots = Boxes(2, (0, 0));
        Assert.Equal(new SlotRef(0, 1), SlotPlanning.FreeSlotsFrom(slots, null).First());
        Assert.DoesNotContain(SlotPlanning.FreeSlotsFrom(slots, null), s => s.Box < 0);
    }
}
