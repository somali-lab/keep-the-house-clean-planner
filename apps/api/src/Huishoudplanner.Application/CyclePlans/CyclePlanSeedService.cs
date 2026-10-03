using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using OneOf;

namespace Huishoudplanner.Application.CyclePlans;

/// <summary><c>ensureDefaultPlan</c> of <c>apps/server/src/domain/plans.ts</c>: the empty active plan "Standaard" on a first start, audited as the system actor.</summary>
public sealed class CyclePlanSeedService(ForStoringCyclePlans plans, ForRecordingAudit audit, ForRunningTransactions transactions, TimeProvider time) : ICyclePlanSeedService
{
    public async Task<OneOf<bool, ConflictError, PortError>> SeedAsync(CancellationToken cancellationToken)
    {
        var ran = await transactions.RunAsync(SeedInTransactionAsync, cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<bool, ConflictError, PortError>>(
            outcome => outcome.Match<OneOf<bool, ConflictError, PortError>>(created => created, error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<bool, PortError>>> SeedInTransactionAsync(CancellationToken cancellationToken)
    {
        static TransactionOutcome<OneOf<bool, PortError>> Abort(PortError error) => TransactionOutcome.Abort<OneOf<bool, PortError>>(error);

        var count = await plans.CountAsync(cancellationToken).ConfigureAwait(false);
        if (count.TryPickT1(out var countError, out var existing))
        {
            return Abort(countError);
        }

        if (existing > 0)
        {
            return TransactionOutcome.Commit<OneOf<bool, PortError>>(false);
        }

        var inserted = await plans.InsertAsync(
            new NewCyclePlan(CyclePlanRules.DefaultPlanName, true, [], CyclePlanRules.EmptyWeekThemes, time.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);
        if (inserted.TryPickT1(out var insertError, out var created))
        {
            return Abort(insertError);
        }

        var recorded = await audit.RecordAsync(CyclePlanAudit.ForCreate(AuditActor.System, created), cancellationToken).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<bool, PortError>>(true),
            error => Abort(error));
    }
}
