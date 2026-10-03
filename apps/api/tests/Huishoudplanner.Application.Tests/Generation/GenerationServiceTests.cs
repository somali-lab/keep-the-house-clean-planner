using Huishoudplanner.Application.Tests.CyclePlans;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Application.Tests.Generation;

/// <summary>
/// The generation use cases (<c>generation.test.ts</c>, the scenarios about generation itself): idempotency, the audit entries with the run
/// id, the snapshots, vacation days, inactive tasks and days before today, DST, the repair of stale occurrences after an anchor or slot
/// change, and the replacement rule. Anchor 2026-09-14 (Monday): cycle 0 is 14 Sep to 11 Oct, cycle 1 is 12 Oct to 8 Nov. The nightly and
/// manual job endpoints (<c>POST /jobs/generation</c>) belong to slice 6.3; the use cases behind them are called directly here.
/// </summary>
public sealed class GenerationServiceTests
{
    private static readonly DateTimeOffset MondayMorning = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static readonly AuditActor Nightly = AuditActor.System;

    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    private static readonly int[] MonWedFri = [1, 3, 5];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CyclePlanSlot Slot(string task, int week, int weekday, string? assignee = null) => new(task, week, weekday, assignee);

    private static CyclePlanSlot[] EveryWeek(string task, int weekday, string? assignee = null) =>
        [.. Enumerable.Range(0, 4).Select(week => Slot(task, week, weekday, assignee))];

    private static (CyclePlanWorld World, CyclePlan Plan, string Room) Arrange(DateTimeOffset? now = null, params CyclePlanSlot[] slots)
    {
        var world = new CyclePlanWorld();
        world.Clock.Now = now ?? MondayMorning;
        var room = world.Room("Badkamer");
        var plan = world.Plan("Standaard", active: true, daysAgo: 30, slots);
        return (world, plan, room.Id);
    }

    private static void SetSlots(CyclePlanWorld world, CyclePlan plan, params CyclePlanSlot[] slots)
    {
        var index = world.Plans.Items.FindIndex(p => p.Id == plan.Id);
        world.Plans.Items[index] = plan with { Slots = CyclePlanSlots.Sort(slots) };
    }

    private static List<DateOnly> Days(IEnumerable<Occurrence> occurrences) =>
        [.. occurrences.Select(o => DayKeys.ToDayKey(o.Date, Amsterdam)).Order()];

    private static List<AuditEntry> Entries(CyclePlanWorld world, AuditEntity entity, AuditAction action) =>
        [.. world.Audit.Entries.Where(e => e.Entity == entity && e.Action == action)];

    // ---- (a) idempotency

    [Fact]
    public async Task GenerateUpcoming_generates_the_current_and_the_next_cycle_and_a_second_run_inserts_and_audits_nothing()
    {
        var world = new CyclePlanWorld();
        world.Clock.Now = MondayMorning;
        var room = world.Room("Badkamer");
        var weekly = world.Task("Badkamer", room.Id).Id;
        world.Plan("Standaard", active: true, daysAgo: 30, EveryWeek(weekly, 3));

        var first = (await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct)).AsT0;

        first.Generated.Select(g => (g.CycleIndex, g.Inserted)).Should().Equal((0, 4), (1, 4));
        world.Occurrences.Items.Should().HaveCount(8);
        world.Cycles.Items.Should().HaveCount(2);
        var auditAfterFirst = world.Audit.Entries.Count;

        var second = (await world.Generation.GenerateUpcomingAsync(Nightly, "run-2", Ct)).AsT0;

