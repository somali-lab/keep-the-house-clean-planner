using Huishoudplanner.Application.Tasks;
using Huishoudplanner.Application.Tests.Rooms;
using Huishoudplanner.Application.Tests.Settings;
using Huishoudplanner.Application.Tests.Users;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Tasks;
using Huishoudplanner.Domain.Users;
using OneOf;
using Room = Huishoudplanner.Domain.Rooms.Room;

namespace Huishoudplanner.Application.Tests.Tasks;

/// <summary>An in-memory task store that counts writes, can fail, and answers the usage questions of the room and settings use cases.</summary>
internal sealed partial class FakeTaskStore : ForStoringTasks
{
    private int counter;

    public List<HouseholdTask> Items { get; set; } = [];

    /// <summary>Rooms and intervals that count as used on top of <see cref="Items"/>, for the room and settings tests.</summary>
    public Dictionary<string, int> ExtraTasksPerRoom { get; } = [];

    public List<string> ExtraIntervalsInUse { get; } = [];

    public int Writes { get; private set; }

    public int IntervalReads { get; private set; }

    public PortError? Failure { get; set; }

    public bool FailWrites { get; set; }

    public string NextId() => (++counter).ToString("x24", System.Globalization.CultureInfo.InvariantCulture);

    public Task<OneOf<IReadOnlyList<HouseholdTask>, PortError>> ListAsync(string? roomId, bool? active, TaskCursor? after, int take, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<HouseholdTask>, PortError>>(failure);
        }

        var page = Items
            .Where(t => roomId is null || t.RoomId == roomId)
            .Where(t => active is null || t.Active == active)
            .OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal)
            .Where(t => after is null || Compare(t, after) > 0)
            .Take(take)
            .ToList();
        return Task.FromResult<OneOf<IReadOnlyList<HouseholdTask>, PortError>>(page);
    }

    private static int Compare(HouseholdTask task, TaskCursor cursor)
    {
        var byName = string.CompareOrdinal(task.Name, cursor.Name);
        return byName != 0 ? byName : string.CompareOrdinal(task.Id, cursor.Id);
    }

    public Task<OneOf<HouseholdTask, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<HouseholdTask, NotFound, PortError>>(failure);
        }

        var task = Items.FirstOrDefault(t => t.Id == id);
        return Task.FromResult<OneOf<HouseholdTask, NotFound, PortError>>(task is null ? new NotFound() : task);
    }

    public Task<OneOf<IReadOnlyList<HouseholdTask>, PortError>> FindManyAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<HouseholdTask>, PortError>>(failure);
        }

        IReadOnlyList<HouseholdTask> found = [.. Items.Where(t => ids.Contains(t.Id)).OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal)];
        return Task.FromResult<OneOf<IReadOnlyList<HouseholdTask>, PortError>>(OneOf<IReadOnlyList<HouseholdTask>, PortError>.FromT0(found));
    }

    public Task<OneOf<IReadOnlyList<HouseholdTask>, PortError>> ListActiveInRoomAsync(string roomId, CancellationToken cancellationToken)
    {
        if (Failure is { } failure)
        {
            return Task.FromResult<OneOf<IReadOnlyList<HouseholdTask>, PortError>>(failure);
        }

        IReadOnlyList<HouseholdTask> active = [.. Items.Where(t => t.RoomId == roomId && t.Active).OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal)];
        return Task.FromResult<OneOf<IReadOnlyList<HouseholdTask>, PortError>>(OneOf<IReadOnlyList<HouseholdTask>, PortError>.FromT0(active));
    }

    public Task<OneOf<HouseholdTask, PortError>> InsertAsync(NewTask task, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            return Task.FromResult<OneOf<HouseholdTask, PortError>>(new PortError("fake write failure"));
        }

        Writes++;
        var stored = new HouseholdTask(
            NextId(), task.Name, task.RoomId, task.IntervalKey, task.DurationMinutes, task.Points, task.DefaultAssigneeId, true,
            task.Notes, task.Tags, null, task.CreatedAt, task.CreatedAt);
        Items.Add(stored);
        return Task.FromResult<OneOf<HouseholdTask, PortError>>(stored);
    }

    public Task<OneOf<HouseholdTask, NotFound, PortError>> UpdateAsync(string id, TaskChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        if (FailWrites)
        {
            return Task.FromResult<OneOf<HouseholdTask, NotFound, PortError>>(new PortError("fake write failure"));
        }

        var index = Items.FindIndex(t => t.Id == id);
        if (index < 0)
        {
            return Task.FromResult<OneOf<HouseholdTask, NotFound, PortError>>(new NotFound());
        }

        Writes++;
        var current = Items[index];
        var updated = current with
        {
            Name = changes.Name ?? current.Name,
            RoomId = changes.RoomId ?? current.RoomId,
            IntervalKey = changes.IntervalKey ?? current.IntervalKey,
            DurationMinutes = changes.DurationMinutes ?? current.DurationMinutes,
            Points = changes.Points ?? current.Points,
            DefaultAssigneeId = changes.DefaultAssignee is { } choice ? choice.UserId : current.DefaultAssigneeId,
            Active = changes.Active ?? current.Active,
            Notes = changes.Notes ?? current.Notes,
            Tags = changes.Tags ?? current.Tags,
            UpdatedAt = updatedAt,
        };
        Items[index] = updated;
        return Task.FromResult<OneOf<HouseholdTask, NotFound, PortError>>(updated);
    }

    public Task<OneOf<int, PortError>> CountInRoomAsync(string roomId, CancellationToken cancellationToken) =>
        Task.FromResult<OneOf<int, PortError>>(
            Failure is { } failure ? failure : Items.Count(t => t.RoomId == roomId) + ExtraTasksPerRoom.GetValueOrDefault(roomId));

    public Task<OneOf<IReadOnlyList<string>, PortError>> GetIntervalKeysInUseAsync(CancellationToken cancellationToken)
    {
        IntervalReads++;
        IReadOnlyList<string> keys = [.. Items.Select(t => t.IntervalKey).Concat(ExtraIntervalsInUse).Distinct(StringComparer.Ordinal)];
        return Task.FromResult<OneOf<IReadOnlyList<string>, PortError>>(
            Failure is { } failure ? failure : OneOf<IReadOnlyList<string>, PortError>.FromT0(keys));
    }
}

