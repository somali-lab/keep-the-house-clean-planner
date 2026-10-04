using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;
using OneOf;

namespace Huishoudplanner.Application.Badges;

/// <summary>
/// The evaluation of the derived badge awards (ADR-0014): port of <c>domain/badgeAwards.ts</c> and <c>reconcileBadges</c> of the Node server. The
/// rules are pure (<see cref="BadgeAwardPlanner"/>); this class reads the badges, the stored awards and the data they count, asks the planner for the
/// differences and applies them with their audit entries.
/// </summary>
/// <remarks>
/// <para>The Node server serialised every evaluation in the points queue because it had no transactions. Here a live evaluation joins the transaction
/// of the execution sync that calls it (so a check-off, its ledger entry and its awards commit together), and a reconciliation is one transaction of
/// its own, started after the writes it follows have committed: a failing evaluation never undoes executions, bonuses or a reset. Two evaluations that
/// overlap conflict on the awards they both write and the transaction runner runs the loser again; the unique key makes a double award impossible.</para>
/// <para>A live evaluation audits every real change of an award; a reconciliation audits one summary and none per award; a run that changes
/// nothing writes and audits nothing.</para>
/// </remarks>
public sealed partial class BadgeAwardService(
    ForStoringBadges badges,
    ForStoringBadgeAwards awards,
    ForReadingBadgeEvidence evidence,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time,
    ILogger<BadgeAwardService> logger) : IBadgeAwardService
{
    /// <summary>What an evaluation decided and applied.</summary>
    private sealed record Applied(BadgeSelection Selection, IReadOnlyList<AppliedAward> Changes);

    public async Task EvaluateAfterExecutionAsync(AuditActor actor, BadgeEvaluationScope scope, PointsSyncReason reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(scope);
        // No catch around the work: a transient transaction error must reach the runner of the caller so that it runs the attempt again, and the stores
        // turn every other infrastructure failure into a value. Such a value is logged and swallowed here: the check-off must not fail.
        var applied = await EvaluateAsync(scope, null, cancellationToken).ConfigureAwait(false);
        if (!applied.TryPickT0(out var done, out var failure))
        {
            LogLiveFailed(logger, PointNames.ToWire(reason), failure.Message);
            return;
        }

        foreach (var change in done.Changes)
        {
            var name = done.Selection.Names.GetValueOrDefault(change.Award.BadgeId) ?? string.Empty;
            var recorded = await audit.RecordAsync(BadgeAudit.ForAward(actor, change, name, reason), cancellationToken).ConfigureAwait(false);
            if (recorded.TryPickT1(out var recordFailure, out _))
            {
                LogLiveFailed(logger, PointNames.ToWire(reason), recordFailure.Message);
                return;
            }
        }
    }

    public async Task<OneOf<BadgeEvaluationResult, ConflictError, PortError>> ReconcileAsync(
        AuditActor actor,
        BadgeEvalTrigger trigger,
        IReadOnlyDictionary<string, string>? badgeNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var ran = await transactions.RunAsync(ct => ReconcileInTransactionAsync(actor, trigger, badgeNames, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<BadgeEvaluationResult, ConflictError, PortError>>(
            outcome => outcome.Match<OneOf<BadgeEvaluationResult, ConflictError, PortError>>(result => result, error => error),
            conflict => conflict,
            error => error);
    }

    public async Task ReconcileSafelyAsync(AuditActor actor, BadgeEvalTrigger trigger, IReadOnlyDictionary<string, string>? badgeNames, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ReconcileAsync(actor, trigger, badgeNames, cancellationToken).ConfigureAwait(false);
            if (!result.TryPickT0(out _, out var failure))
            {
                LogReconcileFailed(logger, BadgeTriggers.ToWire(trigger), failure.Match(conflict => conflict.Code, error => error.Message));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogReconcileThrew(logger, e, BadgeTriggers.ToWire(trigger));
        }
    }

    private async Task<TransactionOutcome<OneOf<BadgeEvaluationResult, PortError>>> ReconcileInTransactionAsync(
        AuditActor actor, BadgeEvalTrigger trigger, IReadOnlyDictionary<string, string>? badgeNames, CancellationToken ct)
    {
        var applied = await EvaluateAsync(BadgeEvaluationScope.Everybody, badgeNames, ct).ConfigureAwait(false);
        if (applied.TryPickT1(out var failure, out var done))
        {
            return TransactionOutcome.Abort<OneOf<BadgeEvaluationResult, PortError>>(failure);
        }

        var result = new BadgeEvaluationResult(
            done.Changes.Count(c => c.Change == AwardChange.Created),
            done.Changes.Count(c => c.Change == AwardChange.Updated),
            done.Changes.Count(c => c.Change == AwardChange.Removed));
        if (done.Changes.Count > 0)
        {
            var recorded = await audit.RecordAsync(BadgeAudit.ForSummary(actor, trigger, result, done.Changes, done.Selection.Names), ct).ConfigureAwait(false);
            if (recorded.TryPickT1(out var recordFailure, out _))
            {
                return TransactionOutcome.Abort<OneOf<BadgeEvaluationResult, PortError>>(recordFailure);
            }
        }

        return TransactionOutcome.Commit<OneOf<BadgeEvaluationResult, PortError>>(result);
    }

    /// <summary>Reads, plans and applies; it joins the transaction it is called in and audits nothing itself.</summary>
    private async Task<OneOf<Applied, PortError>> EvaluateAsync(BadgeEvaluationScope scope, IReadOnlyDictionary<string, string>? badgeNames, CancellationToken ct)
    {
        var every = await badges.ListAllAsync(null, ct).ConfigureAwait(false);
        if (every.TryPickT1(out var badgesFailure, out var allBadges))
        {
            return badgesFailure;
        }

        var selection = BadgeAwardPlanner.Select(allBadges, scope, badgeNames);
        var storedRead = await awards.FindForEvaluationAsync(scope.PersonIds, ct).ConfigureAwait(false);
        if (storedRead.TryPickT1(out var storedFailure, out var stored))
        {
            return storedFailure;
        }

        IReadOnlyList<CreditedExecution> executions = [];
        if (selection.NeedsExecutions)
        {
            var read = await evidence.FindCreditedExecutionsAsync(scope.PersonIds, ct).ConfigureAwait(false);
            if (read.TryPickT1(out var failure, out executions!))
            {
                return failure;
            }
        }

        IReadOnlyList<OnTimeWeek> onTime = [];
        if (selection.NeedsOnTimeWeeks)
        {
            var read = await evidence.FindOnTimeWeeksAsync(scope.PersonIds, ct).ConfigureAwait(false);
            if (read.TryPickT1(out var failure, out onTime!))
            {
                return failure;
            }
        }

        var plan = BadgeAwardPlanner.Plan(selection, scope, stored, executions, onTime);
        if (plan.IsEmpty)
        {
            return new Applied(selection, []);
        }

        var written = await awards.ApplyAsync(plan, time.GetUtcNow(), ct).ConfigureAwait(false);
        return written.Match<OneOf<Applied, PortError>>(changes => new Applied(selection, changes), error => error);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Badge evaluation after an execution ({Reason}) failed: {Detail}")]
    private static partial void LogLiveFailed(ILogger logger, string reason, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Badge reconciliation ({Trigger}) failed: {Detail}")]
    private static partial void LogReconcileFailed(ILogger logger, string trigger, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Badge reconciliation ({Trigger}) failed")]
    private static partial void LogReconcileThrew(ILogger logger, Exception exception, string trigger);
}
