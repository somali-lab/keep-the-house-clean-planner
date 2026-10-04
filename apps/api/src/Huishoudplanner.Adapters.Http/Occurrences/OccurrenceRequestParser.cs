using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Huishoudplanner.Domain.Calendar;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Occurrences;

/// <summary>
/// Reads the bodies and the query of the occurrence endpoints the way the Node Zod schemas did: every problem is a <c>400 validation_error</c>
/// keyed by field (a wrong type, an explicit <c>null</c> where none is allowed, a day that is not <c>YYYY-MM-DD</c>, an instant without a time
/// zone); malformed JSON is keyed <c>body</c>. Unknown fields are ignored. Only the syntax is checked here; the rules (id shape, existence,
/// the choice of a completion, the length of a reason) belong to the use cases.
/// </summary>
internal static partial class OccurrenceRequestParser
{
    /// <summary>The bodies of complete, skip and claim are optional: no body at all is the same as <c>{}</c>.</summary>
    public static async Task<OneOf<JsonElement, ValidationErrors>> ReadOptionalBodyAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var body = await Rooms.RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken).ConfigureAwait(false);
        if (body.TryPickT1(out var errors, out var json) && errors.Errors.TryGetValue("body", out var messages) && messages is ["is required"])
        {
            using var empty = JsonDocument.Parse("{}");
            return empty.RootElement.Clone();
        }

        return body;
    }

    public static Task<OneOf<JsonElement, ValidationErrors>> ReadBodyAsync(HttpContext http, CancellationToken cancellationToken) =>
        Rooms.RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken);

    public static OneOf<CompleteCommand, ValidationErrors> ParseComplete(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var completedBy = ReadString(body, "completedBy", required: false, errors);
        var takeOver = false;
        if (body.TryGetProperty("takeOver", out var value))
        {
            if (value.ValueKind == JsonValueKind.True)
            {
                takeOver = true;
            }
            else
            {
                errors["takeOver"] = ["must be true"];
            }
        }

        return errors.Count > 0 ? new ValidationErrors(errors) : new CompleteCommand(completedBy, takeOver);
    }

    public static OneOf<EditCompletionCommand, ValidationErrors> ParseEditCompletion(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var date = ReadDay(body, "date", errors);
        var completedAt = ReadInstant(body, "completedAt", errors);
        var completedBy = ReadString(body, "completedBy", required: true, errors);
        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new EditCompletionCommand(date!.Value, completedAt!.Value, completedBy!);
    }

    public static OneOf<string?, ValidationErrors> ParseSkip(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var reason = ReadString(body, "reason", required: false, errors);
        return errors.Count > 0 ? new ValidationErrors(errors) : reason;
    }

    public static OneOf<DateOnly, ValidationErrors> ParseReschedule(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var date = ReadDay(body, "date", errors);
        return errors.Count > 0 ? new ValidationErrors(errors) : date!.Value;
    }

    /// <summary>The new assignee: an id, or <see langword="null"/> for anyone. The member is required, an explicit null is its value.</summary>
    public static OneOf<string?, ValidationErrors> ParseAssign(JsonElement body)
    {
        if (!body.TryGetProperty("assigneeId", out var value))
        {
            return ValidationErrors.For("assigneeId", "is required");
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => OneOf<string?, ValidationErrors>.FromT0(null),
            JsonValueKind.String => value.GetString(),
            _ => ValidationErrors.For("assigneeId", "must be a string or null"),
        };
    }

    /// <summary>The query of the list, from strings so that a malformed value is a field-keyed <c>validation_error</c> and never a binding failure.</summary>
    public static OneOf<OccurrenceListRequest, ValidationErrors> ParseList(string? from, string? to, string? assigneeId, string? status, string? limit, string? cursor, string? order = null)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var fromDay = QueryDay(from, "from", errors);
        var toDay = QueryDay(to, "to", errors);

        OccurrenceStatus? statusFilter = null;
        if (status is not null)
        {
            if (OccurrenceNames.TryParseStatus(status, out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors["status"] = ["must be one of: open, done, skipped"];
            }
        }

        int? limitValue = null;
        if (limit is not null)
        {
            if (int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                limitValue = parsed;
            }
            else
            {
                errors["limit"] = ["Must be an integer."];
            }
        }

        var orderValue = OccurrenceOrder.Ascending;
        if (order is not null)
        {
            switch (order)
            {
                case "asc":
                    break;
                case "desc":
                    orderValue = OccurrenceOrder.Descending;
                    break;
                default:
                    errors["order"] = ["must be one of: asc, desc"];
                    break;
            }
        }

        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new OccurrenceListRequest(fromDay!.Value, toDay!.Value, assigneeId, statusFilter, limitValue, cursor, orderValue);
    }

    private static bool TryDay(string value, out DateOnly day)
    {
        var valid = DayKeys.IsDayKey(value);
        day = valid ? DayKeys.Parse(value) : default;
        return valid;
    }

    private static DateOnly? QueryDay(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (value is null)
        {
            errors[field] = ["is required"];
            return null;
        }

        if (TryDay(value, out var day))
        {
            return day;
        }

        errors[field] = ["invalid_day_key"];
        return null;
    }

    private static DateOnly? ReadDay(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        var text = ReadString(body, field, required: true, errors);
        if (text is null)
        {
            return null;
        }

        if (TryDay(text, out var day))
        {
            return day;
        }

        errors[field] = ["invalid_day_key"];
        return null;
    }

    private static DateTimeOffset? ReadInstant(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        var text = ReadString(body, field, required: true, errors);
        if (text is null)
        {
            return null;
        }

        if (IsoInstant().IsMatch(text) &&
            DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return OccurrenceRules.WholeMilliseconds(parsed);
        }

        errors[field] = ["must be an ISO 8601 date and time with a time zone, for example 2026-09-18T08:00:00.000Z"];
        return null;
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

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}(:?\d{2})?)$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex IsoInstant();
}
