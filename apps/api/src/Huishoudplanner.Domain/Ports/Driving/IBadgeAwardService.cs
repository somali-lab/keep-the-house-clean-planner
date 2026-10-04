using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The evaluation of the derived badge awards (ADR-0014; <c>evaluateBadgeAwards</c> of the Node server). The points use cases call it: a live
/// evaluation after every execution sync, and a reconciliation as the last step of the points reconciliation and after a statistics reset.
/// A failed badge evaluation must never undo executions, bonuses or a reset.
/// </summary>
public interface IBadgeAwardService
{
    /// <summary>
    /// The live evaluation after an execution sync, for the people and the task of <paramref name="scope"/>: every real change of an award is its
    /// own <c>badgeAward</c> entry with <c>meta: { reason, badgeName }</c>, and a run that changes nothing writes and audits nothing. It joins the
    /// transaction of the caller (the occurrence use case), so it must run inside one. A failure is logged and never reported: the check-off that
    /// caused it must not fail, and the next reconciliation repairs the awards.
    /// </summary>
    Task EvaluateAfterExecutionAsync(AuditActor actor, BadgeEvaluationScope scope, PointsSyncReason reason, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the awards of everybody match the data, in one transaction, with one <c>badgeAward</c> summary entry when anything changed.
    /// <paramref name="badgeNames"/> names badges that no longer exist (a deleted one), so the history of the awards that are withdrawn with it
    /// keeps the name.
    /// </summary>
    Task<OneOf<BadgeEvaluationResult, ConflictError, PortError>> ReconcileAsync(
        AuditActor actor,
        BadgeEvalTrigger trigger,
        IReadOnlyDictionary<string, string>? badgeNames,
        CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="ReconcileAsync"/> for a caller whose own write is already committed (a badge change, a task deletion, a statistics reset;
    /// <c>reconcileBadgesSafely</c> of the Node server): a failure is logged and never fails that request.
    /// </summary>
    Task ReconcileSafelyAsync(AuditActor actor, BadgeEvalTrigger trigger, IReadOnlyDictionary<string, string>? badgeNames, CancellationToken cancellationToken);
}
