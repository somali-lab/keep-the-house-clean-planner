using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.CyclePlans;

namespace Huishoudplanner.Domain.Activation;

/// <summary>
/// How an activation appears in the audit log, as the Node server writes it (<c>setActivePlan</c> in <c>data/cyclePlans.ts</c>): the plan gets an
/// <c>activate</c> entry (a cleared draft flag is part of it), every plan it deactivated an <c>update</c> entry that names the activated plan.
/// Both carry the run id of the activation in <c>meta</c>.
/// </summary>
public static class ActivationAudit
{
    public static AuditEntry ForActivated(AuditActor actor, CyclePlan before, CyclePlan after, string runId)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var draftChanged = before.Draft != after.Draft;
        var beforeFields = new List<KeyValuePair<string, AuditValue>> { new("active", before.Active) };
        var afterFields = new List<KeyValuePair<string, AuditValue>> { new("active", true) };
        if (draftChanged)
        {
            beforeFields.Add(new("draft", before.Draft));
            afterFields.Add(new("draft", after.Draft));
        }

        return new AuditEntry(
            actor,
            AuditEntity.CyclePlan,
            after.Id,
            AuditAction.Activate,
            new AuditObject(beforeFields),
            new AuditObject(afterFields),
            AuditObject.Of(("runId", runId)));
    }

    public static AuditEntry ForDeactivated(AuditActor actor, string planId, string activatedPlanId, string runId) =>
        new(
            actor,
            AuditEntity.CyclePlan,
            planId,
            AuditAction.Update,
            AuditObject.Of(("active", true)),
            AuditObject.Of(("active", false)),
            AuditObject.Of(("runId", runId), ("activatedPlanId", new AuditObjectId(activatedPlanId))));
}
