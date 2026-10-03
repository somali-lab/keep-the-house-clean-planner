using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Users;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Stores the people of the household. Reads and writes enlist in the running transaction when there is one
/// (<see cref="ForRunningTransactions"/>); the use case writes the audit entry in the same transaction. Documents of
/// installations from before roles or notification moments read with their defaults (<see cref="UserDefaults"/>).
/// </summary>
public interface ForStoringUsers
{
    /// <summary>One page, oldest first (<c>createdAt</c>, then id).</summary>
    Task<OneOf<UserPage, PortError>> ListAsync(UserQuery query, CancellationToken cancellationToken);

    /// <param name="userId">A 24-character hex id.</param>
    Task<OneOf<User, NotFound, PortError>> FindAsync(string userId, CancellationToken cancellationToken);

    Task<OneOf<long, PortError>> CountAsync(CancellationToken cancellationToken);

    /// <summary>Active administrators other than <paramref name="userId"/> (a document without a role counts as an administrator).</summary>
    Task<OneOf<long, PortError>> CountOtherActiveAdminsAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Stores a new, active user with notifications off; the store assigns the id and sets both timestamps to <paramref name="now"/>.</summary>
    Task<OneOf<User, PortError>> InsertAsync(NewUser user, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Sets the members of <paramref name="patch"/> that are not null, and <c>updatedAt</c>. Nothing else of the document changes.</summary>
    Task<OneOf<Success, NotFound, PortError>> UpdateAsync(string userId, UserPatch patch, DateTimeOffset now, CancellationToken cancellationToken);
}
