using Huishoudplanner.Domain.Activation;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Settings;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Domain.Tests.Activation;

/// <summary>The pure projection of an activation (domain/activationPreview.ts): what is removed, added and preserved, the token, and the audit entries.</summary>
public sealed class ActivationPreviewerTests
{
    private const string Plan = "d00000000000000000000001";
    private const string Other = "d00000000000000000000002";
    private const string Task = "a00000000000000000000001";
    private const string Room = "b00000000000000000000001";
    private const string Cycle0 = "e00000000000000000000001";
    private const string Actor = "c00000000000000000000001";

    private static readonly DateTimeOffset Now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Zone = DayKeys.FindZone("Europe/Amsterdam");
    private static readonly HouseholdSettings Settings = SettingsDefaults.ForNewInstallation("Europe/Amsterdam", new DateOnly(2026, 9, 14), Now);
    private static readonly HouseholdTask TheTask = new(Task, "Ramen", Room, "1w", 10, 10, null, true, string.Empty, [], null, Now, Now);
    private static readonly Dictionary<string, HouseholdTask> Tasks = new() { [Task] = TheTask };
    private static readonly Dictionary<string, string> Rooms = new() { [Room] = "Badkamer" };
    private static readonly Cycle Current = new(Cycle0, 0, new DateOnly(2026, 9, 14), new DateOnly(2026, 10, 11), Other, Now, "run");

    private static CyclePlan PlanWith(DateTimeOffset updatedAt, params CyclePlanSlot[] slots) =>
        new(Plan, "Nieuw", false, slots, ["", "", "", ""], false, PlanSources.Manual, null, null, false, Now, updatedAt);

    private static Occurrence Generated(string id, DateOnly day, OccurrenceStatus status = OccurrenceStatus.Open, DateOnly? movedTo = null)
    {
        var planned = DayKeys.FromDayKey(day, Zone);
        return new Occurrence(
            id, Task, Cycle0, Other, movedTo is { } to ? DayKeys.FromDayKey(to, Zone) : planned, planned, null, status, null, null, null, null,
            10, "Ramen", Room, "Badkamer", OccurrenceOrigin.Generated, Now, Now);
    }

    private static ActivationPreview Build(CyclePlan plan, params Occurrence[] existing) =>
        ActivationPreviewer.Build(plan, Other, Settings, Zone, ActivationWindow.Of(Settings, Zone, Now), Tasks, Rooms, Current, null, existing);

    [Fact]
    public void An_open_generated_occurrence_in_the_window_is_removed_and_the_ones_that_happened_are_preserved()
    {
        var open = Generated("f00000000000000000000001", new DateOnly(2026, 9, 17));
        var done = Generated("f00000000000000000000002", new DateOnly(2026, 9, 18), OccurrenceStatus.Done);
        var skipped = Generated("f00000000000000000000003", new DateOnly(2026, 9, 19), OccurrenceStatus.Skipped);
        var moved = Generated("f00000000000000000000004", new DateOnly(2026, 9, 20), movedTo: new DateOnly(2026, 9, 21));
        var past = Generated("f00000000000000000000005", new DateOnly(2026, 9, 14));

        var preview = Build(PlanWith(Now), open, done, skipped, moved, past);

        preview.AsOfDate.Should().Be(new DateOnly(2026, 9, 16));
        preview.Removed.Select(i => i.OccurrenceId).Should().Equal(open.Id);
        preview.Preserved.Done.Select(i => i.OccurrenceId).Should().Equal(done.Id);
        preview.Preserved.Skipped.Select(i => i.OccurrenceId).Should().Equal(skipped.Id);
        preview.Preserved.Moved.Select(i => i.OccurrenceId).Should().Equal(moved.Id);
        preview.Preserved.Adhoc.Should().BeEmpty();
        preview.PreviewToken.Should().MatchRegex("^[a-f0-9]{64}$");
    }

    [Fact]
    public void The_plan_adds_what_is_not_blocked_by_a_surviving_occurrence_on_the_same_slot()
    {
        var slot = new CyclePlanSlot(Task, 0, 4, null); // Thursday 17 Sep and 15 Oct
        var blocked = Generated("f00000000000000000000001", new DateOnly(2026, 9, 17), OccurrenceStatus.Skipped);

        var preview = Build(PlanWith(Now, slot), blocked);

        preview.Added.Select(i => i.Date).Should().Equal(new DateOnly(2026, 10, 15));
        preview.Added[0].Should().Be(new ActivationPreviewItem(null, 1, Task, "Ramen", new DateOnly(2026, 10, 15), null));
    }

    [Fact]
    public void The_token_is_stable_for_the_same_state_and_changes_when_the_plan_or_an_occurrence_changes()
    {
        var slot = new CyclePlanSlot(Task, 0, 4, null);
        var open = Generated("f00000000000000000000001", new DateOnly(2026, 9, 17));

        var same = Build(PlanWith(Now, slot), open).PreviewToken;

        same.Should().Be(Build(PlanWith(Now, slot), open).PreviewToken);
        Build(PlanWith(Now.AddMinutes(1), slot), open).PreviewToken.Should().NotBe(same, "the plan was saved");
        Build(PlanWith(Now, slot), open with { UpdatedAt = Now.AddMinutes(1) }).PreviewToken.Should().NotBe(same, "an occurrence changed");
        Build(PlanWith(Now, slot)).PreviewToken.Should().NotBe(same, "an occurrence is gone");
    }

    [Fact]
    public void Activation_audit_names_a_cleared_draft_and_a_deactivation_names_the_activated_plan()
    {
        var by = new AuditActor(Actor, AuditSource.Ui);
        var draft = PlanWith(Now) with { Draft = true, Source = PlanSources.Ai };

        var activated = ActivationAudit.ForActivated(by, draft, draft with { Active = true, Draft = false }, "run-1");
        var plain = ActivationAudit.ForActivated(by, PlanWith(Now), PlanWith(Now) with { Active = true }, "run-1");
        var deactivated = ActivationAudit.ForDeactivated(by, Other, Plan, "run-1");

        activated.Action.Should().Be(AuditAction.Activate);
        activated.Before.Should().Be(AuditObject.Of(("active", false), ("draft", true)));
        activated.After.Should().Be(AuditObject.Of(("active", true), ("draft", false)));
        activated.Meta.Should().Be(AuditObject.Of(("runId", "run-1")));
        plain.Before.Should().Be(AuditObject.Of(("active", false)));
        plain.After.Should().Be(AuditObject.Of(("active", true)));
        deactivated.Action.Should().Be(AuditAction.Update);
        deactivated.Meta.Should().Be(AuditObject.Of(("runId", "run-1"), ("activatedPlanId", new AuditObjectId(Plan))));
    }
}
