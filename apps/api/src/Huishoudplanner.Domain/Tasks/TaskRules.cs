using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Limits;

namespace Huishoudplanner.Domain.Tasks;

/// <summary>
/// The value rules of a task (<c>createTaskInputSchema</c> and <c>updateTaskInputSchema</c> of the Node server): the shape of the
/// ids, a name and tags that are not empty after trimming, a duration of at least a minute and points from 0 to 1000. The
/// existence of the referenced room, interval and person is checked by the use case, which owns the stores.
/// </summary>
public static class TaskRules
{
    /// <summary>The API id format: 24 hexadecimal characters.</summary>
    public static bool IsId(string? id) =>
        id is { Length: 24 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F'));

    public static IReadOnlyList<string> NormaliseTags(IEnumerable<string> tags) => [.. tags.Select(t => t.Trim())];

    /// <summary>Field-keyed problems of a create command; <see langword="null"/> when it is valid.</summary>
    public static ValidationErrors? Validate(CreateTaskCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        CheckName(command.Name, errors);
        CheckId("roomId", command.RoomId, errors);
        CheckInterval(command.IntervalKey, errors);
        CheckDuration(command.DurationMinutes, errors);
        CheckPoints(command.Points, errors);
        if (command.DefaultAssigneeId is not null)
        {
            CheckId("defaultAssigneeId", command.DefaultAssigneeId, errors);
        }

        CheckTags(command.Tags, errors);
        return errors.Count == 0 ? null : new ValidationErrors(errors);
    }

    /// <summary>Field-keyed problems of a patch; <see langword="null"/> when it is valid.</summary>
    public static ValidationErrors? Validate(TaskPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (patch.Name is not null)
        {
            CheckName(patch.Name, errors);
        }

        if (patch.RoomId is not null)
        {
            CheckId("roomId", patch.RoomId, errors);
        }

        if (patch.IntervalKey is not null)
        {
            CheckInterval(patch.IntervalKey, errors);
        }

        if (patch.DurationMinutes is { } minutes)
        {
            CheckDuration(minutes, errors);
        }

        CheckPoints(patch.Points, errors);
        if (patch.DefaultAssignee?.UserId is { } assignee)
        {
            CheckId("defaultAssigneeId", assignee, errors);
        }

        CheckTags(patch.Tags, errors);
        return errors.Count == 0 ? null : new ValidationErrors(errors);
    }

    private static void CheckName(string name, Dictionary<string, string[]> errors)
    {
        if (name.Trim().Length == 0)
        {
            errors["name"] = ["must not be empty"];
        }
    }

    private static void CheckInterval(string key, Dictionary<string, string[]> errors)
    {
        if (key.Length == 0)
        {
            errors["intervalKey"] = ["must not be empty"];
        }
    }

    private static void CheckId(string field, string id, Dictionary<string, string[]> errors)
    {
        if (!IsId(id))
        {
            errors[field] = ["invalid_object_id"];
        }
    }

    private static void CheckDuration(int minutes, Dictionary<string, string[]> errors)
    {
        if (minutes < 1)
        {
            errors["durationMinutes"] = ["must be at least 1"];
        }
    }

    private static void CheckPoints(int? points, Dictionary<string, string[]> errors)
    {
        if (points is { } value && (value < TaskPoints.Min || value > TaskPoints.Max))
        {
            errors["points"] = [$"must be a whole number from {TaskPoints.Min} to {TaskPoints.Max}"];
        }
    }

    private static void CheckTags(IReadOnlyList<string>? tags, Dictionary<string, string[]> errors)
    {
        if (tags is null)
        {
            return;
        }

        for (var i = 0; i < tags.Count; i++)
        {
            if (tags[i].Trim().Length == 0)
            {
                errors[$"tags.{i}"] = ["must not be empty"];
            }
        }
    }
}
