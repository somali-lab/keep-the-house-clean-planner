using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Concurrency;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Rooms;
using OneOf;

namespace Huishoudplanner.Application.Rooms;

/// <summary>
/// The room use cases (requirements 4.1). Every state change runs in one transaction together with its audit entry;
/// a change that changes nothing writes and audits nothing (ADR-0004). Port of <c>routes/rooms.ts</c> and <c>data/rooms.ts</c>.
/// </summary>
public sealed class RoomService(
    ForStoringRooms rooms,
    ForStoringTasks tasks,
    ForRunningTransactions transactions,
    ForRecordingAudit audit,
    TimeProvider time) : IRoomService
{
    /// <summary>The distance between the sort orders of rooms that were created without one.</summary>
    private const int SortOrderStep = 10;

    public async Task<OneOf<RoomList, ValidationErrors, PortError>> ListAsync(bool? active, int? limit, string? cursor, CancellationToken cancellationToken)
    {
        var take = limit ?? RoomListQuery.DefaultLimit;
        if (take is < 1 or > RoomListQuery.MaxLimit)
        {
            return ValidationErrors.For("limit", "must be a whole number from 1 to " + RoomListQuery.MaxLimit);
        }

        RoomCursor? after = null;
        if (cursor is not null)
        {
            if (!RoomCursor.TryDecode(cursor, out var decoded))
            {
                return ValidationErrors.For("cursor", "invalid_cursor");
            }

            after = decoded;
        }

        var found = await rooms.ListAsync(active, after, take + 1, cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<RoomList, ValidationErrors, PortError>>(
            items => items.Count > take
                ? new RoomList(items.Take(take).ToList(), RoomCursor.After(items[take - 1]).Encode())
                : new RoomList(items, null),
            error => error);
    }

    public async Task<OneOf<Room, NotFound, ValidationErrors, PortError>> GetAsync(string id, CancellationToken cancellationToken)
    {
        if (!RoomIds.IsValid(id))
        {
            return InvalidId();
        }

        var found = await rooms.FindAsync(id, cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<Room, NotFound, ValidationErrors, PortError>>(room => room, notFound => notFound, error => error);
    }

    public async Task<OneOf<Room, ValidationErrors, ConflictError, PortError>> CreateAsync(Actor actor, CreateRoomCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(command);
        var name = command.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return NameRequired();
        }

        var ran = await transactions.RunAsync(ct => CreateInTransactionAsync(actor, name, command, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<Room, ValidationErrors, ConflictError, PortError>>(
            outcome => outcome.Match<OneOf<Room, ValidationErrors, ConflictError, PortError>>(room => room, error => error),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<Room, PortError>>> CreateInTransactionAsync(Actor actor, string name, CreateRoomCommand command, CancellationToken ct)
    {
        var sortOrder = command.SortOrder;
        if (sortOrder is null)
        {
            var last = await rooms.FindLastAsync(ct).ConfigureAwait(false);
            if (last.TryPickT2(out var lastError, out var rest))
            {
                return TransactionOutcome.Abort<OneOf<Room, PortError>>(lastError);
            }

            sortOrder = rest.Match(
                lastRoom => (int)Math.Min((long)lastRoom.SortOrder + SortOrderStep, int.MaxValue),
                _ => SortOrderStep);
        }

        var inserted = await rooms.InsertAsync(new NewRoom(name, sortOrder.Value, true, command.Virtual, time.GetUtcNow()), ct).ConfigureAwait(false);
        if (inserted.TryPickT1(out var insertError, out var room))
        {
            return TransactionOutcome.Abort<OneOf<Room, PortError>>(insertError);
        }

        var entry = ChangeSet.Between(null, Fields(room)).ToEntry(AuditActor.From(actor), AuditEntity.Room, room.Id, AuditAction.Create);
        return await CommitWithAuditAsync(entry, room, ct).ConfigureAwait(false);
    }

    public async Task<OneOf<Room, NotFound, ValidationErrors, ConflictError, PortError, PreconditionFailed>> UpdateAsync(
        Actor actor, string id, RoomPatch patch, CancellationToken cancellationToken, int? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(patch);
        if (!RoomIds.IsValid(id))
        {
            return InvalidId();
        }

        var name = patch.Name?.Trim();
        if (name is { Length: 0 })
        {
            return NameRequired();
        }

        var ran = await transactions.RunAsync(ct => UpdateInTransactionAsync(actor, id, patch with { Name = name }, expectedVersion, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<Room, NotFound, ValidationErrors, ConflictError, PortError, PreconditionFailed>>(
            outcome => outcome.Match<OneOf<Room, NotFound, ValidationErrors, ConflictError, PortError, PreconditionFailed>>(
                room => room, notFound => notFound, error => error, stale => stale),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<Room, NotFound, PortError, PreconditionFailed>>> UpdateInTransactionAsync(
        Actor actor, string id, RoomPatch patch, int? expectedVersion, CancellationToken ct)
    {
        static TransactionOutcome<OneOf<Room, NotFound, PortError, PreconditionFailed>> Abort(OneOf<Room, NotFound, PortError, PreconditionFailed> value) =>
            TransactionOutcome.Abort(value);

        var found = await rooms.FindAsync(id, ct).ConfigureAwait(false);
        if (found.TryPickT1(out var notFound, out var rest))
        {
            return Abort(notFound);
        }

        if (rest.TryPickT1(out var findError, out var before))
        {
            return Abort(findError);
        }

        // The precondition is checked before the no-op rule: a stale If-Match on a patch that changes nothing is still a 412.
        if (EntityVersion.Check(expectedVersion, before.Version) is { } stale)
        {
            return Abort(stale);
        }

        var after = before with
        {
            Name = patch.Name ?? before.Name,
            SortOrder = patch.SortOrder ?? before.SortOrder,
            Active = patch.Active ?? before.Active,
            Virtual = patch.Virtual ?? before.Virtual,
        };
        var changes = ChangeSet.Between(Fields(before), Fields(after));
        if (changes.IsNoOp)
        {
            return TransactionOutcome.Commit<OneOf<Room, NotFound, PortError, PreconditionFailed>>(before);
        }

        var updated = await rooms.UpdateAsync(
            id,
            new RoomChanges(
                after.Name != before.Name ? after.Name : null,
                after.SortOrder != before.SortOrder ? after.SortOrder : null,
                after.Active != before.Active ? after.Active : null,
                after.Virtual != before.Virtual ? after.Virtual : null),
            time.GetUtcNow(),
            ct,
            expectedVersion).ConfigureAwait(false);
        if (!updated.TryPickT0(out var room, out var updateFailure))
        {
            return Abort(updateFailure.Match<OneOf<Room, NotFound, PortError, PreconditionFailed>>(notFound => notFound, error => error, failed => failed));
        }

        var entry = changes.ToEntry(AuditActor.From(actor), AuditEntity.Room, id, AuditAction.Update);
        var recorded = await audit.RecordAsync(entry, ct).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<Room, NotFound, PortError, PreconditionFailed>>(room),
            error => Abort(error));
    }

    public async Task<OneOf<Success, NotFound, ValidationErrors, RoomInUse, ConflictError, PortError, PreconditionFailed>> DeleteAsync(
        Actor actor, string id, CancellationToken cancellationToken, int? expectedVersion = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (!RoomIds.IsValid(id))
        {
            return InvalidId();
        }

        var ran = await transactions.RunAsync(ct => DeleteInTransactionAsync(actor, id, expectedVersion, ct), cancellationToken).ConfigureAwait(false);
        return ran.Match<OneOf<Success, NotFound, ValidationErrors, RoomInUse, ConflictError, PortError, PreconditionFailed>>(
            outcome => outcome.Match<OneOf<Success, NotFound, ValidationErrors, RoomInUse, ConflictError, PortError, PreconditionFailed>>(
                success => success, notFound => notFound, inUse => inUse, error => error, stale => stale),
            conflict => conflict,
            error => error);
    }

    private async Task<TransactionOutcome<OneOf<Success, NotFound, RoomInUse, PortError, PreconditionFailed>>> DeleteInTransactionAsync(
        Actor actor, string id, int? expectedVersion, CancellationToken ct)
    {
        var found = await rooms.FindAsync(id, ct).ConfigureAwait(false);
        if (found.TryPickT1(out var notFound, out var rest))
        {
            return Abort(notFound);
        }

        if (rest.TryPickT1(out var findError, out var before))
        {
            return Abort(findError);
        }

        if (EntityVersion.Check(expectedVersion, before.Version) is { } stale)
        {
            return Abort(stale);
        }

        var counted = await tasks.CountInRoomAsync(id, ct).ConfigureAwait(false);
        if (counted.TryPickT1(out var countError, out var taskCount))
        {
            return Abort(countError);
        }

        if (taskCount > 0)
        {
            return Abort(new RoomInUse(taskCount));
        }

        var deleted = await rooms.DeleteAsync(id, ct, expectedVersion).ConfigureAwait(false);
        if (!deleted.TryPickT0(out _, out var deleteFailure))
        {
            return Abort(deleteFailure.Match<OneOf<Success, NotFound, RoomInUse, PortError, PreconditionFailed>>(notFound => notFound, error => error, failed => failed));
        }

        var entry = ChangeSet.Between(Fields(before), null).ToEntry(AuditActor.From(actor), AuditEntity.Room, id, AuditAction.Delete);
        var recorded = await audit.RecordAsync(entry, ct).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<Success, NotFound, RoomInUse, PortError, PreconditionFailed>>(new Success()),
            error => Abort(error));
    }

    private static TransactionOutcome<OneOf<Success, NotFound, RoomInUse, PortError, PreconditionFailed>> Abort(OneOf<Success, NotFound, RoomInUse, PortError, PreconditionFailed> value) =>
        TransactionOutcome.Abort(value);

    private async Task<TransactionOutcome<OneOf<Room, PortError>>> CommitWithAuditAsync(AuditEntry entry, Room room, CancellationToken ct)
    {
        var recorded = await audit.RecordAsync(entry, ct).ConfigureAwait(false);
        return recorded.Match(
            _ => TransactionOutcome.Commit<OneOf<Room, PortError>>(room),
            error => TransactionOutcome.Abort<OneOf<Room, PortError>>(error));
    }

    /// <summary>The fields an audit entry records for a room: the document without id and timestamps (as the Node server).</summary>
    private static AuditObject Fields(Room room) => AuditObject.Of(
        ("name", room.Name),
        ("sortOrder", room.SortOrder),
        ("active", room.Active),
        ("virtual", room.Virtual));

    private static ValidationErrors NameRequired() => ValidationErrors.For("name", "must not be empty");

    private static ValidationErrors InvalidId() => ValidationErrors.For("id", "invalid_object_id");
}
