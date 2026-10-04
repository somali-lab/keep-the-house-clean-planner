using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driving;
using OneOf;

namespace Huishoudplanner.Application.Tests.Badges;

/// <summary>An <see cref="IBadgeAwardService"/> that records how the points and statistics use cases call it, for the tests of those callers.</summary>
internal sealed class RecordingBadgeAwards : IBadgeAwardService
{
    public sealed record LiveCall(AuditActor Actor, BadgeEvaluationScope Scope, PointsSyncReason Reason);

    public sealed record ReconcileCall(AuditActor Actor, BadgeEvalTrigger Trigger, bool Safely, IReadOnlyDictionary<string, string>? Names);

    public List<LiveCall> Live { get; } = [];

    public List<ReconcileCall> Reconciles { get; } = [];

    /// <summary>What <see cref="ReconcileAsync"/> answers; a failure makes the badge step of a points reconciliation fail.</summary>
    public OneOf<BadgeEvaluationResult, ConflictError, PortError> ReconcileResult { get; set; } = BadgeEvaluationResult.None;

    public Task EvaluateAfterExecutionAsync(AuditActor actor, BadgeEvaluationScope scope, PointsSyncReason reason, CancellationToken cancellationToken)
    {
        Live.Add(new LiveCall(actor, scope, reason));
        return Task.CompletedTask;
    }

    public Task<OneOf<BadgeEvaluationResult, ConflictError, PortError>> ReconcileAsync(
        AuditActor actor, BadgeEvalTrigger trigger, IReadOnlyDictionary<string, string>? badgeNames, CancellationToken cancellationToken)
    {
        Reconciles.Add(new ReconcileCall(actor, trigger, false, badgeNames));
        return Task.FromResult(ReconcileResult);
    }

    public Task ReconcileSafelyAsync(AuditActor actor, BadgeEvalTrigger trigger, IReadOnlyDictionary<string, string>? badgeNames, CancellationToken cancellationToken)
    {
        Reconciles.Add(new ReconcileCall(actor, trigger, true, badgeNames));
        return Task.CompletedTask;
    }
}
