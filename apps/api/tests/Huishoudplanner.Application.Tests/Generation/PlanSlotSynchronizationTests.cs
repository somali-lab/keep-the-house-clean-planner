using Huishoudplanner.Application.Tests.CyclePlans;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;

namespace Huishoudplanner.Application.Tests.Generation;

/// <summary>
/// Saving the slots of the ACTIVE plan always synchronises the upcoming occurrences, in the same request and the same transaction
/// (requirements 4.3; <c>generation.test.ts</c> "editing the active plan"). The replaced and created occurrences are audited with the
/// system as source and the saving profile as actor; a draft plan changes nothing.
/// </summary>
public sealed class PlanSlotSynchronizationTests
{
    private static readonly TimeZoneInfo Amsterdam = DayKeys.FindZone("Europe/Amsterdam");

    private static readonly DateTimeOffset MondayMorning = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CyclePlanSlot Slot(string task, int week, int weekday, string? assignee = null) => new(task, week, weekday, assignee);

    private static CyclePlanSlot[] EveryWeek(string task, int weekday, string? assignee) =>
        [.. Enumerable.Range(0, 4).Select(week => Slot(task, week, weekday, assignee))];

    private static (CyclePlanWorld World, CyclePlan Active, string Weekly, string Anna, string Bram) Arrange()
    {
        var world = new CyclePlanWorld();
        world.Clock.Now = MondayMorning;
        var room = world.Room("Badkamer");
        var weekly = world.Task("Badkamer", room.Id, "1w", 30).Id;
        var anna = world.Person("Persoon 1").Id;
        var bram = world.Person("Persoon 2").Id;
        var active = world.Plan("Standaard", active: true, daysAgo: 30);
        return (world, active, weekly, anna, bram);
    }

    private static List<string> Days(CyclePlanWorld world) =>
        [.. world.Occurrences.Items.Select(o => DayKeys.ToDayKey(o.Date, Amsterdam).ToString("MM-dd", System.Globalization.CultureInfo.InvariantCulture)).Order()];

    [Fact]
    public async Task Saving_the_slots_of_the_active_plan_generates_the_upcoming_occurrences_and_reports_it_in_synchronized()
    {
        var (world, active, weekly, anna, _) = Arrange();

        var saved = (await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, active.Id, EveryWeek(weekly, 1, anna), Ct)).AsT0;

        saved.Synchronized.Should().NotBeNull();
        saved.Synchronized!.Removed.Should().Be(0);
        saved.Synchronized.Generated.Select(g => (g.CycleIndex, g.Inserted, g.Skipped, g.PlanId)).Should().Equal((0, 4, 0, active.Id), (1, 4, 0, active.Id));
        world.Occurrences.Items.Should().HaveCount(8);
        world.Cycles.Items.Select(c => c.PlanId).Should().OnlyContain(id => id == active.Id);
    }

    [Fact]
    public async Task Saving_other_slots_replaces_the_open_generated_occurrences_and_audits_the_replacement_as_the_system_for_the_saving_profile()
    {
        var (world, active, weekly, anna, bram) = Arrange();
        await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, active.Id, EveryWeek(weekly, 1, anna), Ct);
        world.Audit.Entries.Clear();

        var saved = (await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, active.Id, EveryWeek(weekly, 4, bram), Ct)).AsT0;

        saved.Synchronized!.Removed.Should().Be(8);
        Days(world).Should().Equal("09-17", "09-24", "10-01", "10-08", "10-15", "10-22", "10-29", "11-05");
        world.Occurrences.Items.Should().OnlyContain(o => o.AssigneeId == bram);
        var removals = world.Audit.Entries.Where(e => e.Entity == AuditEntity.Occurrence && e.Action == AuditAction.Delete).ToList();
        removals.Should().HaveCount(8);
        removals.Should().OnlyContain(e => e.Meta!["reason"] == new AuditString("plan_update") && e.Actor.Source == AuditSource.System && e.Actor.ActorId == CyclePlanWorld.Planner.ActorId);
        world.Audit.Entries.Where(e => e.Entity == AuditEntity.Occurrence && e.Action == AuditAction.Create).Should().HaveCount(8);
        world.Audit.Entries.Where(e => e.Entity == AuditEntity.CyclePlan).Should().ContainSingle("the slot diff of the plan itself, attributed to the profile").Which.Actor.Source.Should().Be(AuditSource.Ui);
    }

    [Fact]
    public async Task A_newly_assigned_task_appears_on_its_date_for_its_person()
    {
        var (world, active, weekly, anna, _) = Arrange();

        await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, active.Id, [Slot(weekly, 0, 4, anna)], Ct);

        var occurrence = world.Occurrences.Items.Single(o => o.CycleId == world.Cycles.Items.Single(c => c.Index == 0).Id);
        DayKeys.ToDayKey(occurrence.Date, Amsterdam).Should().Be(new DateOnly(2026, 9, 17));
        (occurrence.AssigneeId, occurrence.Status).Should().Be((anna, OccurrenceStatus.Open));
    }

    [Fact]
    public async Task Saving_the_slots_of_a_draft_plan_does_not_synchronise_anything()
    {
        var (world, _, weekly, anna, _) = Arrange();
        var draft = world.Plan("Concept", daysAgo: 1);

        var saved = (await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, draft.Id, [Slot(weekly, 0, 4, anna)], Ct)).AsT0;

        saved.Synchronized.Should().BeNull();
        world.Occurrences.Items.Should().BeEmpty();
        world.Cycles.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failure_while_synchronising_rolls_the_slot_save_back()
    {
        var (world, active, weekly, anna, _) = Arrange();
        world.Occurrences.Failure = new PortError("occurrences down");

        var result = await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, active.Id, [Slot(weekly, 0, 4, anna)], Ct);

        result.AsT5.Message.Should().Be("occurrences down");
        world.Plans.Items.Single(p => p.Id == active.Id).Slots.Should().BeEmpty();
        world.Audit.Entries.Should().BeEmpty();
        world.Cycles.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Synchronising_without_settings_is_settings_missing_and_saves_nothing()
    {
        var (world, active, weekly, anna, _) = Arrange();
        world.Settings.Document = null;

        var result = await world.Service.ReplaceSlotsAsync(CyclePlanWorld.Planner, active.Id, [Slot(weekly, 0, 4, anna)], Ct);

        result.IsT6.Should().BeTrue();
        world.Plans.Items.Single(p => p.Id == active.Id).Slots.Should().BeEmpty();
        world.Audit.Entries.Should().BeEmpty();
    }
}
