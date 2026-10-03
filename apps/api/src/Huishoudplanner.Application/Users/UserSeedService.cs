using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Users;
using OneOf;

namespace Huishoudplanner.Application.Users;

/// <summary>
/// The users part of the Node <c>seed</c>: an empty users collection gets the configured profiles, the first as
/// administrator and the others as members, audited as the system actor. All or nothing, in one transaction.
/// </summary>
public sealed class UserSeedService(
    ForStoringUsers users,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : IUserSeedService
{
    public async Task<OneOf<int, ConflictError, PortError>> SeedAsync(IReadOnlyList<SeedProfile> profiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var result = await transactions.RunAsync<OneOf<int, PortError>>(
            async token =>
            {
                var count = await users.CountAsync(token).ConfigureAwait(false);
                if (count.IsT1)
                {
                    return TransactionOutcome.Abort<OneOf<int, PortError>>(count.AsT1);
                }

                if (count.AsT0 > 0)
                {
                    return TransactionOutcome.Commit<OneOf<int, PortError>>(0);
                }

                var now = time.GetUtcNow();
                for (var index = 0; index < profiles.Count; index++)
                {
                    var values = new NewUser(
                        profiles[index].Name,
                        profiles[index].Color,
                        index == 0 ? Role.Admin : Role.Member,
                        [],
                        UserDefaults.NewUserMinutes,
                        UserDefaults.NewUserMinutes);
                    var stored = await UserCreation.StoreAsync(users, audit, AuditActor.System, values, now, token).ConfigureAwait(false);
                    if (stored.IsT1)
                    {
                        return TransactionOutcome.Abort<OneOf<int, PortError>>(stored.AsT1);
                    }
                }

                return TransactionOutcome.Commit<OneOf<int, PortError>>(profiles.Count);
            },
            cancellationToken).ConfigureAwait(false);

        return result.Match<OneOf<int, ConflictError, PortError>>(
            inner => inner.Match<OneOf<int, ConflictError, PortError>>(created => created, error => error),
            conflict => conflict,
            error => error);
    }
}
