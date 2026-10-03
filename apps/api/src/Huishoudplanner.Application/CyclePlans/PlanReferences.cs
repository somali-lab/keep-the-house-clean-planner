using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Due;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Planning;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Rooms;
using Huishoudplanner.Domain.Tasks;
using Huishoudplanner.Domain.Users;
using OneOf;

namespace Huishoudplanner.Application.CyclePlans;

/// <summary>What plan validation reads besides the plan: the tasks, the people and the household intervals (<c>validateSlotsAgainstDb</c>).</summary>
internal sealed record PlanReferences(IReadOnlyList<PlanTask> Tasks, IReadOnlyList<PlanUser> Users, IReadOnlyList<Interval> Intervals)
{
    public PlanValidation Validate(IReadOnlyList<CyclePlanSlot> slots) =>
        PlanValidator.Validate(
            new PlanDraft([.. slots.Select(s => new PlanSlot(s.TaskId, s.WeekIndex, s.Weekday, s.AssigneeId))]),
            Tasks,
            Users,
            Intervals);
}

/// <summary>
/// Reads the references of a plan through the existing stores, page by page (the stores only list bounded pages). A missing settings
/// document means no intervals, as in the Node server.
/// </summary>
internal sealed class PlanReferenceReader(ForStoringTasks tasks, ForStoringUsers users, ForStoringRooms rooms, ForStoringSettings settings)
{
    private const int TaskPage = TaskListQuery.MaxLimit;

    private const int RoomPage = RoomListQuery.MaxLimit;

    public async Task<OneOf<PlanReferences, PortError>> ReadAsync(CancellationToken cancellationToken)
    {
        var allTasks = await ReadTasksAsync(cancellationToken).ConfigureAwait(false);
        if (allTasks.TryPickT1(out var taskError, out var taskList))
        {
            return taskError;
        }

        var allUsers = await ReadUsersAsync(cancellationToken).ConfigureAwait(false);
        if (allUsers.TryPickT1(out var userError, out var userList))
        {
            return userError;
        }

        var read = await settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (read.TryPickT2(out var settingsError, out var rest))
        {
            return settingsError;
        }

        IReadOnlyList<Interval> intervals = rest.Match(current => current.Intervals, _ => (IReadOnlyList<Interval>)[]);
        return new PlanReferences(
            [.. taskList.Select(t => new PlanTask(t.Id, t.Name, t.IntervalKey, t.DurationMinutes, t.Active))],
            [.. userList.Select(u => new PlanUser(
                u.Id,
                u.Name,
                u.Active,
                u.UnavailableWeekdays,
                new DayMinutes(u.DailyBudgetMinutes.Weekday, u.DailyBudgetMinutes.Weekend),
                new DayMinutes(u.MaxDailyMinutes.Weekday, u.MaxDailyMinutes.Weekend)))],
            intervals);
    }

    /// <summary>The names the diff shows: task name, minutes and the name of the task's room.</summary>
    public async Task<OneOf<IReadOnlyDictionary<string, PlanTaskInfo>, PortError>> ReadTaskInfoAsync(CancellationToken cancellationToken)
    {
        var allTasks = await ReadTasksAsync(cancellationToken).ConfigureAwait(false);
        if (allTasks.TryPickT1(out var taskError, out var taskList))
        {
            return taskError;
        }

        var roomNames = new Dictionary<string, string>(StringComparer.Ordinal);
        RoomCursor? after = null;
        while (true)
        {
            var page = await rooms.ListAsync(null, after, RoomPage, cancellationToken).ConfigureAwait(false);
            if (page.TryPickT1(out var roomError, out var roomList))
            {
                return roomError;
            }

            foreach (var room in roomList)
            {
                roomNames[room.Id] = room.Name;
            }

            if (roomList.Count < RoomPage)
            {
                break;
            }

            after = RoomCursor.After(roomList[^1]);
        }

        var info = new Dictionary<string, PlanTaskInfo>(StringComparer.Ordinal);
        foreach (var task in taskList)
        {
            info[task.Id] = new PlanTaskInfo(task.Name, roomNames.GetValueOrDefault(task.RoomId), task.DurationMinutes);
        }

        return info;
    }

    private async Task<OneOf<IReadOnlyList<HouseholdTask>, PortError>> ReadTasksAsync(CancellationToken cancellationToken)
    {
        var all = new List<HouseholdTask>();
        TaskCursor? after = null;
        while (true)
        {
            var page = await tasks.ListAsync(null, null, after, TaskPage, cancellationToken).ConfigureAwait(false);
            if (page.TryPickT1(out var error, out var items))
            {
                return error;
            }

            all.AddRange(items);
            if (items.Count < TaskPage)
            {
                return all;
            }

            after = TaskCursor.After(items[^1]);
        }
    }

    private async Task<OneOf<IReadOnlyList<User>, PortError>> ReadUsersAsync(CancellationToken cancellationToken)
    {
        var all = new List<User>();
        UserCursor? after = null;
        while (true)
        {
            var page = await users.ListAsync(new UserQuery(null, after, UserLimits.MaxPageSize), cancellationToken).ConfigureAwait(false);
            if (page.TryPickT1(out var error, out var found))
            {
                return error;
            }

            all.AddRange(found.Items);
            if (found.NextCursor is null || !UserCursor.TryParse(found.NextCursor, out var next))
            {
                return all;
            }

            after = next;
        }
    }
}
