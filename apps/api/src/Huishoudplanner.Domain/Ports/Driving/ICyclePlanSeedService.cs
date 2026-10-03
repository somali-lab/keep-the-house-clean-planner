using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>First-run plan (<c>ensureDefaultPlan</c> in apps/server/src/domain/plans.ts).</summary>
public interface ICyclePlanSeedService
{
    /// <summary>
    /// When there are no plans, creates the empty active plan "Standaard" with a <c>create</c> audit entry of the system actor, in one
    /// transaction. <see langword="true"/> when it was created, <see langword="false"/> when plans already existed.
    /// </summary>
    Task<OneOf<bool, ConflictError, PortError>> SeedAsync(CancellationToken cancellationToken);
}
