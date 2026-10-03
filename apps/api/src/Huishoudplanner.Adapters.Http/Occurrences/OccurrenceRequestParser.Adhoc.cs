using System.Text.Json;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Tasks;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Occurrences;

/// <summary>
/// The bodies of the extra execution and the one-off task, read like the Node Zod schemas: a wrong type, an explicit <c>null</c> where none is
/// allowed, a non-integer number or a day that is not <c>YYYY-MM-DD</c> is a <c>400 validation_error</c> keyed by field. Only the syntax is
/// checked here; the rules (id shape, name length, ranges, request key format) belong to <see cref="AdhocRules"/>.
/// </summary>
internal static partial class OccurrenceRequestParser
{
    public static OneOf<ExtraExecutionCommand, ValidationErrors> ParseExtra(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var taskId = ReadString(body, "taskId", required: true, errors);
        var date = ReadDay(body, "date", errors);
        var assignee = ReadAssignee(body, errors);
        var done = ReadBoolean(body, "done", errors);
        var requestId = ReadString(body, "requestId", required: false, errors);
        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new ExtraExecutionCommand(taskId!, date!.Value, assignee, done ?? false, requestId);
    }

    public static OneOf<OneOffCommand, ValidationErrors> ParseOneOff(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = ReadString(body, "name", required: true, errors);
        var roomId = ReadNullableString(body, "roomId", errors);
        var duration = ReadInteger(body, "durationMinutes", required: true, errors);
        var date = ReadDay(body, "date", errors);
        var assignee = ReadAssignee(body, errors);
        var done = ReadBoolean(body, "done", errors);
        var points = ReadInteger(body, "points", required: false, errors);
        var requestId = ReadString(body, "requestId", required: false, errors);
        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new OneOffCommand(name!, roomId, duration!.Value, date!.Value, assignee, done ?? false, points, requestId);
    }

    /// <summary>The assignee: absent is "not said", an id is that person, an explicit <c>null</c> is "anyone".</summary>
    private static AssigneeChoice? ReadAssignee(JsonElement body, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("assigneeId", out var value))
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
                errors["assigneeId"] = ["must be a string or null"];
                return null;
        }
    }

    /// <summary>An optional string that may be an explicit <c>null</c> (a one-off task without a room).</summary>
    private static string? ReadNullableString(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (body.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ReadString(body, field, required: false, errors);
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
}
