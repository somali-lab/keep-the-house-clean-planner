using System.Text.Json;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Tasks;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Tasks;

/// <summary>
/// Reads the JSON body of a task write the way the Node Zod schemas did: every problem is a <c>400 validation_error</c> keyed
/// by field (a wrong type, an explicit <c>null</c> where none is allowed, a non-integer number, a tag that is not a string, keyed
/// <c>tags.0</c>); malformed JSON and an empty body are keyed <c>body</c> by <see cref="Rooms.RoomRequestParser.ReadObjectAsync"/>.
/// Unknown fields are ignored. The rules about the values (empty name, duration below 1, points range, id shape) belong to
/// <see cref="TaskRules"/>.
/// </summary>
internal static class TaskRequestParser
{
    public static Task<OneOf<JsonElement, ValidationErrors>> ReadBodyAsync(HttpContext http, CancellationToken cancellationToken) =>
        Rooms.RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken);

    public static OneOf<CreateTaskCommand, ValidationErrors> ParseCreate(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = ReadString(body, "name", required: true, errors);
        var roomId = ReadString(body, "roomId", required: true, errors);
        var intervalKey = ReadString(body, "intervalKey", required: true, errors);
        var duration = ReadInteger(body, "durationMinutes", required: true, errors);
        var points = ReadInteger(body, "points", required: false, errors);
        var assignee = ReadNullableString(body, "defaultAssigneeId", errors);
        var notes = ReadString(body, "notes", required: false, errors);
        var tags = ReadTags(body, errors);
        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new CreateTaskCommand(name!, roomId!, intervalKey!, duration!.Value, points, assignee?.UserId, notes ?? string.Empty, tags);
    }

    public static OneOf<TaskPatch, ValidationErrors> ParsePatch(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = ReadString(body, "name", required: false, errors);
        var roomId = ReadString(body, "roomId", required: false, errors);
        var intervalKey = ReadString(body, "intervalKey", required: false, errors);
        var duration = ReadInteger(body, "durationMinutes", required: false, errors);
        var points = ReadInteger(body, "points", required: false, errors);
        var assignee = ReadNullableString(body, "defaultAssigneeId", errors);
        var active = ReadBoolean(body, "active", errors);
        var notes = ReadString(body, "notes", required: false, errors);
        var tags = ReadTags(body, errors);
        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new TaskPatch(name, roomId, intervalKey, duration, points, assignee, active, notes, tags);
    }

    public static OneOf<BulkRoomChange, ValidationErrors> ParseBulk(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var op = ReadString(body, "op", required: true, errors);
        if (op is not null && op is not ("deactivate" or "reassign"))
        {
            errors["op"] = ["must be 'deactivate' or 'reassign'"];
        }

        if (op == "reassign")
        {
            if (!body.TryGetProperty("defaultAssigneeId", out _))
            {
                errors["defaultAssigneeId"] = ["is required"];
            }
            else
            {
                var assignee = ReadNullableString(body, "defaultAssigneeId", errors);
                if (errors.Count == 0)
                {
                    return new BulkRoomChange.Reassign(assignee?.UserId);
                }
            }
        }

        if (errors.Count > 0)
        {
            return new ValidationErrors(errors);
        }

        return new BulkRoomChange.Deactivate();
    }

    private static string? ReadString(JsonElement body, string field, bool required, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            if (required)
            {
                errors[field] = ["is required"];
            }

            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        errors[field] = ["must be a string"];
        return null;
    }

    /// <summary>A string or an explicit null; <see langword="null"/> when the field is absent, an <see cref="AssigneeChoice"/> otherwise.</summary>
    private static AssigneeChoice? ReadNullableString(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return new AssigneeChoice(null);
            case JsonValueKind.String:
                return new AssigneeChoice(value.GetString());
            default:
                errors[field] = ["must be a string or null"];
                return null;
        }
    }

    private static int? ReadInteger(JsonElement body, string field, bool required, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            if (required)
            {
                errors[field] = ["is required"];
            }

            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
            number == Math.Floor(number) && number is >= int.MinValue and <= int.MaxValue)
        {
            return (int)number;
        }

        errors[field] = ["must be a whole number"];
        return null;
    }

    private static bool? ReadBoolean(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        errors[field] = ["must be true or false"];
        return null;
    }

    private static List<string>? ReadTags(JsonElement body, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("tags", out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            errors["tags"] = ["must be an array of strings"];
            return null;
        }

        var tags = new List<string>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                tags.Add(item.GetString()!);
                if (tags[^1].Trim().Length == 0)
                {
                    errors[$"tags.{index}"] = ["must not be empty"];
                }
            }
            else
            {
                errors[$"tags.{index}"] = ["must be a string"];
            }

            index++;
        }

        return tags;
    }
}
