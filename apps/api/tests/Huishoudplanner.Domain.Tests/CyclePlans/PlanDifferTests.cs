using Huishoudplanner.Domain.CyclePlans;

namespace Huishoudplanner.Domain.Tests.CyclePlans;

/// <summary>
/// Port of <c>domain/planDiff.ts</c>. It is not in <c>packages/shared</c> and has no TypeScript unit test (only the HTTP diff of the
/// activation flow touches it), so there are no golden vectors: these are direct tests of each pairing step.
/// </summary>
public sealed class PlanDifferTests
{
    private const string Dishes = "d00000000000000000000001";
    private const string Floor = "f00000000000000000000002";
    private const string Gone = "9e00000000000000000000e3";
    private const string Anna = "0000000000000000000000a1";
    private const string Bram = "0000000000000000000000b2";

    private static readonly Dictionary<string, PlanTaskInfo> Tasks = new()
    {
        [Dishes] = new("Afwas", "Keuken", 15),
        [Floor] = new("Vloer", null, 30),
    };

    private static CyclePlanSlot Slot(string task, int week, int weekday, string? assignee = null) => new(task, week, weekday, assignee);

    private static PlanDiff Diff(CyclePlanSlot[] before, CyclePlanSlot[] after) => PlanDiffer.Diff(before, after, Tasks);

    [Fact]
    public void Identical_plans_are_all_unchanged()
    {
        var slots = new[] { Slot(Dishes, 0, 1, Anna), Slot(Floor, 1, 2) };

        var diff = Diff(slots, [.. slots]);

        diff.Unchanged.Should().Be(2);
        diff.Added.Should().BeEmpty();
        diff.Removed.Should().BeEmpty();
        diff.Moved.Should().BeEmpty();
    }

    [Fact]
    public void A_slot_only_in_the_new_plan_is_added_with_the_task_and_room_names()
    {
        var diff = Diff([], [Slot(Dishes, 2, 5, Bram)]);

        diff.Added.Should().ContainSingle().Which.Should().Be(new DiffSlot(Dishes, "Afwas", "Keuken", 15, new DiffPosition(2, 5, Bram)));
    }

    [Fact]
    public void A_slot_only_in_the_old_plan_is_removed()
    {
        var diff = Diff([Slot(Floor, 0, 3)], []);

        diff.Removed.Should().ContainSingle().Which.Should().Be(new DiffSlot(Floor, "Vloer", null, 30, new DiffPosition(0, 3, null)));
    }

    [Fact]
    public void The_same_day_with_another_assignee_is_a_move_not_an_add_and_a_remove()
    {
        var diff = Diff([Slot(Dishes, 0, 1, Anna)], [Slot(Dishes, 0, 1, Bram)]);

        diff.Moved.Should().ContainSingle().Which.Should().Be(
            new MovedSlot(Dishes, "Afwas", "Keuken", 15, new DiffPosition(0, 1, Anna), new DiffPosition(0, 1, Bram)));
        diff.Added.Should().BeEmpty();
        diff.Removed.Should().BeEmpty();
    }

    [Fact]
    public void An_assignee_that_appears_or_disappears_counts_as_another_assignee()
    {
        var diff = Diff([Slot(Dishes, 0, 1, Anna)], [Slot(Dishes, 0, 1)]);

        diff.Unchanged.Should().Be(0);
        diff.Moved.Should().ContainSingle();
    }

    [Fact]
    public void The_remaining_slots_of_a_task_are_paired_in_cycle_order_and_the_surplus_is_added_or_removed()
    {
        // Monday first: Sunday (0) of week 0 comes after Friday (5) of week 0.
        var before = new[] { Slot(Dishes, 0, 0), Slot(Dishes, 0, 5), Slot(Dishes, 1, 2) };
        var after = new[] { Slot(Dishes, 0, 3), Slot(Dishes, 2, 4) };

        var diff = Diff(before, after);

        diff.Moved.Select(m => (m.From.WeekIndex, m.From.Weekday, m.To.WeekIndex, m.To.Weekday)).Should().Equal(
            (0, 5, 0, 3),
            (0, 0, 2, 4));
        diff.Removed.Should().ContainSingle().Which.Position.Should().Be(new DiffPosition(1, 2, null));
        diff.Added.Should().BeEmpty();
    }

    [Fact]
    public void Identical_slots_are_matched_before_same_day_moves()
    {
        var before = new[] { Slot(Dishes, 0, 1, Anna), Slot(Dishes, 0, 1, Bram) };
        var after = new[] { Slot(Dishes, 0, 1, Bram), Slot(Dishes, 0, 1, Anna) };

        var diff = Diff(before, after);

        diff.Unchanged.Should().Be(2);
        diff.Moved.Should().BeEmpty();
    }

    [Fact]
    public void Tasks_are_compared_independently()
    {
        var diff = Diff([Slot(Dishes, 0, 1), Slot(Floor, 0, 1)], [Slot(Dishes, 0, 1), Slot(Floor, 0, 2)]);

        diff.Unchanged.Should().Be(1);
        diff.Moved.Should().ContainSingle().Which.TaskId.Should().Be(Floor);
    }

    [Fact]
    public void A_task_that_no_longer_exists_shows_its_id_with_no_room_and_no_minutes()
    {
        var diff = Diff([Slot(Gone, 0, 1)], []);

        diff.Removed.Should().ContainSingle().Which.Should().Be(new DiffSlot(Gone, Gone, null, 0, new DiffPosition(0, 1, null)));
    }
}
