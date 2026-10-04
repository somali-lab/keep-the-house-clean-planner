using Huishoudplanner.Domain.Audit;

namespace Huishoudplanner.Domain.CyclePlans;

/// <summary>
/// How a plan appears in the audit log, as the Node server writes it (<c>data/cyclePlans.ts</c>): the stored fields without id and
/// timestamps; a slot save records only the added, removed and changed slots.
/// </summary>
public static class CyclePlanAudit
{
    public static AuditObject Fields(CyclePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return AuditObject.Of(
            ("name", plan.Name),
            ("active", plan.Active),
            ("slots", new AuditArray([.. plan.Slots.Select(s => (AuditValue)Slot(s))])),
            ("weekThemes", Strings(plan.WeekThemes)),
            ("draft", plan.Draft),
            ("source", plan.Source),
            ("proposalId", plan.ProposalId is { } proposal ? AuditValue.FromString(proposal) : AuditNull.Instance),
            ("rationale", plan.Rationale is { } rationale ? Strings(rationale) : AuditNull.Instance),
            ("discarded", plan.Discarded));
    }

    public static AuditObject Slot(CyclePlanSlot slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        return AuditObject.Of(
            ("taskId", new AuditObjectId(slot.TaskId)),
            ("weekIndex", slot.WeekIndex),
            ("weekday", slot.Weekday),
            ("assigneeId", slot.AssigneeId is { } assignee ? new AuditObjectId(assignee) : AuditNull.Instance),
            ("sortOrder", slot.SortOrder));
    }

    /// <summary>The creation entry; a copy names its source in <c>meta.copiedFrom</c>.</summary>
    public static AuditEntry ForCreate(AuditActor actor, CyclePlan plan, string? copiedFrom = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ChangeSet.Between(null, Fields(plan)).ToEntry(
            actor,
            AuditEntity.CyclePlan,
            plan.Id,
            AuditAction.Create,
            copiedFrom is null ? null : AuditObject.Of(("copiedFrom", new AuditObjectId(copiedFrom))));
    }

    public static AuditEntry ForDelete(AuditActor actor, CyclePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return ChangeSet.Between(Fields(plan), null).ToEntry(actor, AuditEntity.CyclePlan, plan.Id, AuditAction.Delete);
    }

    public static AuditEntry ForSlots(AuditActor actor, string planId, SlotDiff diff, AuditObject? meta = null)
    {
        ArgumentNullException.ThrowIfNull(diff);
        var before = new AuditArray([.. diff.Removed.Concat(diff.Changed.Select(c => c.Before)).Select(s => (AuditValue)Slot(s))]);
        var after = new AuditArray([.. diff.Added.Concat(diff.Changed.Select(c => c.After)).Select(s => (AuditValue)Slot(s))]);
        return new AuditEntry(
            actor, AuditEntity.CyclePlan, planId, AuditAction.Update, AuditObject.Of(("slots", before)), AuditObject.Of(("slots", after)), meta);
    }

    /// <summary>
    /// A deleted task left the plan (<c>removeTaskFromPlans</c>): the slot entry with <c>meta: { reason: 'task_delete', taskId }</c>
    /// (requirements 4.9).
    /// </summary>
    public static AuditEntry ForTaskDelete(AuditActor actor, string planId, string taskId, SlotDiff diff)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        return ForSlots(actor, planId, diff, AuditObject.Of(("reason", "task_delete"), ("taskId", new AuditObjectId(taskId))));
    }

    private static AuditArray Strings(IEnumerable<string> values) => new([.. values.Select(AuditValue.FromString)]);
}