        second.Generated.Select(g => (g.Inserted, g.Skipped)).Should().Equal((0, 4), (0, 4));
        second.Removed.Should().Be(0);
        world.Occurrences.Items.Should().HaveCount(8);
        world.Cycles.Items.Should().HaveCount(2);
        world.Audit.Entries.Should().HaveCount(auditAfterFirst, "a no-op run writes and audits nothing");
    }

    [Fact]
    public async Task GenerateUpcoming_does_not_let_an_ad_hoc_occurrence_on_a_slot_day_block_the_generated_one()
    {
        var (world, _, room) = Arrange();
        var weekly = world.Task("Badkamer", room).Id;
        SetSlots(world, world.Plans.Items[0], EveryWeek(weekly, 3));
        await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);
        var generated = world.Occurrences.Items.Single(o => DayKeys.ToDayKey(o.Date, Amsterdam) == new DateOnly(2026, 9, 16));
        var adhoc = generated with { Id = world.Occurrences.NextId(), Origin = OccurrenceOrigin.Adhoc, PlanId = null };
        world.Occurrences.Items.Remove(generated);
        world.Occurrences.Items.Add(adhoc);

        await world.Generation.GenerateUpcomingAsync(Nightly, "run-2", Ct);

        world.Occurrences.Items.Where(o => DayKeys.ToDayKey(o.Date, Amsterdam) == new DateOnly(2026, 9, 16)).Select(o => o.Origin)
            .Should().BeEquivalentTo([OccurrenceOrigin.Adhoc, OccurrenceOrigin.Generated]);
        var count = world.Occurrences.Items.Count;

        var again = (await world.Generation.GenerateUpcomingAsync(Nightly, "run-3", Ct)).AsT0;

        again.Removed.Should().Be(0);
        again.Generated.Select(g => g.Inserted).Should().Equal(0, 0);
        world.Occurrences.Items.Should().HaveCount(count);
    }

    [Fact]
    public async Task GenerateUpcoming_audits_each_generated_occurrence_and_cycle_with_the_run_id()
    {
        var (world, _, room) = Arrange();
        var weekly = world.Task("Badkamer", room).Id;
        SetSlots(world, world.Plans.Items[0], Slot(weekly, 1, 1));

        var run = (await world.Generation.GenerateUpcomingAsync(Nightly, "run-7", Ct)).AsT0;

        run.RunId.Should().Be("run-7");
        var created = Entries(world, AuditEntity.Occurrence, AuditAction.Create);
        created.Should().HaveCount(2);
        created.Should().OnlyContain(e => e.Meta!["runId"] == new AuditString("run-7") && e.Actor == Nightly);
        created[0].After["status"].Should().Be(new AuditString("open"));
        created[0].After["origin"].Should().Be(new AuditString("generated"));
        created[0].After["taskNameSnapshot"].Should().Be(new AuditString("Badkamer"));
        created[0].After["roomNameSnapshot"].Should().Be(new AuditString("Badkamer"));
        Entries(world, AuditEntity.Cycle, AuditAction.Create).Should().HaveCount(2);
    }

    // ---- snapshots

    [Fact]
    public async Task GenerateUpcoming_snapshots_name_duration_room_assignee_and_plan_and_plans_each_occurrence_on_its_own_day()
    {
        var (world, plan, room) = Arrange();
        var weekly = world.Task("Badkamer", room, "1w", 30).Id;
        var anna = world.Person("Persoon A").Id;
        SetSlots(world, plan, Slot(weekly, 2, 5, anna));

        await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);

        var occurrence = world.Occurrences.Items.Single(o => o.CycleId == world.Cycles.Items.Single(c => c.Index == 0).Id);
        occurrence.TaskNameSnapshot.Should().Be("Badkamer");
        occurrence.RoomIdSnapshot.Should().Be(room);
        occurrence.RoomNameSnapshot.Should().Be("Badkamer");
        occurrence.DurationMinutesSnapshot.Should().Be(30);
        occurrence.AssigneeId.Should().Be(anna);
        occurrence.PlanId.Should().Be(plan.Id);
        occurrence.Status.Should().Be(OccurrenceStatus.Open);
        occurrence.PlannedDate.Should().Be(occurrence.Date);
        DayKeys.ToDayKey(occurrence.Date, Amsterdam).Should().Be(new DateOnly(2026, 10, 2));
        occurrence.CycleId.Should().Be(world.Cycles.Items.Single(c => c.Index == 0).Id);
    }

    [Fact]
    public async Task A_later_task_change_never_rewrites_the_snapshots_of_existing_occurrences_and_the_next_generation_snapshots_the_new_values()
    {
        // interval-change.test.ts: a change of duration and name takes effect from the next generation only.
        var (world, plan, room) = Arrange();
        var task = world.Task("Badkamer schoonmaken", room, "1w", 30);
        SetSlots(world, plan, EveryWeek(task.Id, 1, "c00000000000000000000001"));
        await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);
        var before = world.Occurrences.Items.ToList();
        before.Should().HaveCount(8);

        world.Clock.Now = new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);
        world.Tasks.Items[0] = task with { DurationMinutes = 45, IntervalKey = "2wk", Name = "Badkamer grondig" };
        world.Occurrences.Items.Should().Equal(before, "changing the task writes only the task");

        var again = (await world.Generation.GenerateUpcomingAsync(Nightly, "run-2", Ct)).AsT0;

        again.Generated.Select(g => g.Inserted).Should().Equal(0, 0);
        world.Occurrences.Items.Should().Equal(before);
        world.Plans.Items[0].Slots.Should().HaveCount(4, "the template slots stay");

        world.Clock.Now = new DateTimeOffset(2026, 11, 9, 6, 0, 0, TimeSpan.Zero); // the start of cycle 2
        await world.Generation.GenerateUpcomingAsync(Nightly, "run-3", Ct);

        var cycle2 = world.Occurrences.Items.Where(o => DayKeys.ToDayKey(o.Date, Amsterdam) is { } day && day >= new DateOnly(2026, 11, 9) && day <= new DateOnly(2026, 12, 6)).ToList();
        cycle2.Should().HaveCount(4);
        cycle2.Should().OnlyContain(o => o.DurationMinutesSnapshot == 45 && o.TaskNameSnapshot == "Badkamer grondig");
        world.Occurrences.Items.Where(o => DayKeys.ToDayKey(o.Date, Amsterdam) < new DateOnly(2026, 11, 9)).Should()
            .OnlyContain(o => o.DurationMinutesSnapshot == 30 && o.TaskNameSnapshot == "Badkamer schoonmaken");
    }

    // ---- (b) vacation, inactive tasks, the past, DST, (e) intervals

    [Fact]
    public async Task GenerateUpcoming_leaves_vacation_days_empty()
    {
        var (world, plan, room) = Arrange();
        var daily = world.Task("Afwas", room, "daily", 15).Id;
        SetSlots(world, plan, [.. Enumerable.Range(0, 2).SelectMany(week => Enumerable.Range(0, 7).Select(day => Slot(daily, week, day)))]);
        world.Settings.Document = world.Settings.Document! with
        {
            VacationRanges = [new VacationRange(new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 20)), new VacationRange(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18))],
        };

        await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);

        var days = Days(world.Occurrences.Items);
        foreach (var vacation in new[] { new DateOnly(2026, 9, 17), new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 19), new DateOnly(2026, 9, 20), new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18) })
        {
            days.Should().NotContain(vacation);
        }

        days.Should().Contain([new DateOnly(2026, 9, 16), new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 19)]);
        days.Should().HaveCount(28 - 4 - 7);
    }

    [Fact]
    public async Task GenerateUpcoming_skips_inactive_tasks_and_days_before_today()
    {
        var (world, plan, room) = Arrange(new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero)); // Wednesday
        var weekly = world.Task("Badkamer", room).Id;
        var old = world.Task("Oud", room, active: false).Id;
        SetSlots(world, plan, Slot(weekly, 0, 1), Slot(weekly, 0, 3), Slot(old, 0, 4));

        await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);

        Days(world.Occurrences.Items.Where(o => o.Date < DayKeys.FromDayKey(new DateOnly(2026, 10, 12), Amsterdam))).Should().Equal(new DateOnly(2026, 9, 16));
    }

    [Fact]
    public async Task GenerateUpcoming_produces_correct_local_dates_across_the_end_of_daylight_saving_time()
    {
        var (world, plan, room) = Arrange();
        var weekly = world.Task("Badkamer", room).Id;
        SetSlots(world, plan, Slot(weekly, 1, 0), Slot(weekly, 2, 1)); // cycle 1: Sunday 25 Oct (DST ends), Monday 26 Oct

        await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);

        var cycle1 = world.Occurrences.Items.Where(o => o.Date >= new DateTimeOffset(2026, 10, 11, 22, 0, 0, TimeSpan.Zero)).OrderBy(o => o.Date).ToList();
        cycle1.Select(o => o.Date).Should().Equal(
            new DateTimeOffset(2026, 10, 24, 22, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 25, 23, 0, 0, TimeSpan.Zero));
        Days(cycle1).Should().Equal(new DateOnly(2026, 10, 25), new DateOnly(2026, 10, 26));
    }

    [Fact]
    public async Task GenerateUpcoming_yields_eight_occurrences_per_cycle_for_a_task_with_eight_slots()
    {
        var (world, plan, room) = Arrange();
        var twice = world.Task("Wastafel", room, "2w").Id;
        SetSlots(world, plan, [.. Enumerable.Range(0, 4).SelectMany(week => new[] { Slot(twice, week, 2), Slot(twice, week, 5) })]);

        var run = (await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct)).AsT0;

        run.Generated.Select(g => g.Inserted).Should().Equal(8, 8);
    }

    [Fact]
    public async Task Without_an_active_plan_only_the_cycles_are_ensured_and_nothing_is_generated()
    {
        var world = new CyclePlanWorld();
        world.Clock.Now = MondayMorning;
        world.Plan("Standaard", active: false, daysAgo: 30);

        var run = (await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct)).AsT0;

        run.Generated.Should().OnlyContain(g => g.PlanId == null && g.Inserted == 0 && g.Skipped == 0);
        world.Cycles.Items.Select(c => (c.Index, c.PlanId)).Should().Equal((0, (string?)null), (1, null));
        world.Occurrences.Items.Should().BeEmpty();
    }

    // ---- cycles

    [Fact]
    public async Task GenerateCycle_creates_the_cycle_with_the_active_plan_and_day_key_bounds_and_leaves_an_existing_one_alone()
    {
        var (world, plan, _) = Arrange();

        var result = (await world.Generation.GenerateCycleAsync(Nightly, 2, "run-1", Ct)).AsT0;

        var cycle = world.Cycles.Items.Should().ContainSingle().Subject;
        (cycle.Index, cycle.StartDate, cycle.EndDate, cycle.PlanId, cycle.GenerationRunId).Should()
            .Be((2, new DateOnly(2026, 11, 9), new DateOnly(2026, 12, 6), plan.Id, "run-1"));
        result.CycleId.Should().Be(cycle.Id);
        var writes = world.Cycles.Writes;

        await world.Generation.GenerateCycleAsync(Nightly, 2, "run-2", Ct);

        world.Cycles.Writes.Should().Be(writes);
    }

    [Fact]
    public async Task GenerateUpcoming_realigns_the_bounds_of_existing_cycles_when_the_anchor_moved_and_audits_it()
    {
        var (world, _, _) = Arrange();
        await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);
        world.Settings.Document = world.Settings.Document! with { CycleAnchorDate = new DateOnly(2026, 9, 21) };

        await world.Generation.GenerateUpcomingAsync(Nightly, "run-2", Ct);

        world.Cycles.Items.Select(c => (c.Index, c.StartDate, c.EndDate)).Should().Contain((0, new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 18)));
        var alignment = Entries(world, AuditEntity.Cycle, AuditAction.Update).Where(e => e.Meta!["reason"] == new AuditString("anchor_alignment")).ToList().Should().HaveCount(2).And.Subject.First();
        alignment.Meta!["runId"].Should().Be(new AuditString("run-2"));
    }

    // ---- repair after an anchor or slot change (nightly reconciliation)

    [Fact]
    public async Task GenerateUpcoming_repairs_future_occurrences_generated_before_the_anchor_changed()
    {
        var (world, plan, room) = Arrange(new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero));
        var water = world.Task("Waterbak", room, "1w", 3).Id;
        var anna = world.Person("Persoon A").Id;
        var bram = world.Person("Persoon B").Id;
        SetSlots(world, plan, EveryWeek(water, 3, anna));
        await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);
        world.Settings.Document = world.Settings.Document! with { CycleAnchorDate = new DateOnly(2026, 9, 21) };
        SetSlots(world, plan, [.. Enumerable.Range(0, 4).SelectMany(week => MonWedFri.Select(weekday => Slot(water, week, weekday, bram)))]);

        var run = (await world.Generation.GenerateUpcomingAsync(Nightly, "run-2", Ct)).AsT0;

        run.Removed.Should().Be(4, "the occurrences of 23 Sep, 30 Sep, 7 Oct and 14 Oct are replaced");
        Days(world.Occurrences.Items).Select(d => d.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Should().Equal(
            "09-21", "09-23", "09-25", "09-28", "09-30", "10-02", "10-05", "10-07", "10-09", "10-12", "10-14", "10-16", "10-21", "10-28", "11-04");
        world.Occurrences.Items.Where(o => DayKeys.ToDayKey(o.Date, Amsterdam) <= new DateOnly(2026, 10, 18)).Should().OnlyContain(o => o.AssigneeId == bram);
        world.Occurrences.Items.Where(o => DayKeys.ToDayKey(o.Date, Amsterdam) > new DateOnly(2026, 10, 18)).Should().OnlyContain(o => o.AssigneeId == anna);
        world.Cycles.Items.OrderBy(c => c.Index).Select(c => (c.Index, c.StartDate, c.EndDate)).Should().Equal(
            (-1, new DateOnly(2026, 8, 24), new DateOnly(2026, 9, 20)),
            (0, new DateOnly(2026, 9, 21), new DateOnly(2026, 10, 18)),
            (1, new DateOnly(2026, 10, 19), new DateOnly(2026, 11, 15)));
    }

    [Fact]
    public async Task GenerateUpcoming_repairs_stale_upcoming_occurrences_after_the_slots_changed_and_audits_the_removals()
    {
        var (world, plan, room) = Arrange();
        var weekly = world.Task("Badkamer", room, "1w", 30).Id;
        var anna = world.Person("Persoon A").Id;
        var bram = world.Person("Persoon B").Id;
        SetSlots(world, plan, EveryWeek(weekly, 1, anna));
        await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);
        SetSlots(world, plan, EveryWeek(weekly, 4, bram));

        var run = (await world.Generation.GenerateUpcomingAsync(Nightly, "run-2", Ct)).AsT0;

        run.Removed.Should().Be(8);
        Days(world.Occurrences.Items).Select(d => d.ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Should().Equal(
            "09-17", "09-24", "10-01", "10-08", "10-15", "10-22", "10-29", "11-05");
        world.Occurrences.Items.Should().OnlyContain(o => o.AssigneeId == bram);
        var removals = Entries(world, AuditEntity.Occurrence, AuditAction.Delete);
        removals.Should().HaveCount(8);
        removals.Should().OnlyContain(e => e.Meta!["reason"] == new AuditString("nightly_reconciliation") && e.Meta["runId"] == new AuditString("run-2"));
    }

    // ---- replacement

    [Fact]
    public async Task ReplaceUpcoming_keeps_done_skipped_dragged_past_and_ad_hoc_occurrences_and_replaces_the_rest()
    {
        var (world, plan, room) = Arrange();
        var weekly = world.Task("Badkamer", room, "1w", 30).Id;
        var twice = world.Task("Wastafel", room, "2w", 10).Id;
        var anna = world.Person("Persoon A").Id;
        var bram = world.Person("Persoon B").Id;
        SetSlots(world, plan, [.. EveryWeek(weekly, 1, anna), .. Enumerable.Range(0, 4).SelectMany(week => new[] { Slot(twice, week, 3), Slot(twice, week, 6) })]);
        await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);
        Occurrence On(string task, string day) => world.Occurrences.Items.Single(o => o.TaskId == task && DayKeys.ToDayKey(o.Date, Amsterdam) == DateOnly.Parse(day, System.Globalization.CultureInfo.InvariantCulture));
        void Change(Occurrence occurrence, Func<Occurrence, Occurrence> change) => world.Occurrences.Items[world.Occurrences.Items.FindIndex(o => o.Id == occurrence.Id)] = change(occurrence);

        var past = On(weekly, "2026-09-14");
        var done = On(weekly, "2026-09-21");
        var skipped = On(twice, "2026-09-23");
        var dragged = On(weekly, "2026-09-28");
        Change(done, o => o with { Status = OccurrenceStatus.Done, CompletedAt = world.Clock.Now, CompletedBy = anna });
        Change(skipped, o => o with { Status = OccurrenceStatus.Skipped });
        Change(dragged, o => o with { Date = DayKeys.FromDayKey(new DateOnly(2026, 9, 29), Amsterdam) });
        var adhoc = done with { Id = world.Occurrences.NextId(), Origin = OccurrenceOrigin.Adhoc, PlanId = null, Status = OccurrenceStatus.Open, CompletedAt = null, CompletedBy = null, Date = DayKeys.FromDayKey(new DateOnly(2026, 9, 30), Amsterdam), PlannedDate = DayKeys.FromDayKey(new DateOnly(2026, 9, 30), Amsterdam) };
        world.Occurrences.Items.Add(adhoc);

        world.Clock.Now = new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero); // two days later a new plan is activated
        var newPlan = world.Plan("Nieuw", false, 1, [.. Enumerable.Range(0, 4).Select(week => Slot(twice, week, 4, bram))]);
        world.Audit.Entries.Clear();

        var result = (await world.Generation.ReplaceUpcomingAsync(new AuditActor(CyclePlanWorld.Planner.ActorId, AuditSource.System), newPlan.Id, "run-9", ReplacementReasons.PlanActivation, Ct)).AsT0;

        result.Removed.Should().BeGreaterThan(0);
        foreach (var kept in new[] { past, done, skipped, dragged, adhoc })
        {
            world.Occurrences.Items.Should().Contain(o => o.Id == kept.Id, DayKeys.ToDayKey(kept.Date, Amsterdam).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        world.Occurrences.Items.Where(o => o.PlanId == newPlan.Id).Select(o => DayKeys.ToDayKey(o.Date, Amsterdam).ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Order()
            .Should().Equal("09-17", "09-24", "10-01", "10-08", "10-15", "10-22", "10-29", "11-05");
        world.Occurrences.Items.Where(o => o.PlanId == newPlan.Id).Should().OnlyContain(o => o.AssigneeId == bram);
        var removals = Entries(world, AuditEntity.Occurrence, AuditAction.Delete);
        removals.Should().HaveCount(result.Removed);
        removals.Should().OnlyContain(e => e.Actor.Source == AuditSource.System && e.Actor.ActorId == CyclePlanWorld.Planner.ActorId && e.Meta!["runId"] == new AuditString("run-9") && e.Meta["reason"] == new AuditString("plan_activation"));
        world.Cycles.Items.Where(c => c.Index is 0 or 1).Should().OnlyContain(c => c.PlanId == newPlan.Id);
        Entries(world, AuditEntity.Cycle, AuditAction.Update).Should().NotBeEmpty("the cycles are pointed at the new plan");
    }

    [Fact]
    public async Task ReplaceUpcoming_keeps_generated_work_beyond_the_next_cycle()
    {
        var (world, plan, room) = Arrange();
        var task = world.Task("Ramen", room).Id;
        SetSlots(world, plan, Slot(task, 0, 3));
        await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);
        await world.Generation.GenerateCycleAsync(Nightly, 2, "future-scope", Ct);
        var farFuture = world.Occurrences.Items.Single(o => DayKeys.ToDayKey(o.Date, Amsterdam) == new DateOnly(2026, 11, 11));
        var empty = world.Plan("Leeg", daysAgo: 1);

        await world.Generation.ReplaceUpcomingAsync(Nightly, empty.Id, "run-2", ReplacementReasons.PlanActivation, Ct);

        world.Occurrences.Items.Should().Contain(o => o.Id == farFuture.Id);
    }

    [Fact]
    public async Task ReplaceUpcoming_of_an_unknown_plan_is_not_found_and_writes_nothing()
    {
        var (world, _, _) = Arrange();

        var result = await world.Generation.ReplaceUpcomingAsync(Nightly, "0123456789abcdef01234567", "run-1", ReplacementReasons.PlanUpdate, Ct);

        result.IsT1.Should().BeTrue();
        world.Cycles.Writes.Should().Be(0);
        world.Audit.Entries.Should().BeEmpty();
    }

    // ---- failures

    [Fact]
    public async Task Generation_without_settings_is_settings_missing()
    {
        var (world, _, _) = Arrange();
        world.Settings.Document = null;

        (await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct)).IsT1.Should().BeTrue();
        (await world.Generation.GenerateCycleAsync(Nightly, 0, "run-1", Ct)).IsT1.Should().BeTrue();
        (await world.Generation.ReplaceUpcomingAsync(Nightly, world.Plans.Items[0].Id, "run-1", ReplacementReasons.PlanUpdate, Ct)).IsT2.Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_audit_entry_rolls_the_whole_run_back_cycles_and_occurrences_included()
    {
        var (world, plan, room) = Arrange();
        var weekly = world.Task("Badkamer", room).Id;
        SetSlots(world, plan, EveryWeek(weekly, 3));
        world.Audit.Failure = new PortError("audit down");

        var result = await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);

        result.AsT3.Message.Should().Be("audit down");
        world.Occurrences.Items.Should().BeEmpty();
        world.Cycles.Items.Should().BeEmpty();
        world.Transactions.Aborts.Should().Be(1);
    }

    [Fact]
    public async Task A_port_failure_while_inserting_is_passed_on_and_rolls_back()
    {
        var (world, plan, room) = Arrange();
        var weekly = world.Task("Badkamer", room).Id;
        SetSlots(world, plan, EveryWeek(weekly, 3));
        world.Occurrences.Failure = new PortError("occurrences down");

        var result = await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct);

        result.AsT3.Message.Should().Be("occurrences down");
        world.Cycles.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_conflict_of_the_transaction_runner_is_passed_on()
    {
        var (world, _, _) = Arrange();
        world.Transactions.ConflictInsteadOfRunning = new ConflictError("write_conflict", "busy");

        (await world.Generation.GenerateUpcomingAsync(Nightly, "run-1", Ct)).AsT2.Code.Should().Be("write_conflict");
    }
}
