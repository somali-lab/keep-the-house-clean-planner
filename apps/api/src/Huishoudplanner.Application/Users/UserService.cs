using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Users;
using OneOf;
using Changed = OneOf.OneOf<Huishoudplanner.Domain.Users.User, Huishoudplanner.Domain.Errors.NotFound, Huishoudplanner.Domain.Errors.ConflictError, Huishoudplanner.Domain.Errors.PortError>;

namespace Huishoudplanner.Application.Users;

/// <summary>
/// The users use cases. Parsing and the rules live in <see cref="UserRules"/>; this class orders them: validate, check the
/// permission, then read, decide and write (entity and audit entry) inside one transaction. A change that does not alter
/// the stored user commits without writing anything.
/// </summary>
public sealed class UserService(
    ForStoringUsers users,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : IUserService
{
    public async Task<OneOf<UserPage, ValidationErrors, PortError>> ListAsync(
        bool? active, string? cursor, int? limit, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? UserLimits.DefaultPageSize;
        if (pageSize is < 1 or > UserLimits.MaxPageSize)
        {
            return ValidationErrors.For("limit", $"must be between 1 and {UserLimits.MaxPageSize}");
        }

        UserCursor? after = null;
        if (cursor is not null)
        {
            if (!UserCursor.TryParse(cursor, out var parsed))
            {
                return ValidationErrors.For("cursor", "invalid_cursor");
            }

            after = parsed;
        }

        var page = await users.ListAsync(new UserQuery(active, after, pageSize), cancellationToken).ConfigureAwait(false);
        return page.Match<OneOf<UserPage, ValidationErrors, PortError>>(p => p, e => e);
    }

    public async Task<OneOf<User, ValidationErrors, ConflictError, PortError>> CreateAsync(
        Actor actor, CreateUserInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var parsed = UserRules.ParseCreate(input);
        if (parsed.IsT1)
        {
            return parsed.AsT1;
        }

        var values = parsed.AsT0;
        var auditActor = AuditActor.From(actor);
        var result = await transactions.RunAsync<OneOf<User, PortError>>(
            async token =>
            {
                var stored = await UserCreation.StoreAsync(users, audit, auditActor, values, time.GetUtcNow(), token).ConfigureAwait(false);
                return stored.IsT0 ? TransactionOutcome.Commit<OneOf<User, PortError>>(stored.AsT0) : TransactionOutcome.Abort<OneOf<User, PortError>>(stored.AsT1);
            },
            cancellationToken).ConfigureAwait(false);

        return result.Match<OneOf<User, ValidationErrors, ConflictError, PortError>>(
            inner => inner.Match<OneOf<User, ValidationErrors, ConflictError, PortError>>(u => u, e => e),
            conflict => conflict,
            error => error);
    }

    public async Task<OneOf<User, NotFound, ValidationErrors, ConflictError, PortError>> UpdateAsync(
        Actor actor, string userId, UpdateUserInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!UserRules.IsObjectId(userId))
        {
            return ValidationErrors.For("id", UserRules.InvalidObjectId);
        }

        var parsed = UserRules.ParsePatch(input);
        if (parsed.IsT1)
        {
            return parsed.AsT1;
        }

        var changed = await ChangeAsync(actor, userId.ToLowerInvariant(), parsed.AsT0, cancellationToken).ConfigureAwait(false);
        return changed.Match<OneOf<User, NotFound, ValidationErrors, ConflictError, PortError>>(u => u, nf => nf, c => c, e => e);
    }

    public async Task<OneOf<User, NotFound, ValidationErrors, Forbidden, ConflictError, PortError>> SetBrowserNotificationsAsync(
        Actor actor, string userId, BrowserNotificationsInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!UserRules.IsObjectId(userId))
        {
            return ValidationErrors.For("id", UserRules.InvalidObjectId);
        }

        var parsed = UserRules.ParseBrowserNotifications(input);
        if (parsed.IsT1)
        {
            return parsed.AsT1;
        }

        var id = userId.ToLowerInvariant();
        if (!string.Equals(actor.ActorId, id, StringComparison.OrdinalIgnoreCase) && actor.Role != Role.Admin)
        {
            return new Forbidden("Only an administrator can change another person's notifications");
        }

        var changed = await ChangeAsync(actor, id, new UserPatch(BrowserNotifications: parsed.AsT0), cancellationToken).ConfigureAwait(false);
        return changed.Match<OneOf<User, NotFound, ValidationErrors, Forbidden, ConflictError, PortError>>(u => u, nf => nf, c => c, e => e);
    }

    private async Task<Changed> ChangeAsync(Actor actor, string id, UserPatch patch, CancellationToken cancellationToken)
    {
        var auditActor = AuditActor.From(actor);
        var result = await transactions.RunAsync<Changed>(
            async token =>
            {
                var found = await users.FindAsync(id, token).ConfigureAwait(false);
                if (found.IsT1)
                {
                    return TransactionOutcome.Abort<Changed>(found.AsT1);
                }

                if (found.IsT2)
                {
                    return TransactionOutcome.Abort<Changed>(found.AsT2);
                }

                var before = found.AsT0;
                if (UserRules.RemovesActiveAdmin(before, patch))
                {
                    var others = await users.CountOtherActiveAdminsAsync(id, token).ConfigureAwait(false);
                    if (others.IsT1)
                    {
                        return TransactionOutcome.Abort<Changed>(others.AsT1);
                    }

                    if (others.AsT0 == 0)
                    {
                        return TransactionOutcome.Abort<Changed>(new ConflictError(UserRules.LastAdminCode, UserRules.LastAdminDetail));
                    }
                }

                var now = time.GetUtcNow();
                var after = UserRules.Apply(before, patch, now);
                var change = ChangeSet.Between(UserRules.ToAudit(before), UserRules.ToAudit(after));
                if (change.IsNoOp)
                {
                    return TransactionOutcome.Commit<Changed>(before);
                }

                var written = await users.UpdateAsync(id, patch, now, token).ConfigureAwait(false);
                if (written.IsT1)
                {
                    return TransactionOutcome.Abort<Changed>(written.AsT1);
                }

                if (written.IsT2)
                {
                    return TransactionOutcome.Abort<Changed>(written.AsT2);
                }

                var recorded = await audit.RecordAsync(
                    change.ToEntry(auditActor, AuditEntity.User, id, AuditAction.Update), token).ConfigureAwait(false);
                return recorded.IsT1
                    ? TransactionOutcome.Abort<Changed>(recorded.AsT1)
                    : TransactionOutcome.Commit<Changed>(after);
            },
            cancellationToken).ConfigureAwait(false);

        return result.Match<Changed>(inner => inner, conflict => conflict, error => error);
    }
}
