using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using OneOf;

namespace Huishoudplanner.Application.Audit;

/// <summary>
/// Deletes audit entries older than the configured retention (<c>AUDIT_RETENTION_DAYS</c>). Port of
/// <c>domain/auditRetention.ts</c>; the Node server writes no audit entry for it either. The scheduling is the job
/// adapter's business (slice 6.3).
/// </summary>
public sealed class AuditRetentionService(
    ForReadingAuditRetention retention,
    ForDeletingAuditEntries eraser,
    TimeProvider time) : IAuditRetentionService
{
    public async Task<OneOf<RetentionDisabled, RetentionDone, PortError>> RunAsync(CancellationToken cancellationToken)
    {
        var read = await retention.GetAsync(cancellationToken).ConfigureAwait(false);
        if (read.TryPickT1(out var readError, out var policy))
        {
            return readError;
        }

        if (policy.Days is not { } days)
        {
            return new RetentionDisabled();
        }

        if (days < 1)
        {
            // Configuration validation guarantees at least one day; deleting everything on a bad value would be unrecoverable.
            return new PortError("audit_retention.invalid_days: the retention must be at least one day.");
        }

        var cutoff = time.GetUtcNow().AddDays(-days);
        var deleted = await eraser.DeleteOlderThanAsync(cutoff, cancellationToken).ConfigureAwait(false);
        return deleted.Match<OneOf<RetentionDisabled, RetentionDone, PortError>>(
            count => new RetentionDone(cutoff, count),
            error => error);
    }
}
