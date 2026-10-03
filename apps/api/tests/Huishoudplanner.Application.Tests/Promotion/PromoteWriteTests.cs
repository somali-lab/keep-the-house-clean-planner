using Huishoudplanner.Application.Promotion;
using Huishoudplanner.Application.Tests.CyclePlans;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Promotion;
using Huishoudplanner.Domain.Settings;

namespace Huishoudplanner.Application.Tests.Promotion;

/// <summary>
/// The apply and dismiss use cases against fakes (the <c>POST /promote-suggestions/apply</c> and <c>dismiss</c> routes of promote.ts): what is
/// changed, what is audited and that a refusal changes nothing. The HTTP behaviour and the stored documents are covered by <c>PromoteEndpointTests</c>.
/// </summary>
public sealed class PromoteWriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class World
    {
        public CyclePlanWorld Plans { get; } = new();

        public string TaskId { get; }

        public string P1 { get; }

        public string P2 { get; }

        public CyclePlan Plan { get; }

        public World(bool active = true)
        {
            var room = Plans.Room("Badkamer");
            TaskId = Plans.Task("Badkamer schoonmaken", room.Id, "4wk").Id;
            P1 = Plans.Person("Persoon 1").Id;
            P2 = Plans.Person("Persoon 2").Id;
            Plan = Plans.Plan("Standaard", active, 1, new CyclePlanSlot(TaskId, 1, 2, P1));
        }

        public PromoteService Service => new(
            Plans.Settings, Plans.Plans, Plans.Cycles, Plans.Tasks, new FakePromotionEvidence(), Plans.Service, Plans.Transactions, Plans.Audit, Plans.Clock);

        public ApplyPromotionCommand Apply(int toWeekday = 3, string? toAssignee = null, int weekIndex = 1, int weekday = 2) =>
            new(Plan.Id, TaskId, weekIndex, weekday, toWeekday, toAssignee);

        public DismissedPromotion Dismissal(int toWeekday = 3, string evidence = "a00000000000000000000002") =>
            new(Plan.Id, TaskId, 1, 2, toWeekday, null, evidence);
    }

    // ---- apply

    [Fact]
    public async Task Apply_moves_the_slot_keeps_the_person_and_audits_the_slot_change_with_promotedFrom()
    {
        var w = new World();

        var saved = (await w.Service.ApplyAsync(CyclePlanWorld.Planner, w.Apply(), Ct)).AsT0;

        saved.Plan.Slots.Should().ContainSingle().Which.Should().Be(new CyclePlanSlot(w.TaskId, 1, 3, w.P1));
        saved.Synchronized.Should().NotBeNull("the slot of the active plan changed, so the upcoming occurrences are synchronised");
        var entry = w.Plans.Audit.Entries.Single(e => e.Entity == AuditEntity.CyclePlan);
        entry.Action.Should().Be(AuditAction.Update);
        entry.Actor.Source.Should().Be(AuditSource.Ui);
        entry.Meta!.Properties.Select(p => p.Key).Should().Equal("promotedFrom", "toWeekday");
        entry.Meta.Properties.Single(p => p.Key == "promotedFrom").Value.Should().Be(
            AuditObject.Of(("weekIndex", 1), ("weekday", 2), ("assigneeId", new AuditObjectId(w.P1))));
    }

    [Fact]
    public async Task Apply_can_hand_the_slot_to_another_person_and_says_so_in_the_meta()
    {
        var w = new World();

        var saved = (await w.Service.ApplyAsync(CyclePlanWorld.Planner, w.Apply(4, w.P2), Ct)).AsT0;

        saved.Plan.Slots.Single().Should().Be(new CyclePlanSlot(w.TaskId, 1, 4, w.P2));
        w.Plans.Audit.Entries.Single(e => e.Entity == AuditEntity.CyclePlan).Meta!.Properties
            .Select(p => p.Key).Should().Equal("promotedFrom", "toWeekday", "toAssigneeId");
    }

    [Fact]
    public async Task Apply_is_refused_with_the_validation_when_the_new_weekday_is_unavailable_and_changes_nothing()
    {
        var w = new World();
        w.Plans.People.Users[0] = w.Plans.People.Users[0] with { UnavailableWeekdays = [3] };

        var result = await w.Service.ApplyAsync(CyclePlanWorld.Planner, w.Apply(), Ct);

        result.AsT3.Validation.Errors.Should().ContainSingle().Which.Code.Should().Be("assignee_unavailable");
        w.Plans.Plans.Items.Single().Slots.Should().Equal(new CyclePlanSlot(w.TaskId, 1, 2, w.P1));
        w.Plans.Audit.Entries.Should().BeEmpty();
        w.Plans.Plans.Writes.Should().Be(0);
    }

    [Fact]
    public async Task Apply_only_changes_the_active_plan()
    {
        var w = new World(active: false);
        var other = w.Plans.Plan("Actief", active: true, daysAgo: 0);

        var notActive = await w.Service.ApplyAsync(CyclePlanWorld.Planner, w.Apply(), Ct);
        var unknown = await w.Service.ApplyAsync(CyclePlanWorld.Planner, w.Apply() with { PlanId = "0123456789abcdef01234567" }, Ct);

        notActive.AsT4.Code.Should().Be("plan_not_active");
        unknown.AsT4.Code.Should().Be("plan_not_active");
        other.Active.Should().BeTrue();
        w.Plans.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_without_any_active_plan_is_plan_not_active()
    {
        var w = new World(active: false);

        (await w.Service.ApplyAsync(CyclePlanWorld.Planner, w.Apply(), Ct)).AsT4.Code.Should().Be("plan_not_active");
    }

    [Fact]
    public async Task Apply_for_a_slot_that_is_gone_is_slot_not_found()
    {
        var w = new World();

        (await w.Service.ApplyAsync(CyclePlanWorld.Planner, w.Apply(weekIndex: 2), Ct)).IsT1.Should().BeTrue();
        w.Plans.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Apply_rejects_bad_values_field_by_field()
    {
        var w = new World();

        var errors = (await w.Service.ApplyAsync(
            CyclePlanWorld.Planner, new ApplyPromotionCommand("x", "y", 4, 7, -1, "z"), Ct)).AsT2.Errors;

        errors.Keys.Should().BeEquivalentTo("planId", "taskId", "weekIndex", "weekday", "toWeekday", "toAssigneeId");
    }

    [Fact]
    public async Task Apply_returns_a_port_failure_and_changes_nothing()
    {
        var w = new World();
        w.Plans.Plans.Failure = new PortError("boom");

        (await w.Service.ApplyAsync(CyclePlanWorld.Planner, w.Apply(), Ct)).AsT5.Message.Should().Be("boom");
    }

    // ---- dismiss

    [Fact]
    public async Task Dismiss_appends_the_dismissal_and_audits_a_settings_update()
    {
        var w = new World();

        (await w.Service.DismissAsync(CyclePlanWorld.Planner, w.Dismissal(), Ct)).IsT0.Should().BeTrue();

        w.Plans.Settings.Document!.DismissedPromotions.Should().Equal(w.Dismissal());
        var entry = w.Plans.Audit.Entries.Should().ContainSingle().Subject;
        (entry.Entity, entry.Action, entry.EntityId).Should().Be((AuditEntity.Settings, AuditAction.Update, SettingsIds.Singleton));
        entry.After.Properties.Select(p => p.Key).Should().Equal("dismissedPromotions");
    }

    [Fact]
    public async Task Dismiss_replaces_an_earlier_dismissal_of_the_same_target_and_keeps_other_targets()
    {
        var w = new World();
        await w.Service.DismissAsync(CyclePlanWorld.Planner, w.Dismissal(3, "a00000000000000000000001"), Ct);
        await w.Service.DismissAsync(CyclePlanWorld.Planner, w.Dismissal(4, "a00000000000000000000001"), Ct);

        await w.Service.DismissAsync(CyclePlanWorld.Planner, w.Dismissal(3, "a00000000000000000000009"), Ct);

        w.Plans.Settings.Document!.DismissedPromotions.Select(d => (d.ToWeekday, d.LastEvidenceId)).Should()
            .Equal((4, "a00000000000000000000001"), (3, "a00000000000000000000009"));
    }

    [Fact]
    public async Task Dismissing_what_is_already_stored_writes_and_audits_nothing()
    {
        var w = new World();
        await w.Service.DismissAsync(CyclePlanWorld.Planner, w.Dismissal(), Ct);
        var writes = w.Plans.Settings.Writes;

        (await w.Service.DismissAsync(CyclePlanWorld.Planner, w.Dismissal(), Ct)).IsT0.Should().BeTrue();

        w.Plans.Settings.Writes.Should().Be(writes);
        w.Plans.Audit.Entries.Should().HaveCount(1);
    }

    [Fact]
    public async Task Dismiss_rejects_bad_values_and_reports_missing_settings_and_failures()
    {
        var w = new World();
        var bad = new DismissedPromotion("x", "y", 9, 9, 9, "z", "q");
        (await w.Service.DismissAsync(CyclePlanWorld.Planner, bad, Ct)).AsT1.Errors.Keys.Should()
            .BeEquivalentTo("planId", "taskId", "weekIndex", "weekday", "toWeekday", "toAssigneeId", "lastEvidenceId");

        w.Plans.Settings.Failure = new PortError("boom");
        (await w.Service.DismissAsync(CyclePlanWorld.Planner, w.Dismissal(), Ct)).AsT3.Message.Should().Be("boom");

        w.Plans.Settings.Failure = null;
        w.Plans.Settings.Document = null;
        (await w.Service.DismissAsync(CyclePlanWorld.Planner, w.Dismissal(), Ct)).IsT4.Should().BeTrue();
        w.Plans.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Dismiss_records_the_dismissal_with_lower_case_ids()
    {
        var w = new World();

        await w.Service.DismissAsync(CyclePlanWorld.Planner, w.Dismissal(3, "A00000000000000000000002"), Ct);

        w.Plans.Settings.Document!.DismissedPromotions.Single().LastEvidenceId.Should().Be("a00000000000000000000002");
    }
}
