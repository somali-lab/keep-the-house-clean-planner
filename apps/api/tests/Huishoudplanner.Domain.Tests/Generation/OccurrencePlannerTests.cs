using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Domain.Tests.Generation;

/// <summary>
/// Direct tests of the pure generation rules of <c>domain/generation.ts</c> (<c>plannedOccurrences</c>, <c>isInVacation</c>) and of
/// the snapshots a generated occurrence carries (ADR-0011). The TypeScript function takes database documents (ObjectIds), not plain
/// data, so there are no golden vectors: the scenarios of <c>generation.test.ts</c> that are about dates are ported here instead.
/// Anchor 2026-09-14 (Monday): cycle 0 is 14 Sep to 11 Oct, cycle 1 is 12 Oct to 8 Nov and contains the end of DST (25 Oct).
/// </summary>
public sealed class OccurrencePlannerTests
{
    private const string Badkamer = "a00000000000000000000001";
    private const string Wastafel = "a00000000000000000000002";
    private const string Room = "b00000000000000000000001";
    private const string Anna = "c00000000000000000000001";

    private static readonly DateTimeOffset Now = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    private static HouseholdTask Task(string id, string name, bool active = true, int minutes = 30, string room = Room) =>
        new(id, name, room, "1w", minutes, minutes, null, active, string.Empty, [], null, Now, Now);

