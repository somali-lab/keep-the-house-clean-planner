using System.Text.Json;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Promotion;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Promotion;

/// <summary>
/// Reads the JSON body of the two promotion writes the way the Node Zod schemas did: every problem is a <c>400 validation_error</c> keyed by
/// field (a missing member, a wrong type, a non-integer number); malformed JSON and an empty body are keyed <c>body</c>. Unknown fields are ignored.
/// The rules about the values (id shape, week and weekday ranges) belong to <see cref="PromotionRules"/>.
/// </summary>
internal static class PromoteRequestParser
{
    public static Task<OneOf<JsonElement, ValidationErrors>> ReadBodyAsync(HttpContext http, CancellationToken cancellationToken) =>
        Rooms.RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken);

    public static OneOf<ApplyPromotionCommand, ValidationErrors> ParseApply(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var planId = ReadString(body, "planId", errors);
        var taskId = ReadString(body, "taskId", errors);
        var weekIndex = ReadInteger(body, "weekIndex", errors);
        var weekday = ReadInteger(body, "weekday", errors);
        var toWeekday = ReadInteger(body, "toWeekday", errors);
        string? toAssigneeId = null;
        if (body.TryGetProperty("toAssigneeId", out var assignee))
        {
            if (assignee.ValueKind == JsonValueKind.String)
            {
                toAssigneeId = assignee.GetString();
            }
            else
            {
                errors["toAssigneeId"] = ["must be a string"];
            }
        }

        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new ApplyPromotionCommand(planId!, taskId!, weekIndex!.Value, weekday!.Value, toWeekday!.Value, toAssigneeId);
    }

    public static OneOf<DismissedPromotion, ValidationErrors> ParseDismiss(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var planId = ReadString(body, "planId", errors);
        var taskId = ReadString(body, "taskId", errors);
        var weekIndex = ReadInteger(body, "weekIndex", errors);
        var weekday = ReadInteger(body, "weekday", errors);
        var toWeekday = ReadInteger(body, "toWeekday", errors);
        var lastEvidenceId = ReadString(body, "lastEvidenceId", errors);
        string? toAssigneeId = null;
        if (!body.TryGetProperty("toAssigneeId", out var assignee))
        {
            errors["toAssigneeId"] = ["is required"];
        }
        else if (assignee.ValueKind == JsonValueKind.String)
        {
            toAssigneeId = assignee.GetString();
        }
        else if (assignee.ValueKind != JsonValueKind.Null)
        {
            errors["toAssigneeId"] = ["must be a string or null"];
        }

        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new DismissedPromotion(planId!, taskId!, weekIndex!.Value, weekday!.Value, toWeekday!.Value, toAssigneeId, lastEvidenceId!);
    }

    private static string? ReadString(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            errors[field] = ["is required"];
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        errors[field] = ["must be a string"];
        return null;
    }

    private static int? ReadInteger(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            errors[field] = ["is required"];
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
