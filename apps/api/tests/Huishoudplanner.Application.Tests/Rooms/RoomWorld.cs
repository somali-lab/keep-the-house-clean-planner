using Huishoudplanner.Application.Rooms;
using Huishoudplanner.Application.Tests.Tasks;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Rooms;
using OneOf;

namespace Huishoudplanner.Application.Tests.Rooms;

/// <summary>
/// The room use cases with hand-written in-memory ports. The transaction fake rolls the rooms and the audit log back
/// when the work aborts, so a test can see that an entity write never survives without its entry.
/// </summary>
internal sealed class RoomWorld
{
    public static readonly DateTimeOffset Now = new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);

    public static readonly Actor Admin = new("0123456789abcdef01234567", Role.Admin, ActorSource.Ui);

    public FakeRooms Rooms { get; } = new();

    public FakeTaskStore Usage { get; } = new();

    public FakeAudit Audit { get; } = new();

    public FakeTransactions Transactions { get; }

    public FixedClock Clock { get; } = new(Now);

    public RoomService Service { get; }

    public RoomWorld()
    {
        Transactions = new FakeTransactions(Rooms, Audit);
        Service = new RoomService(Rooms, Usage, Transactions, Audit, Clock);
    }

    public Room Seed(string name, int sortOrder, bool active = true, bool isVirtual = false)
    {
        var room = new Room(Rooms.NextId(), name, sortOrder, active, isVirtual, Now.AddDays(-1), Now.AddDays(-1));
        Rooms.Items.Add(room);
        return room;
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class FakeRooms : ForStoringRooms
{
    private int counter;

    public List<Room> Items { get; set; } = [];

    public int Writes { get; private set; }

    public PortError? Failure { get; set; }

    public bool FailWrites { get; set; }

    public string NextId() => (++counter).ToString("x24", System.Globalization.CultureInfo.InvariantCulture);

    public void ResetWrites() => Writes = 0;

    public Task<OneOf<IReadOnlyList<Room>, PortError>> ListAsync(bool? active, RoomCursor? after, int take, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<Room>, PortError>>(failure);
        }

        var query = Items.Where(r => active is null || r.Active == active)
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal)
            .Where(r => after is null || Compare(r, after) > 0)
            .Take(take)
            .ToList();
        return Task.FromResult<OneOf<IReadOnlyList<Room>, PortError>>(query);
    }

    private static int Compare(Room room, RoomCursor cursor)
    {
        var bySort = room.SortOrder.CompareTo(cursor.SortOrder);
        if (bySort != 0)
        {
            return bySort;
        }

        var byName = string.CompareOrdinal(room.Name, cursor.Name);
        return byName != 0 ? byName : string.CompareOrdinal(room.Id, cursor.Id);
    }

    public Task<OneOf<Room, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<Room, NotFound, PortError>>(failure);
        }

        var room = Items.FirstOrDefault(r => r.Id == id);
        return Task.FromResult<OneOf<Room, NotFound, PortError>>(room is null ? new NotFound() : room);
    }

    public Task<OneOf<IReadOnlyList<Room>, PortError>> FindManyAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<Room>, PortError>>(failure);
        }

        IReadOnlyList<Room> found = [.. Items.Where(r => ids.Contains(r.Id)).OrderBy(r => r.SortOrder).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal)];
        return Task.FromResult<OneOf<IReadOnlyList<Room>, PortError>>(OneOf<IReadOnlyList<Room>, PortError>.FromT0(found));
    }

    public Task<OneOf<Room, NotFound, PortError>> FindLastAsync(CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<Room, NotFound, PortError>>(failure);
        }

        var room = Items.OrderByDescending(r => r.SortOrder).FirstOrDefault();
        return Task.FromResult<OneOf<Room, NotFound, PortError>>(room is null ? new NotFound() : room);
    }

    public Task<OneOf<Room, PortError>> InsertAsync(NewRoom room, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            return Task.FromResult<OneOf<Room, PortError>>(new PortError("fake write failure"));
        }

        Writes++;
        var stored = new Room(NextId(), room.Name, room.SortOrder, room.Active, room.Virtual, room.CreatedAt, room.CreatedAt);
        Items.Add(stored);
        return Task.FromResult<OneOf<Room, PortError>>(stored);
    }

    public Task<OneOf<Room, NotFound, PortError>> UpdateAsync(string id, RoomChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            return Task.FromResult<OneOf<Room, NotFound, PortError>>(new PortError("fake write failure"));
        }

        var index = Items.FindIndex(r => r.Id == id);
        if (index < 0)
        {
            return Task.FromResult<OneOf<Room, NotFound, PortError>>(new NotFound());
        }

        Writes++;
        var current = Items[index];
        var updated = current with
        {
            Name = changes.Name ?? current.Name,
            SortOrder = changes.SortOrder ?? current.SortOrder,
            Active = changes.Active ?? current.Active,
            Virtual = changes.Virtual ?? current.Virtual,
            UpdatedAt = updatedAt,
        };
        Items[index] = updated;
        return Task.FromResult<OneOf<Room, NotFound, PortError>>(updated);
    }

    public Task<OneOf<Success, NotFound, PortError>> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            return Task.FromResult<OneOf<Success, NotFound, PortError>>(new PortError("fake write failure"));
        }

        var removed = Items.RemoveAll(r => r.Id == id);
        if (removed == 0)
        {
            return Task.FromResult<OneOf<Success, NotFound, PortError>>(new NotFound());
        }

        Writes++;
        return Task.FromResult<OneOf<Success, NotFound, PortError>>(new Success());
    }
}

internal sealed class FakeAudit : ForRecordingAudit
{
    public List<AuditEntry> Entries { get; set; } = [];

    public PortError? Failure { get; set; }

    public Task<OneOf<Success, PortError>> RecordAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<Success, PortError>>(failure);
        }

        Entries.Add(entry);
        return Task.FromResult<OneOf<Success, PortError>>(new Success());
    }
}

internal sealed class FakeTransactions(FakeRooms rooms, FakeAudit audit) : ForRunningTransactions
{
    public int Runs { get; private set; }

    public int Aborts { get; private set; }

    public ConflictError? ConflictInsteadOfRunning { get; set; }

    public async Task<OneOf<T, ConflictError, PortError>> RunAsync<T>(
        Func<CancellationToken, Task<TransactionOutcome<T>>> work,
        CancellationToken cancellationToken)
    {
        if (ConflictInsteadOfRunning is { } conflict)
        {
            return conflict;
        }

        Runs++;
        var roomsBefore = rooms.Items.ToList();
        var auditBefore = audit.Entries.ToList();
        var outcome = await work(cancellationToken);
        if (!outcome.ShouldCommit)
        {
            Aborts++;
            rooms.Items = roomsBefore;
            audit.Entries = auditBefore;
        }

        return outcome.Value;
    }
}
