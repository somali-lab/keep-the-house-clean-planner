using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Users;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>First-run people: the configured profiles on an empty users collection (<c>SEED_USERS</c>).</summary>
public interface IUserSeedService
{
    /// <summary>
    /// When there are no users, creates one per profile (the first an administrator, the others members, each with the
    /// budget 60/120) with an audit entry of the system actor, all in one transaction. Otherwise does nothing.
    /// Returns the number of users created.
    /// </summary>
    Task<OneOf<int, ConflictError, PortError>> SeedAsync(IReadOnlyList<SeedProfile> profiles, CancellationToken cancellationToken);
}
