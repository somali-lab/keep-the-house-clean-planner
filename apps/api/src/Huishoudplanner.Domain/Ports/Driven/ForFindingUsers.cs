using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>Looks a user up by id, only as far as identity needs. Slice 1.1 extends or supersedes it with the users resource.</summary>
public interface ForFindingUsers
{
    /// <param name="userId">A 24-character hex id.</param>
    Task<OneOf<UserIdentity, NotFound, PortError>> FindAsync(string userId, CancellationToken cancellationToken);
}
