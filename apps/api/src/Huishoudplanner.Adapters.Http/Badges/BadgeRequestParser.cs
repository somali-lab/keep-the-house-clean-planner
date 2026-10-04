using System.Text.Json;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Badges;

/// <summary>
/// Reads the JSON body of a badge write the way the Node server's Zod schemas did: every problem is a <c>400 validation_error</c> keyed by the dotted
/// field path (malformed JSON, a wrong type, <c>null</c> for a field that cannot be null, a non-integer threshold, an unknown rule type), unknown
/// fields are ignored, and a field is looked up case sensitively. The rules about the values (lengths, thresholds, task ids, the image bytes) belong
/// to <see cref="BadgeValidation"/> and the use cases, not to this adapter.
/// </summary>
internal static class BadgeRequestParser
{
    /// <summary>A picture of 256 KB is 350 KB of base64 text; the rest of the body is small.</summary>
    private const int MaxBodyBytes = 512 * 1024;

    private const int OptionalBodyBytes = 16 * 1024;

    public static Task<OneOf<JsonElement, ValidationErrors>> ReadBodyAsync(HttpContext http, CancellationToken cancellationToken) =>
        Rooms.RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken, MaxBodyBytes);

    /// <summary>The body of the examples action is optional: no body is an empty object (the Node route read <c>request.body ?? {}</c>).</summary>
    public static async Task<OneOf<JsonElement, ValidationErrors>> ReadOptionalBodyAsync(HttpContext http, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);
        var buffer = new byte[OptionalBodyBytes + 1];
        var read = await http.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return Empty();
        }

        if (read > OptionalBodyBytes)
        {
            return ValidationErrors.For("body", "is too large");
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, read));
            var element = document.RootElement.Clone();
            return element.ValueKind == JsonValueKind.Object ? element : ValidationErrors.For("body", "must be a JSON object");
        }
        catch (JsonException)
        {
            return ValidationErrors.For("body", "is not valid JSON");
        }
    }

    private static JsonElement Empty()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    public static OneOf<CreateBadgeCommand, ValidationErrors> ParseCreate(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = ReadString(body, "name", required: true, errors);
        var description = ReadString(body, "description", required: false, errors);
        var rule = ReadRule(body, required: true, errors);
        var active = ReadBoolean(body, "active", errors);
        var image = body.TryGetProperty("image", out var imageValue) ? ReadImage(imageValue, nullable: false, errors) : null;
        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new CreateBadgeCommand(name!, description, rule!, active, image?.Image);
    }

    public static OneOf<BadgePatch, ValidationErrors> ParsePatch(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = ReadString(body, "name", required: false, errors);
        var description = ReadString(body, "description", required: false, errors);
        var rule = ReadRule(body, required: false, errors);
        var active = ReadBoolean(body, "active", errors);
        var image = body.TryGetProperty("image", out var imageValue) ? ReadImage(imageValue, nullable: true, errors) : null;
        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new BadgePatch(name, description, rule, active, image);
    }

    public static OneOf<BadgeLanguage, ValidationErrors> ParseExamples(JsonElement body)
    {
        if (!body.TryGetProperty("language", out var value))
        {
            return BadgeLanguage.Nl;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            switch (value.GetString())
            {
                case "nl":
                    return BadgeLanguage.Nl;
                case "en":
                    return BadgeLanguage.En;
            }
        }

        return ValidationErrors.For("language", "must be nl or en");
    }

    private static BadgeRuleInput? ReadRule(JsonElement body, bool required, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("rule", out var rule))
        {
            if (required)
            {
                errors["rule"] = ["is required"];
            }

            return null;
        }

        if (rule.ValueKind != JsonValueKind.Object)
        {
            errors["rule"] = ["must be an object"];
            return null;
        }

        if (!rule.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String ||
            !BadgeNames.TryParseRuleType(typeValue.GetString(), out var type))
        {
            errors["rule.type"] = ["must be executions, minutes or onTimeWeeks"];
            return null;
        }

        var threshold = ReadInteger(rule, "threshold", "rule.threshold", errors);
        IReadOnlyList<string> taskIds = [];
        if (type != BadgeRuleType.OnTimeWeeks)
        {
            taskIds = ReadTaskIds(rule, errors);
        }

        return errors.ContainsKey("rule.threshold") || errors.ContainsKey("rule.taskIds") ? null : new BadgeRuleInput(type, taskIds, threshold!.Value);
    }

    private static IReadOnlyList<string> ReadTaskIds(JsonElement rule, Dictionary<string, string[]> errors)
    {
        if (!rule.TryGetProperty("taskIds", out var value))
        {
            errors["rule.taskIds"] = ["is required"];
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String))
        {
            errors["rule.taskIds"] = ["must be an array of ids"];
            return [];
        }

        return [.. value.EnumerateArray().Select(item => item.GetString()!)];
    }

    private static BadgeImageChange? ReadImage(JsonElement value, bool nullable, Dictionary<string, string[]> errors)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (nullable)
            {
                return new BadgeImageChange(null);
            }

            errors["image"] = ["must be an object"];
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            errors["image"] = ["must be an object"];
            return null;
        }

        string? contentType = null;
        string? data = null;
        if (!value.TryGetProperty("contentType", out var type) || type.ValueKind != JsonValueKind.String)
        {
            errors["image.contentType"] = [type.ValueKind == default ? "is required" : "must be a string"];
        }
        else
        {
            contentType = type.GetString();
        }

        if (!value.TryGetProperty("data", out var bytes) || bytes.ValueKind != JsonValueKind.String)
        {
            errors["image.data"] = [bytes.ValueKind == default ? "is required" : "must be a string"];
        }
        else
        {
            data = bytes.GetString();
        }

        return contentType is null || data is null ? null : new BadgeImageChange(new BadgeImageInput(contentType, data));
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

    private static int? ReadInteger(JsonElement body, string field, string key, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            errors[key] = ["is required"];
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
            number == Math.Floor(number) && number is >= int.MinValue and <= int.MaxValue)
        {
            return (int)number;
        }

        errors[key] = ["must be a whole number"];
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
}
