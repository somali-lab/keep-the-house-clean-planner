using System.Globalization;
using Huishoudplanner.Domain.Audit;

namespace Huishoudplanner.Domain.Generation;

/// <summary>How a cycle appears in the audit log, as the Node server writes it (<c>data/cycles.ts</c>): day keys are strings, the run id is in <c>meta</c>.</summary>
public static class CycleAudit
{
    public static AuditEntry ForCreate(AuditActor actor, Cycle cycle)
    {
        ArgumentNullException.ThrowIfNull(cycle);
        var fields = AuditObject.Of(
            ("index", cycle.Index),
            ("startDate", Day(cycle.StartDate)),
            ("endDate", Day(cycle.EndDate)),
            ("planId", Id(cycle.PlanId)),
            ("generatedAt", cycle.GeneratedAt),
            ("generationRunId", cycle.GenerationRunId));
        return ChangeSet.Between(null, fields).ToEntry(actor, AuditEntity.Cycle, cycle.Id, AuditAction.Create, AuditObject.Of(("runId", cycle.GenerationRunId)));
    }

    /// <summary>The configured anchor moved: the stored bounds of an existing cycle are realigned.</summary>
    public static AuditEntry ForAnchorAlignment(AuditActor actor, Cycle before, Cycle after, string runId)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return new AuditEntry(
            actor,
            AuditEntity.Cycle,
            before.Id,
            AuditAction.Update,
            AuditObject.Of(("startDate", Day(before.StartDate)), ("endDate", Day(before.EndDate))),
            AuditObject.Of(("startDate", Day(after.StartDate)), ("endDate", Day(after.EndDate))),
            AuditObject.Of(("runId", runId), ("reason", "anchor_alignment")));
    }

    /// <summary>The cycle is now generated from another plan.</summary>
    public static AuditEntry ForPlan(AuditActor actor, string cycleId, string? beforePlanId, string afterPlanId, string runId) =>
        new(
            actor,
            AuditEntity.Cycle,
            cycleId,
            AuditAction.Update,
            AuditObject.Of(("planId", Id(beforePlanId))),
            AuditObject.Of(("planId", Id(afterPlanId))),
            AuditObject.Of(("runId", runId)));

    private static AuditValue Day(DateOnly day) => AuditValue.FromString(day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static AuditValue Id(string? id) => id is null ? AuditNull.Instance : new AuditObjectId(id);
}
