using System.Buffers.Text;
using System.Text.Json;

namespace Huishoudplanner.Domain.Tasks;

/// <summary>
/// A recurring piece of household work (requirements 3, <c>tasks</c>). <see cref="Points"/> is the value in force: a task stored
/// before points existed reads as the default for its duration (ADR-0011). <see cref="DefaultAssigneeId"/> is <see langword="null"/>
/// for "anyone". <see cref="Id"/>, <see cref="RoomId"/> and <see cref="DefaultAssigneeId"/> are 24 character hexadecimal ids.
/// </summary>
public sealed record HouseholdTask(
    string Id,
    string Name,
    string RoomId,
    string IntervalKey,
    int DurationMinutes,
    int Points,
    string? DefaultAssigneeId,
    bool Active,
    string Notes,
    IReadOnlyList<string> Tags,
    DateTimeOffset? LastCompletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public bool Equals(HouseholdTask? other) =>
        other is not null &&
        Id == other.Id && Name == other.Name && RoomId == other.RoomId && IntervalKey == other.IntervalKey &&
        DurationMinutes == other.DurationMinutes && Points == other.Points && DefaultAssigneeId == other.DefaultAssigneeId &&
        Active == other.Active && Notes == other.Notes && Tags.SequenceEqual(other.Tags, StringComparer.Ordinal) &&
        LastCompletedAt == other.LastCompletedAt && CreatedAt == other.CreatedAt && UpdatedAt == other.UpdatedAt;

    public override int GetHashCode() => HashCode.Combine(Id, Name, RoomId, IntervalKey);
}

/// <summary>What the caller asks for when creating a task. A missing <see cref="Points"/> becomes the default for the duration.</summary>
public sealed record CreateTaskCommand(
    string Name,
    string RoomId,
    string IntervalKey,
    int DurationMinutes,
    int? Points = null,
    string? DefaultAssigneeId = null,
    string Notes = "",
    IReadOnlyList<string>? Tags = null);

/// <summary>The new value of the default assignee in a patch, where <see langword="null"/> is a value ("anyone"), not "absent".</summary>
public sealed record AssigneeChoice(string? UserId);

/// <summary>
/// A partial update: only the fields that are set change. The default assignee is wrapped because <c>null</c> is a valid new value. <see cref="ResetPoints"/>
/// (an explicit <c>points: null</c>) hands the points back to the default for the duration the task has after the patch; <see cref="Points"/> is then ignored.
/// </summary>
public sealed record TaskPatch(
    string? Name = null,
    string? RoomId = null,
    string? IntervalKey = null,
    int? DurationMinutes = null,
    int? Points = null,
    AssigneeChoice? DefaultAssignee = null,
    bool? Active = null,
    string? Notes = null,
    IReadOnlyList<string>? Tags = null,
    bool ResetPoints = false);

/// <summary>A task as the store is asked to create it (validated, normalised, points resolved). The store assigns the id.</summary>
public sealed record NewTask(
    string Name,
    string RoomId,
    string IntervalKey,
    int DurationMinutes,
    int Points,
    string? DefaultAssigneeId,
    string Notes,
    IReadOnlyList<string> Tags,
    DateTimeOffset CreatedAt);

/// <summary>The fields a store write changes: exactly the ones that differ from the stored task. <c>updatedAt</c> is set by the store.</summary>
public sealed record TaskChanges(
    string? Name = null,
    string? RoomId = null,
    string? IntervalKey = null,
    int? DurationMinutes = null,
    int? Points = null,
    AssigneeChoice? DefaultAssignee = null,
    bool? Active = null,
    string? Notes = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>The bulk change of one room (<c>POST /rooms/{id}/tasks/bulk</c>): deactivate every active task, or give them all one default assignee.</summary>
public abstract record BulkRoomChange
{
    private BulkRoomChange()
    {
    }

    public sealed record Deactivate : BulkRoomChange;

    public sealed record Reassign(string? UserId) : BulkRoomChange;
}

/// <summary>What a list read asks for. The order is fixed: name, then id.</summary>
public sealed record TaskListQuery(string? RoomId, bool? Active, int Limit, TaskCursor? After)
{
    public const int DefaultLimit = 50;

    public const int MaxLimit = 200;
}

/// <summary>One page of tasks. <see cref="NextCursor"/> is <see langword="null"/> on the last page.</summary>
public sealed record TaskList(IReadOnlyList<HouseholdTask> Items, string? NextCursor);

/// <summary>The position after a task in the list order (name, id). Opaque to clients: <see cref="Encode"/> gives the string they pass back as <c>cursor</c>.</summary>
public sealed record TaskCursor(string Name, string Id)
{
    public static TaskCursor After(HouseholdTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return new(task.Name, task.Id);
    }

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new object[] { Name, Id }));

    /// <summary>False for anything this application did not produce.</summary>
    public static bool TryDecode(string? value, out TaskCursor cursor)
    {
        cursor = null!;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(value));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() != 2 ||
                root[0].ValueKind != JsonValueKind.String || root[1].ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var id = root[1].GetString()!;
            if (!TaskRules.IsId(id))
            {
                return false;
            }

            cursor = new TaskCursor(root[0].GetString()!, id);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
