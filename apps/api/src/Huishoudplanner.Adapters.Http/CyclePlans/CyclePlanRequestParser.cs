using System.Text.Json;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Adapters.Http.CyclePlans;

/// <summary>
/// Reads the JSON body of a plan write the way the Node Zod schemas did: every problem is a <c>400 validation_error</c> keyed by field
/// (a wrong type, an explicit <c>null</c> where none is allowed, a non-integer number, a slot member keyed <c>slots[2].weekday</c>);
/// malformed JSON and an empty body are keyed <c>body</c> by <see cref="Rooms.RoomRequestParser.ReadObjectAsync"/>. Unknown fields are
/// ignored. The rules about the values (empty name, id shape, position ranges) belong to <see cref="CyclePlanRules"/>.
/// </summary>
internal static class CyclePlanRequestParser
{
    /// <summary>A plan with a hundred tasks every day of the cycle is about 500 KB of slots; the Node server accepted a megabyte.</summary>
    private const int MaxSlotsBodyBytes = 1024 * 1024;

    public static Task<OneOf<JsonElement, ValidationErrors>> ReadBodyAsync(HttpContext http, CancellationToken cancellationToken) =>
        Rooms.RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken);

    public static Task<OneOf<JsonElement, ValidationErrors>> ReadSlotsBodyAsync(HttpContext http, CancellationToken cancellationToken) =>
        Rooms.RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken, MaxSlotsBodyBytes);

    public static OneOf<CreateCyclePlanCommand, ValidationErrors> ParseCreate(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = ReadString(body, "name", required: true, errors);
        var copyFromId = ReadString(body, "copyFromId", required: false, errors);
        return errors.Count > 0 ? new ValidationErrors(errors) : new CreateCyclePlanCommand(name!, copyFromId);
    }

    public static OneOf<CyclePlanPatch, ValidationErrors> ParsePatch(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = ReadString(body, "name", required: false, errors);
        var themes = ReadWeekThemes(body, errors);
        return errors.Count > 0 ? new ValidationErrors(errors) : new CyclePlanPatch(name, themes);
    }

    public static OneOf<IReadOnlyList<CyclePlanSlot>, ValidationErrors> ParseSlots(JsonElement body)
    {
        if (!body.TryGetProperty("slots", out var value))
        {
            return ValidationErrors.For("slots", "is required");
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return ValidationErrors.For("slots", "must be an array");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var slots = new List<CyclePlanSlot>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var prefix = $"slots[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                errors[prefix] = ["must be an object"];
            }
            else
            {
                var taskId = ReadString(item, "taskId", required: true, errors, prefix);
                var weekIndex = ReadInteger(item, "weekIndex", required: true, errors, prefix);
                var weekday = ReadInteger(item, "weekday", required: true, errors, prefix);
                var assignee = ReadNullableString(item, "assigneeId", errors, prefix);
                var sortOrder = ReadInteger(item, "sortOrder", required: false, errors, prefix);
                if (taskId is not null && weekIndex is not null && weekday is not null && assignee is not null)
                {
                    slots.Add(new CyclePlanSlot(taskId, weekIndex.Value, weekday.Value, assignee.Value.Id, sortOrder ?? 0));
                }
            }

            index++;
        }

        return errors.Count > 0 ? new ValidationErrors(errors) : slots;
    }

    private static string? ReadString(JsonElement body, string field, bool required, Dictionary<string, string[]> errors, string? prefix = null)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            if (required)
            {
                errors[Key(prefix, field)] = ["is required"];
            }

            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        errors[Key(prefix, field)] = ["must be a string"];
        return null;
    }

    /// <summary>A required string or explicit null; <see langword="null"/> when absent or wrong, a value (whose <c>Id</c> may be null) otherwise.</summary>
    private static (string? Id, bool Present)? ReadNullableString(JsonElement body, string field, Dictionary<string, string[]> errors, string prefix)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            errors[Key(prefix, field)] = ["is required"];
            return null;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return (null, true);
            case JsonValueKind.String:
                return (value.GetString(), true);
            default:
                errors[Key(prefix, field)] = ["must be a string or null"];
                return null;
        }
    }

    private static int? ReadInteger(JsonElement body, string field, bool required, Dictionary<string, string[]> errors, string prefix)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            if (required)
            {
                errors[Key(prefix, field)] = ["is required"];
            }

            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
            number == Math.Floor(number) && number is >= int.MinValue and <= int.MaxValue)
        {
            return (int)number;
        }

        errors[Key(prefix, field)] = ["must be a whole number"];
        return null;
    }

    private static List<string>? ReadWeekThemes(JsonElement body, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("weekThemes", out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            errors["weekThemes"] = ["must be an array of four strings"];
            return null;
        }

        var themes = new List<string>();
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                themes.Add(item.GetString()!);
            }
            else
            {
                errors[$"weekThemes.{index}"] = ["must be a string"];
            }

            index++;
        }

        return themes;
    }

    private static string Key(string? prefix, string field) => prefix is null ? field : $"{prefix}.{field}";
}
