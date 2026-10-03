using System.Text.Json;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Users;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Users;

/// <summary>
/// Reads the JSON body of a users write the way the Node Zod schemas did: every problem is a <c>400 validation_error</c>
/// keyed by field with the dotted path of the Node server (malformed JSON and an empty body are keyed <c>body</c>, a wrong type,
/// an explicit <c>null</c> or a non-integer number is keyed by the field). Unknown fields are ignored. The rules about the values
/// (colour format, ranges, duplicate times) belong to <c>UserRules</c>; the body is read with
/// <see cref="Rooms.RoomRequestParser.ReadObjectAsync"/>.
/// </summary>
internal static class UserRequestParser
{
    public static Task<OneOf<JsonElement, ValidationErrors>> ReadBodyAsync(HttpContext http, CancellationToken cancellationToken) =>
        Rooms.RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken);

    public static CreateUserInput? ParseCreate(JsonElement body, out ValidationErrors? invalid)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var input = new CreateUserInput(
            ReadString(body, "name", errors),
            ReadString(body, "color", errors),
            ReadString(body, "role", errors),
            ReadWeekdays(body, errors),
            ReadMinutes(body, "dailyBudgetMinutes", errors),
            ReadMinutes(body, "maxDailyMinutes", errors));
        invalid = errors.Count > 0 ? new ValidationErrors(errors) : null;
        return invalid is null ? input : null;
    }

    public static UpdateUserInput? ParsePatch(JsonElement body, out ValidationErrors? invalid)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var input = new UpdateUserInput(
            ReadString(body, "name", errors),
            ReadString(body, "color", errors),
            ReadBoolean(body, "active", errors),
            ReadString(body, "role", errors),
            ReadWeekdays(body, errors),
            ReadMinutes(body, "dailyBudgetMinutes", errors),
            ReadMinutes(body, "maxDailyMinutes", errors),
            ReadNotifications(body, "browserNotifications", errors));
        invalid = errors.Count > 0 ? new ValidationErrors(errors) : null;
        return invalid is null ? input : null;
    }

    public static BrowserNotificationsInput? ParseNotifications(JsonElement body, out ValidationErrors? invalid)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var input = ReadNotificationFields(body, string.Empty, errors);
        invalid = errors.Count > 0 ? new ValidationErrors(errors) : null;
        return invalid is null ? input : null;
    }

    private static BrowserNotificationsInput? ReadNotifications(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            errors[field] = ["must be an object"];
            return null;
        }

        return ReadNotificationFields(value, field + ".", errors);
    }

    private static BrowserNotificationsInput ReadNotificationFields(JsonElement element, string prefix, Dictionary<string, string[]> errors)
    {
        var enabled = ReadBoolean(element, "enabled", errors, prefix);
        List<string>? times = null;
        if (element.TryGetProperty("times", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array)
            {
                errors[prefix + "times"] = ["must be an array of strings"];
            }
            else
            {
                times = [];
                var index = 0;
                foreach (var item in list.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        times.Add(item.GetString()!);
                    }
                    else
                    {
                        errors[$"{prefix}times.{index}"] = ["must be a string"];
                    }

                    index++;
                }
            }
        }

        return new BrowserNotificationsInput(enabled, times);
    }

    private static DailyMinutesInput? ReadMinutes(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            errors[field] = ["must be an object"];
            return null;
        }

        return new DailyMinutesInput(
            ReadInteger(value, "weekday", errors, field + "."),
            ReadInteger(value, "weekend", errors, field + "."));
    }

    private static List<int>? ReadWeekdays(JsonElement body, Dictionary<string, string[]> errors)
    {
        const string field = "unavailableWeekdays";
        if (!body.TryGetProperty(field, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            errors[field] = ["must be an array of whole numbers"];
            return null;
        }

        var days = new List<int>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (TryInteger(item, out var day))
            {
                days.Add(day);
            }
            else
            {
                errors[$"{field}.{index}"] = ["must be a whole number"];
            }

            index++;
        }

        return days;
    }

    private static string? ReadString(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        errors[field] = ["must be a string"];
        return null;
    }

    private static bool? ReadBoolean(JsonElement body, string field, Dictionary<string, string[]> errors, string prefix = "")
    {
        if (!body.TryGetProperty(field, out var value))
        {
            return null;
        }

        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return value.GetBoolean();
        }

        errors[prefix + field] = ["must be true or false"];
        return null;
    }

    private static int? ReadInteger(JsonElement body, string field, Dictionary<string, string[]> errors, string prefix)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            return null;
        }

        if (TryInteger(value, out var number))
        {
            return number;
        }

        errors[prefix + field] = ["must be a whole number"];
        return null;
    }

    private static bool TryInteger(JsonElement value, out int number)
    {
        number = 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var d) &&
            d == Math.Floor(d) && d is >= int.MinValue and <= int.MaxValue)
        {
            number = (int)d;
            return true;
        }

        return false;
    }
}