    private static Dictionary<string, HouseholdTask> Tasks(params HouseholdTask[] tasks) => tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);

    private static CyclePlanSlot Slot(string task, int week, int weekday, string? assignee = null) => new(task, week, weekday, assignee);

    private static HouseholdSettings Settings(DateOnly? anchor = null, params VacationRange[] vacations) =>
        SettingsDefaults.ForNewInstallation("Europe/Amsterdam", anchor ?? new DateOnly(2026, 9, 14), Now) with { VacationRanges = vacations };

    [Fact]
    public void Plan_places_a_slot_on_the_weekday_of_its_week_in_the_cycle()
    {
        var planned = OccurrencePlanner.Plan([Slot(Badkamer, 2, 5, Anna)], 0, Settings(), Tasks(Task(Badkamer, "Badkamer")), new DateOnly(2026, 9, 14));

        var only = planned.Should().ContainSingle().Subject;
        only.Day.Should().Be(new DateOnly(2026, 10, 2));
        only.CycleIndex.Should().Be(0);
        only.AssigneeId.Should().Be(Anna);
        only.Task.Name.Should().Be("Badkamer");
    }

    [Fact]
    public void Plan_treats_sunday_as_the_last_day_of_the_week_and_stays_correct_across_the_end_of_daylight_saving_time()
    {
        // Cycle 1 starts on 12 Oct: week 1 Sunday is 25 Oct (DST ends), week 2 Monday is 26 Oct.
        var planned = OccurrencePlanner.Plan([Slot(Badkamer, 1, 0), Slot(Badkamer, 2, 1)], 1, Settings(), Tasks(Task(Badkamer, "Badkamer")), new DateOnly(2026, 9, 14));

        planned.Select(p => p.Day).Should().Equal(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26));
    }

    [Fact]
    public void Plan_skips_days_before_today_but_keeps_today()
    {
        var slots = new[] { Slot(Badkamer, 0, 1), Slot(Badkamer, 0, 3), Slot(Badkamer, 0, 4) }; // Mon 14, Wed 16, Thu 17 Sep

        var planned = OccurrencePlanner.Plan(slots, 0, Settings(), Tasks(Task(Badkamer, "Badkamer")), new DateOnly(2026, 9, 16));

        planned.Select(p => p.Day).Should().Equal(new DateOnly(2026, 9, 16), new DateOnly(2026, 9, 17));
    }

    [Fact]
    public void Plan_skips_inactive_and_unknown_tasks()
    {
        var slots = new[] { Slot(Badkamer, 0, 3), Slot(Wastafel, 0, 4), Slot("ffffffffffffffffffffffff", 0, 5) };

        var planned = OccurrencePlanner.Plan(slots, 0, Settings(), Tasks(Task(Badkamer, "Badkamer"), Task(Wastafel, "Oud", active: false)), new DateOnly(2026, 9, 14));

        planned.Select(p => p.Task.Id).Should().Equal(Badkamer);
    }

    [Fact]
    public void Plan_leaves_vacation_days_empty_and_includes_the_first_and_last_day_of_a_range()
    {
        var slots = Enumerable.Range(0, 7).Select(day => Slot(Badkamer, 0, day)).ToArray(); // 14 to 20 Sep
        var vacation = new VacationRange(new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 19));

        var planned = OccurrencePlanner.Plan(slots, 0, Settings(null, vacation), Tasks(Task(Badkamer, "Badkamer")), new DateOnly(2026, 9, 14));

        planned.Select(p => p.Day.Day).Should().BeEquivalentTo([14, 15, 16, 20]);
    }

    [Fact]
    public void IsInVacation_includes_both_ends()
    {
        var ranges = new[] { new VacationRange(new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 19)) };

        OccurrencePlanner.IsInVacation(new DateOnly(2026, 9, 16), ranges).Should().BeFalse();
        OccurrencePlanner.IsInVacation(new DateOnly(2026, 9, 17), ranges).Should().BeTrue();
        OccurrencePlanner.IsInVacation(new DateOnly(2026, 9, 19), ranges).Should().BeTrue();
        OccurrencePlanner.IsInVacation(new DateOnly(2026, 9, 20), ranges).Should().BeFalse();
    }

    [Fact]
    public void Plan_follows_the_cycle_start_of_a_moved_anchor()
    {
        var anchor = new DateOnly(2026, 9, 21);

        var planned = OccurrencePlanner.Plan([Slot(Badkamer, 0, 3)], 0, Settings(anchor), Tasks(Task(Badkamer, "Badkamer")), new DateOnly(2026, 9, 14));

        planned.Single().Day.Should().Be(new DateOnly(2026, 9, 23));
    }

    [Fact]
    public void Plan_keeps_the_order_of_the_slots()
    {
        var planned = OccurrencePlanner.Plan([Slot(Wastafel, 0, 5), Slot(Badkamer, 0, 2)], 0, Settings(), Tasks(Task(Badkamer, "Badkamer"), Task(Wastafel, "Wastafel")), new DateOnly(2026, 9, 14));

        planned.Select(p => p.Task.Id).Should().Equal(Wastafel, Badkamer);
    }

    [Fact]
    public void ToDraft_snapshots_name_duration_room_and_assignee_and_plans_at_local_midnight()
    {
        var planned = new PlannedOccurrence(0, new DateOnly(2026, 10, 2), Task(Badkamer, "Badkamer", minutes: 45), Anna);
        var roomNames = new Dictionary<string, string> { [Room] = "Badkamer (ruimte)" };

        var draft = OccurrencePlanner.ToDraft(planned, "d00000000000000000000001", "e00000000000000000000001", Amsterdam, roomNames, Now);

        draft.TaskId.Should().Be(Badkamer);
        draft.CycleId.Should().Be("d00000000000000000000001");
        draft.PlanId.Should().Be("e00000000000000000000001");
        draft.AssigneeId.Should().Be(Anna);
        draft.DurationMinutesSnapshot.Should().Be(45);
        draft.TaskNameSnapshot.Should().Be("Badkamer");
        draft.RoomIdSnapshot.Should().Be(Room);
        draft.RoomNameSnapshot.Should().Be("Badkamer (ruimte)");
        draft.Date.Should().Be(new DateTimeOffset(2026, 10, 1, 22, 0, 0, TimeSpan.Zero));
        draft.CreatedAt.Should().Be(Now);
    }

    [Fact]
    public void ToDraft_stores_midnight_on_both_sides_of_the_end_of_daylight_saving_time()
    {
        PlannedOccurrence At(DateOnly day) => new(1, day, Task(Badkamer, "Badkamer"), null);
        var rooms = new Dictionary<string, string>();

        OccurrencePlanner.ToDraft(At(new DateOnly(2026, 10, 25)), "d00000000000000000000001", "e00000000000000000000001", Amsterdam, rooms, Now)
            .Date.Should().Be(new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero));
        OccurrencePlanner.ToDraft(At(new DateOnly(2026, 10, 26)), "d00000000000000000000001", "e00000000000000000000001", Amsterdam, rooms, Now)
            .Date.Should().Be(new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ToDraft_has_no_room_name_when_the_room_is_unknown()
    {
        var planned = new PlannedOccurrence(0, new DateOnly(2026, 9, 16), Task(Badkamer, "Badkamer"), null);

        var draft = OccurrencePlanner.ToDraft(planned, "d00000000000000000000001", "e00000000000000000000001", Amsterdam, new Dictionary<string, string>(), Now);

        draft.RoomIdSnapshot.Should().Be(Room);
        draft.RoomNameSnapshot.Should().BeNull();
    }
}
