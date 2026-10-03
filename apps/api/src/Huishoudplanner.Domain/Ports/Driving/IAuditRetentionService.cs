using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>The audit retention job (requirements 4.9, 4.10). Scheduling it is the job adapter's business.</summary>
public interface IAuditRetentionService
{
    /// <summary>
    /// Deletes the entries older than the configured number of days. Does nothing when retention is not configured
    /// (<see cref="RetentionDisabled"/>). Running it again deletes nothing more. Writes no audit entry.
    /// </summary>
    Task<OneOf<RetentionDisabled, RetentionDone, PortError>> RunAsync(CancellationToken cancellationToken);
}
