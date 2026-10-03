using Huishoudplanner.Domain.CyclePlans;

namespace Huishoudplanner.Domain.Tests.CyclePlans;

/// <summary>Port of <c>domain/slots.ts</c> (<c>sortSlots</c>, <c>diffSlots</c>); the Node server has no unit test for it, so these are direct tests.</summary>
public sealed class CyclePlanSlotsTests
{
    private const string A = "a00000000000000000000001";
    private const string B = "b00000000000000000000002";
    private const string Anna = "0000000000000000000000a1";
    private const string Bram = "0000000000000000000000b2";

    private static CyclePlanSlot Slot(string task, int week, int weekday, string? assignee = null, int order = 0) => new(task, week, weekday, assignee, order);

    [Fact]
    public void Sort_orders_by_week_then_monday_first_weekday_then_sort_order_then_task()
    {
        var sorted = CyclePlanSlots.Sort(
        [
            Slot(B, 1, 1),
            Slot(A, 0, 0), // Sunday is the last day of week 0
            Slot(B, 0, 1, order: 2),
            Slot(A, 0, 1, order: 2),
            Slot(B, 0, 1, order: 1),
        ]);

        sorted.Select(s => (s.TaskId, s.WeekIndex, s.Weekday, s.SortOrder)).Should().Equal(
            (B, 0, 1, 1),
            (A, 0, 1, 2),
            (B, 0, 1, 2),
            (A, 0, 0, 0),
            (B, 1, 1, 0));
    }

    [Fact]
    public void Sort_does_not_change_its_input()
    {
        var input = new[] { Slot(B, 1, 1), Slot(A, 0, 1) };

        _ = CyclePlanSlots.Sort(input);

        input[0].TaskId.Should().Be(B);
    }

    [Fact]
    public void Diff_of_equal_lists_is_empty()
    {
        var slots = new[] { Slot(A, 0, 1, Anna), Slot(B, 1, 2) };

        CyclePlanSlots.Diff(slots, [.. slots]).IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Diff_names_added_removed_and_changed_slots_by_task_week_and_weekday()
    {
        var before = new[] { Slot(A, 0, 1, Anna), Slot(B, 0, 3), Slot(B, 1, 5) };
        var after = new[] { Slot(A, 0, 1, Bram), Slot(B, 1, 5), Slot(B, 2, 5) };

        var diff = CyclePlanSlots.Diff(before, after);

        diff.Added.Should().Equal(Slot(B, 2, 5));
        diff.Removed.Should().Equal(Slot(B, 0, 3));
        diff.Changed.Should().ContainSingle().Which.Should().Be((Slot(A, 0, 1, Anna), Slot(A, 0, 1, Bram)));
    }

    [Fact]
    public void Diff_counts_a_changed_sort_order_as_a_change_and_the_assignee_going_to_null_too()
    {
        var diff = CyclePlanSlots.Diff([Slot(A, 0, 1, Anna), Slot(B, 0, 1)], [Slot(A, 0, 1), Slot(B, 0, 1, order: 3)]);

        diff.Changed.Select(c => c.After.TaskId).Should().Equal(A, B);
    }

    [Fact]
    public void KeyOf_is_task_week_and_weekday()
    {
        CyclePlanSlots.KeyOf(Slot(A, 2, 5, Anna)).Should().Be($"{A}:2:5");
    }
}
