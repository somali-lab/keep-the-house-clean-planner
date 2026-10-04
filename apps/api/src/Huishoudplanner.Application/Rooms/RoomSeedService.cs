using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Rooms;
using OneOf;

namespace Huishoudplanner.Application.Rooms;

/// <summary>
/// The rooms part of the Node <c>seed</c> (<c>SEED_ROOMS</c>): an empty rooms collection gets the default rooms, audited as the
/// system actor. All or nothing, in one transaction.
/// </summary>
public sealed class RoomSeedService(
    ForStoringRooms rooms,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : IRoomSeedService
{
    private const int SortOrderStep = 10;

    private static readonly (string Name, bool Virtual)[] DefaultRooms =
    [
        ("Keuken", false),
        ("Badkamer", false),
        ("Toilet", false),
        ("Woonkamer", false),
        ("Slaapkamer", false),
        ("Hal", false),
        ("Hele huis", true),
    ];

    public async Task<OneOf<int, ConflictError, PortError>> SeedAsync(CancellationToken cancellationToken)
    {
        var result = await transactions.RunAsync<OneOf<int, PortError>>(
            async token =>
            {
                var count = await rooms.CountAsync(token).ConfigureAwait(false);
                if (count.IsT1)
                {
                    return TransactionOutcome.Abort<OneOf<int, PortError>>(count.AsT1);
                }

                if (count.AsT0 > 0)
                {
                    return TransactionOutcome.Commit<OneOf<int, PortError>>(0);
                }

                var now = time.GetUtcNow();
                for (var index = 0; index < DefaultRooms.Length; index++)
                {
                    var (name, isVirtual) = DefaultRooms[index];
                    var inserted = await rooms.InsertAsync(new NewRoom(name, (index + 1) * SortOrderStep, true, isVirtual, now), token).ConfigureAwait(false);
                    if (inserted.TryPickT1(out var insertError, out var room))
                    {
                        return TransactionOutcome.Abort<OneOf<int, PortError>>(insertError);
                    }

                    var entry = ChangeSet.Between(null, RoomAuditFields.Of(room)).ToEntry(AuditActor.System, AuditEntity.Room, room.Id, AuditAction.Create);
                    var recorded = await audit.RecordAsync(entry, token).ConfigureAwait(false);
                    if (recorded.TryPickT1(out var auditError, out _))
                    {
                        return TransactionOutcome.Abort<OneOf<int, PortError>>(auditError);
                    }
                }

                return TransactionOutcome.Commit<OneOf<int, PortError>>(DefaultRooms.Length);
            },
            cancellationToken).ConfigureAwait(false);

        return result.Match<OneOf<int, ConflictError, PortError>>(
            inner => inner.Match<OneOf<int, ConflictError, PortError>>(created => created, error => error),
            conflict => conflict,
            error => error);
    }
}
