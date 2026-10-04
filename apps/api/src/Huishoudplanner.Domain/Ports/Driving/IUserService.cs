using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Users;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>The users resource (requirements sections 2 and 3). Every state change is audited in the same transaction.</summary>
public interface IUserService
{
    /// <param name="active">Only active (<c>true</c>) or only inactive (<c>false</c>) users; <see langword="null"/> for all.</param>
    /// <param name="cursor">The <see cref="UserPage.NextCursor"/> of the previous page.</param>
    /// <param name="limit">1 to <see cref="UserLimits.MaxPageSize"/>, default <see cref="UserLimits.DefaultPageSize"/>.</param>
    Task<OneOf<UserPage, ValidationErrors, PortError>> ListAsync(bool? active, string? cursor, int? limit, CancellationToken cancellationToken);

    /// <summary>One person; <see cref="NotFound"/> for an unknown id, a <see cref="ValidationErrors"/> on <c>id</c> for a malformed one.</summary>
    Task<OneOf<User, NotFound, ValidationErrors, PortError>> GetAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Administrators only (the endpoint's policy). Audit: <c>user</c> / <c>create</c>.</summary>
    Task<OneOf<User, ValidationErrors, ConflictError, PortError>> CreateAsync(Actor actor, CreateUserInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Administrators only. <c>last_admin</c> when the last active administrator would be deactivated or demoted. A patch that
    /// changes nothing writes and audits nothing and returns the user as stored. <paramref name="expectedVersion"/> is the version the caller read
    /// (<c>If-Match</c>, ADR-0022): another version is a <see cref="PreconditionFailed"/>, also for a patch that would change nothing; <see langword="null"/>
    /// skips the check.
    /// </summary>
    Task<OneOf<User, NotFound, ValidationErrors, ConflictError, PortError, PreconditionFailed>> UpdateAsync(
        Actor actor, string userId, UpdateUserInput input, CancellationToken cancellationToken, int? expectedVersion = null);

    /// <summary>
    /// A person sets their own moments; an administrator may set anyone's, anyone else is <see cref="Forbidden"/>. The complete
    /// setting replaces the stored one; a setting equal to the stored one writes and audits nothing. The setting is a field of the person's document, so it is conditional on the person's version
    /// like any other change (<paramref name="expectedVersion"/>, <see cref="PreconditionFailed"/>).
    /// </summary>
    Task<OneOf<User, NotFound, ValidationErrors, Forbidden, ConflictError, PortError, PreconditionFailed>> SetBrowserNotificationsAsync(
        Actor actor, string userId, BrowserNotificationsInput input, CancellationToken cancellationToken, int? expectedVersion = null);
}
