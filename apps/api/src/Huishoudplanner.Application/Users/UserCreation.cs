using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Users;
using OneOf;

namespace Huishoudplanner.Application.Users;

/// <summary>Creating a user and its audit entry: the one step shared by the create use case and the seed. Runs inside the caller's transaction.</summary>
internal static class UserCreation
{
    public static async Task<OneOf<User, PortError>> StoreAsync(
        ForStoringUsers users,
        ForRecordingAudit audit,
        AuditActor actor,
        NewUser values,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var inserted = await users.InsertAsync(values, now, cancellationToken).ConfigureAwait(false);
        if (inserted.IsT1)
        {
            return inserted.AsT1;
        }

        var user = inserted.AsT0;
        var entry = ChangeSet.Between(null, UserRules.ToAudit(user)).ToEntry(actor, AuditEntity.User, user.Id, AuditAction.Create);
        var recorded = await audit.RecordAsync(entry, cancellationToken).ConfigureAwait(false);
        if (recorded.IsT1)
        {
            return recorded.AsT1;
        }

        return user;
    }
}
