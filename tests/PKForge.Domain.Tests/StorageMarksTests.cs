using PKForge.Domain;
using Xunit;

namespace PKForge.Domain.Tests;

/// <summary>
/// The storage multi-select: what each gesture marks, that marks survive box changes
/// (they are box-qualified), and that the party never rides along with a box-wide sweep.
/// </summary>
public sealed class StorageMarksTests
{
    private const int Columns = 6;

    /// <summary>Two 30-slot boxes and a party. Box 0: slots 0-5 and 12 occupied. Box 1: slots 0, 7.
    /// Party: slots 0 and 1 occupied.</summary>
    private static readonly SlotSummary[] Slots = Build();

    private static SlotSummary[] Build()
    {
        var occupied = new HashSet<(int, int)> { (0, 0), (0, 1), (0, 2), (0, 3), (0, 4), (0, 5), (0, 12), (1, 0), (1, 7), (-1, 0), (-1, 1) };
        var list = new List<SlotSummary>();
        for (var box = 0; box < 2; box++)
            for (var slot = 0; slot < 30; slot++)
                list.Add(Slot(box, slot, occupied.Contains((box, slot))));
        for (var slot = 0; slot < 6; slot++)
            list.Add(Slot(-1, slot, occupied.Contains((-1, slot))));
        return [.. list];
    }

    private static SlotSummary Slot(int box, int slot, bool occupied) =>
        new(box, slot, occupied ? 25 : null, null, false, true);

    [Fact]
    public void Toggle_flips_an_occupied_slot_and_ignores_an_empty_one()
    {
        var marks = new StorageMarks();
        Assert.True(marks.Toggle(Slots, 0, 3));
        Assert.True(marks.Contains(0, 3));
        Assert.True(marks.Toggle(Slots, 0, 3));
        Assert.False(marks.Contains(0, 3));
        Assert.False(marks.Toggle(Slots, 0, 29));
        Assert.Equal(0, marks.Count);
    }

    [Fact]
    public void Marks_persist_across_boxes()
    {
        var marks = new StorageMarks();
        marks.Toggle(Slots, 0, 1);
        marks.Toggle(Slots, 1, 7);
        Assert.Equal(2, marks.Count);
        Assert.Equal(1, marks.CountIn(0));
        Assert.Equal(1, marks.CountIn(1));
        Assert.Equal(new[] { (0, 1), (1, 7) }, marks.Ordered);
    }

    [Fact]
    public void TogglePage_marks_the_whole_box_then_clears_it_leaving_other_boxes()
    {
        var marks = new StorageMarks();
        marks.Toggle(Slots, 1, 0);
        Assert.True(marks.TogglePage(Slots, 0));
        Assert.Equal(7, marks.CountIn(0));
        Assert.True(marks.IsPageFullyMarked(Slots, 0));

        Assert.False(marks.TogglePage(Slots, 0));
        Assert.Equal(0, marks.CountIn(0));
        Assert.True(marks.Contains(1, 0));
    }

    [Fact]
    public void TogglePage_on_a_partly_marked_box_completes_it()
    {
        var marks = new StorageMarks();
        marks.Toggle(Slots, 0, 0);
        Assert.True(marks.TogglePage(Slots, 0));
        Assert.Equal(7, marks.CountIn(0));
    }

    [Fact]
    public void An_empty_page_is_never_fully_marked()
    {
        var marks = new StorageMarks();
        var emptyBox = Enumerable.Range(0, 30).Select(i => Slot(5, i, false)).ToArray();
        Assert.False(marks.IsPageFullyMarked(emptyBox, 5));
        Assert.False(marks.TogglePage(emptyBox, 5));
    }

    [Fact]
    public void MarkAllBoxes_excludes_the_party()
    {
        var marks = new StorageMarks();
        marks.MarkAllBoxes(Slots);
        Assert.Equal(9, marks.Count);
        Assert.Equal(0, marks.CountIn(-1));
    }

    [Fact]
    public void The_party_page_can_still_be_marked_on_purpose()
    {
        var marks = new StorageMarks();
        marks.MarkPage(Slots, -1);
        Assert.Equal(2, marks.CountIn(-1));
    }

    [Fact]
    public void MarkBoxes_marks_only_the_chosen_boxes_and_never_the_party()
    {
        var marks = new StorageMarks();
        marks.MarkBoxes(Slots, [1, -1]);
        Assert.Equal(new[] { (1, 0), (1, 7) }, marks.Ordered);
    }

    [Fact]
    public void InvertPage_flips_only_the_open_box()
    {
        var marks = new StorageMarks();
        marks.Toggle(Slots, 0, 0);
        marks.Toggle(Slots, 1, 0);
        marks.InvertPage(Slots, 0);
        Assert.False(marks.Contains(0, 0));
        Assert.Equal(6, marks.CountIn(0));
        Assert.True(marks.Contains(1, 0));
    }

    [Fact]
    public void Clear_drops_everything()
    {
        var marks = new StorageMarks();
        marks.MarkAllBoxes(Slots);
        marks.Clear();
        Assert.Equal(0, marks.Count);
    }

    [Fact]
    public void Rectangle_marks_the_occupied_slots_of_the_span_in_either_direction()
    {
        var marks = new StorageMarks();
        // Slot 14 (row 2, col 2) back to slot 1 (row 0, col 1): cols 1-2, rows 0-2.
        marks.SetRectangle(Slots, 0, 14, 1, Columns, mark: true);
        Assert.Equal(new[] { (0, 1), (0, 2) }, marks.Ordered); // 7, 8, 13, 14 are empty
    }

    [Fact]
    public void Rectangle_is_a_rectangle_not_a_reading_order_range()
    {
        var marks = new StorageMarks();
        // Slot 4 (row 0, col 4) to slot 12 (row 2, col 0): cols 0-4 - slot 5 stays out.
        marks.SetRectangle(Slots, 0, 4, 12, Columns, mark: true);
        Assert.False(marks.Contains(0, 5));
        Assert.Equal(6, marks.Count);
    }

    [Fact]
    public void Rectangle_can_erase_and_leaves_other_boxes_alone()
    {
        var marks = new StorageMarks();
        marks.MarkPage(Slots, 0);
        marks.Toggle(Slots, 1, 0);
        marks.SetRectangle(Slots, 0, 0, 2, Columns, mark: false);
        Assert.Equal(4, marks.CountIn(0));
        Assert.True(marks.Contains(1, 0));
    }

    [Fact]
    public void Rectangle_on_the_party_uses_its_two_columns()
    {
        var marks = new StorageMarks();
        marks.SetRectangle(Slots, -1, 0, 1, 2, mark: true);
        Assert.Equal(2, marks.CountIn(-1));
    }

    [Fact]
    public void Prune_forgets_marks_whose_slot_was_emptied()
    {
        var marks = new StorageMarks();
        marks.Toggle(Slots, 0, 0);
        marks.Toggle(Slots, 0, 1);
        var after = Slots.Select(s => s.Box == 0 && s.Slot == 0 ? s with { Species = null } : s).ToArray();
        marks.Prune(after);
        Assert.Equal(new[] { (0, 1) }, marks.Ordered);
    }

    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(7, 0, 6, true)]
    [InlineData(7, 0, 2, false)]
    [InlineData(29, 0, 17, true)]
    [InlineData(-1, 0, 0, false)]
    public void StorageRectangle_contains(int from, int to, int slot, bool expected) =>
        Assert.Equal(expected, StorageRectangle.Contains(from, to, slot, Columns));
}