/// <summary>Runs the work once; an aborted run restores the tasks and the audit entries, like a rolled back transaction.</summary>
internal sealed class TaskTransactions(FakeTaskStore tasks, Rooms.FakeAudit audit, Generation.FakeOccurrenceStore occurrences) : ForRunningTransactions
{
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

        var tasksBefore = tasks.Items.ToList();
        var auditBefore = audit.Entries.ToList();
        var occurrencesBefore = occurrences.Items.ToList();
        var outcome = await work(cancellationToken);
        if (!outcome.ShouldCommit)
        {
            Aborts++;
            tasks.Items = tasksBefore;
            audit.Entries = auditBefore;
            occurrences.Items = occurrencesBefore;
        }

        return outcome.Value;
    }
}

/// <summary>The task use cases with hand-written in-memory ports for tasks, rooms, people and settings.</summary>
internal sealed class TaskWorld
{
    public static readonly DateTimeOffset Now = new(2026, 10, 3, 8, 0, 0, TimeSpan.Zero);

    public static readonly Actor Planner = new("0123456789abcdef01234567", Role.Planner, ActorSource.Ui);

    public FakeTaskStore TaskStore { get; } = new();

    public Rooms.FakeRooms RoomStore { get; } = new();

    public UserWorld People { get; } = new();

    public FakeSettingsStore SettingsStore { get; } = new(SettingsSamples.Seeded());

    public Rooms.FakeAudit Audit { get; } = new();

    public Generation.FakeOccurrenceStore Occurrences { get; } = new();

    public TaskTransactions Transactions { get; }

    public Rooms.FixedClock Clock { get; } = new(Now);

    public TaskService Service { get; }

    public TaskWorld()
    {
        Transactions = new TaskTransactions(TaskStore, Audit, Occurrences);
        Service = new TaskService(TaskStore, RoomStore, new FakeUserStore(People), SettingsStore, Occurrences, Transactions, Audit, Clock);
    }

    public Room Room(string name, bool active = true)
    {
        var room = new Room(RoomStore.NextId(), name, 10, active, false, Now.AddDays(-1), Now.AddDays(-1));
        RoomStore.Items.Add(room);
        return room;
    }

    public User Person(string name, bool active = true) => People.Add(name, Role.Member, active);

    public HouseholdTask Seed(string name, string roomId, string intervalKey = "1w", int minutes = 30, bool active = true, string? assignee = null)
    {
        var task = new HouseholdTask(TaskStore.NextId(), name, roomId, intervalKey, minutes, minutes, assignee, active, string.Empty, [], null, Now.AddDays(-1), Now.AddDays(-1));
        TaskStore.Items.Add(task);
        return task;
    }

    public static CreateTaskCommand Command(string roomId, string name = "Badkamer schoonmaken", string intervalKey = "1w", int minutes = 30) =>
        new(name, roomId, intervalKey, minutes);
}
