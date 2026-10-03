using System.ComponentModel;
using Huishoudplanner.Domain.Tasks;

namespace Huishoudplanner.Adapters.Http.Tasks;

// The request records below only document the bodies in OpenAPI; the bodies are read by TaskRequestParser (Zod-style
// validation_error for a wrong type, null, malformed JSON or an empty body), never bound to these types.

/// <summary>
/// Creates a task. <c>name</c>, <c>roomId</c> (an active room), <c>intervalKey</c> (a key of the household intervals) and
/// <c>durationMinutes</c> (a whole number of at least 1) are required. Without <c>points</c> the server applies the default for the duration.
/// </summary>
public sealed record CreateTaskRequest(
    string? Name,
    string? RoomId,
    string? IntervalKey,
    int? DurationMinutes,
    [property: Description("A whole number from 0 to 1000. Omitted: one point per minute, at least 1 and at most 1000.")] int? Points,
    [property: Description("An active person; null (the default) means anyone.")] string? DefaultAssigneeId,
    [property: Description("Free text, default empty.")] string? Notes,
    [property: Description("Tags, trimmed and not empty; default none.")] string[]? Tags);

/// <summary>Changes a task. Every member is optional; a member that is left out stays as it is. A task is deactivated with <c>active: false</c>.</summary>
public sealed record UpdateTaskRequest(
    string? Name,
    string? RoomId,
    string? IntervalKey,
    int? DurationMinutes,
    int? Points,
    [property: Description("An active person, or null for anyone. A change is audited as its own assign entry.")] string? DefaultAssigneeId,
    bool? Active,
    string? Notes,
    string[]? Tags);

/// <summary>The bulk change of the active tasks of one room: <c>deactivate</c>, or <c>reassign</c> with the required <c>defaultAssigneeId</c> (a person, or null for anyone).</summary>
public sealed record BulkRoomTasksRequest(
    [property: Description("deactivate or reassign.")] string? Op,
    string? DefaultAssigneeId);

/// <summary>A task as the API shows it. <c>points</c> is always present: a task from before points existed shows the default for its duration.</summary>
public sealed record TaskResponse(
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
    internal static TaskResponse From(HouseholdTask task) => new(
        task.Id,
        task.Name,
        task.RoomId,
        task.IntervalKey,
        task.DurationMinutes,
        task.Points,
        task.DefaultAssigneeId,
        task.Active,
        task.Notes,
        task.Tags,
        task.LastCompletedAt,
        task.CreatedAt,
        task.UpdatedAt);
}

/// <summary>One page of tasks; <see cref="NextCursor"/> is <c>null</c> on the last page.</summary>
public sealed record TaskListResponse(IReadOnlyList<TaskResponse> Items, string? NextCursor);

/// <summary>How many tasks a bulk change changed.</summary>
public sealed record BulkRoomTasksResponse(int Updated);
