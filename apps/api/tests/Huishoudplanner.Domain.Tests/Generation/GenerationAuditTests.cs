using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Occurrences;

namespace Huishoudplanner.Domain.Tests.Generation;

/// <summary>
/// How cycles and occurrences appear in the audit log, as the Node server writes them (<c>data/cycles.ts</c>, <c>data/occurrences.ts</c>):
/// the stored fields without id and timestamps, nulls kept, the run id in <c>meta</c>.
/// </summary>
public sealed class GenerationAuditTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 14, 6, 0, 0, TimeSpan.Zero);

    private static readonly AuditActor Planner = new("0123456789abcdef01234567", AuditSource.Ui);

    private const string Id = "d00000000000000000000001";

    private static Occurrence Generated(string? assignee = "c00000000000000000000001") => new(
        Id,
        "a00000000000000000000001",
        "f00000000000000000000001",
        "e00000000000000000000001",
        new DateTimeOffset(2026, 9, 15, 22, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 15, 22, 0, 0, TimeSpan.Zero),
        assignee,
        OccurrenceStatus.Open,
        null,
        null,
        null,
        null,
        30,
        "Badkamer",
        "b00000000000000000000001",
        "Keuken",
        OccurrenceOrigin.Generated,
        At,
        At);

    [Fact]
    public void A_generated_occurrence_is_audited_with_every_stored_field_and_the_run_in_meta()
    {
        var entry = OccurrenceAudit.ForGenerated(Planner, Generated(), "run-1", 0);

        entry.Entity.Should().Be(AuditEntity.Occurrence);
        entry.Action.Should().Be(AuditAction.Create);
        entry.EntityId.Should().Be(Id);
        entry.Before.Count.Should().Be(0);
        entry.After["status"].Should().Be(new AuditString("open"));
        entry.After["origin"].Should().Be(new AuditString("generated"));
        entry.After["taskNameSnapshot"].Should().Be(new AuditString("Badkamer"));
        entry.After["roomNameSnapshot"].Should().Be(new AuditString("Keuken"));
        entry.After["durationMinutesSnapshot"].Should().Be(new AuditInteger(30));
        entry.After["assigneeId"].Should().Be(new AuditObjectId("c00000000000000000000001"));
        entry.After["completedAt"].Should().Be(AuditNull.Instance);
        entry.After["statusBeforeCompletion"].Should().Be(AuditNull.Instance);
        entry.After["date"].Should().Be(new AuditInstant(new DateTimeOffset(2026, 9, 15, 22, 0, 0, TimeSpan.Zero)));
        entry.After.Keys.Should().NotContain(["id", "createdAt", "updatedAt", "requestId", "pointsSnapshot"]);
        entry.Meta!["runId"].Should().Be(new AuditString("run-1"));
        entry.Meta["cycleIndex"].Should().Be(new AuditInteger(0));
    }

    [Fact]
    public void An_unassigned_generated_occurrence_audits_an_explicit_null_assignee()
    {
        OccurrenceAudit.ForGenerated(Planner, Generated(null), "run-1", 1).After["assigneeId"].Should().Be(AuditNull.Instance);
    }

    [Fact]
    public void A_removed_occurrence_is_audited_as_a_delete_with_its_fields_before_and_the_reason_in_meta()
    {
        var entry = OccurrenceAudit.ForRemoved(Planner, Generated(), "run-2", "e00000000000000000000009", "plan_update");

        entry.Action.Should().Be(AuditAction.Delete);
        entry.After.Count.Should().Be(0);
        entry.Before["taskNameSnapshot"].Should().Be(new AuditString("Badkamer"));
        entry.Meta!["runId"].Should().Be(new AuditString("run-2"));
        entry.Meta["planId"].Should().Be(new AuditObjectId("e00000000000000000000009"));
        entry.Meta["reason"].Should().Be(new AuditString("plan_update"));
    }

    [Fact]
    public void An_optional_stored_field_is_audited_only_when_it_has_a_value()
    {
        var done = Generated() with { PointsSnapshot = 30, RequestId = "request-key-123456" };

        var fields = OccurrenceAudit.Fields(done);

        fields["pointsSnapshot"].Should().Be(new AuditInteger(30));
        fields["requestId"].Should().Be(new AuditString("request-key-123456"));
    }

    [Fact]
    public void A_new_cycle_is_audited_with_its_day_keys_plan_and_run()
    {
        var cycle = new Cycle(Id, 1, new DateOnly(2026, 10, 12), new DateOnly(2026, 11, 8), "e00000000000000000000001", At, "run-1");

        var entry = CycleAudit.ForCreate(Planner, cycle);

        entry.Entity.Should().Be(AuditEntity.Cycle);
        entry.Action.Should().Be(AuditAction.Create);
        entry.After["index"].Should().Be(new AuditInteger(1));
        entry.After["startDate"].Should().Be(new AuditString("2026-10-12"));
        entry.After["endDate"].Should().Be(new AuditString("2026-11-08"));
        entry.After["planId"].Should().Be(new AuditObjectId("e00000000000000000000001"));
        entry.After["generationRunId"].Should().Be(new AuditString("run-1"));
        entry.After["generatedAt"].Should().Be(new AuditInstant(At));
        entry.After.Keys.Should().NotContain("id");
        entry.Meta!["runId"].Should().Be(new AuditString("run-1"));
    }

    [Fact]
    public void A_cycle_without_a_plan_audits_an_explicit_null()
    {
        var cycle = new Cycle(Id, 0, new DateOnly(2026, 9, 14), new DateOnly(2026, 10, 11), null, At, "run-1");

        CycleAudit.ForCreate(Planner, cycle).After["planId"].Should().Be(AuditNull.Instance);
    }

    [Fact]
    public void An_anchor_alignment_audits_the_old_and_new_bounds_with_its_reason()
    {
        var before = new Cycle(Id, 0, new DateOnly(2026, 9, 14), new DateOnly(2026, 10, 11), null, At, "run-1");
        var after = before with { StartDate = new DateOnly(2026, 9, 21), EndDate = new DateOnly(2026, 10, 18) };

        var entry = CycleAudit.ForAnchorAlignment(Planner, before, after, "run-2");

        entry.Action.Should().Be(AuditAction.Update);
        entry.Before["startDate"].Should().Be(new AuditString("2026-09-14"));
        entry.After["startDate"].Should().Be(new AuditString("2026-09-21"));
        entry.After["endDate"].Should().Be(new AuditString("2026-10-18"));
        entry.Meta!["reason"].Should().Be(new AuditString("anchor_alignment"));
        entry.Meta["runId"].Should().Be(new AuditString("run-2"));
    }

    [Fact]
    public void Pointing_a_cycle_at_a_plan_audits_the_old_and_new_plan()
    {
        var entry = CycleAudit.ForPlan(Planner, Id, null, "e00000000000000000000002", "run-3");

        entry.Before["planId"].Should().Be(AuditNull.Instance);
        entry.After["planId"].Should().Be(new AuditObjectId("e00000000000000000000002"));
        entry.Meta!["runId"].Should().Be(new AuditString("run-3"));
    }

    [Fact]
    public void A_cycle_cursor_round_trips_also_for_a_negative_index_and_refuses_anything_else()
    {
        foreach (var index in new[] { -3, 0, 1, 40 })
        {
            CycleCursor.TryDecode(new CycleCursor(index).Encode(), out var back).Should().BeTrue();
            back.Index.Should().Be(index);
        }

        CycleCursor.TryDecode("garbage", out _).Should().BeFalse();
        CycleCursor.TryDecode(null, out _).Should().BeFalse();
        CycleCursor.TryDecode(string.Empty, out _).Should().BeFalse();
        CycleCursor.TryDecode(Convert.ToBase64String("[\"x\"]"u8.ToArray()), out _).Should().BeFalse();
    }
}
