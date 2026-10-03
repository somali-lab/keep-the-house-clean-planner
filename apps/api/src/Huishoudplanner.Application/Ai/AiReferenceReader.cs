using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driven;
using Huishoudplanner.Domain.Rooms;
using Huishoudplanner.Domain.Tasks;
using Huishoudplanner.Domain.Users;
using OneOf;

namespace Huishoudplanner.Application.Ai;

/// <summary>What the AI prompts are built from: every task, person and room, read page by page (the stores only list bounded pages).</summary>
internal sealed class AiReferenceReader(ForStoringTasks tasks, ForStoringUsers users, ForStoringRooms rooms)
{
    public async Task<OneOf<IReadOnlyList<HouseholdTask>, PortError>> ReadTasksAsync(CancellationToken cancellationToken)
    {
        var all = new List<HouseholdTask>();
        TaskCursor? after = null;
        while (true)
        {
            var page = await tasks.ListAsync(null, null, after, TaskListQuery.MaxLimit, cancellationToken).ConfigureAwait(false);
            if (page.TryPickT1(out var error, out var items))
            {
                return error;
            }

            all.AddRange(items);
            if (items.Count < TaskListQuery.MaxLimit)
            {
                return all;
            }

            after = TaskCursor.After(items[^1]);
        }
    }

    public async Task<OneOf<IReadOnlyList<User>, PortError>> ReadUsersAsync(CancellationToken cancellationToken)
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

    public async Task<OneOf<IReadOnlyList<Room>, PortError>> ReadRoomsAsync(CancellationToken cancellationToken)
    {
        var all = new List<Room>();
        RoomCursor? after = null;
        while (true)
        {
            var page = await rooms.ListAsync(null, after, RoomListQuery.MaxLimit, cancellationToken).ConfigureAwait(false);
            if (page.TryPickT1(out var error, out var items))
            {
                return error;
            }

            all.AddRange(items);
            if (items.Count < RoomListQuery.MaxLimit)
            {
                return all;
            }

            after = RoomCursor.After(items[^1]);
        }
    }
}
