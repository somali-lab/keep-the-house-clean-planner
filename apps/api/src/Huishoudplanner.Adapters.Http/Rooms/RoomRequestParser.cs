using System.Text.Json;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Rooms;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Rooms;

/// <summary>
/// Reads the JSON body of a room write the way the Node server's Zod schemas did: every problem is a
/// <c>400 validation_error</c> keyed by field (malformed JSON, a wrong type, <c>null</c> for a field, a non-integer sort order),
/// unknown fields are ignored, and the field is looked up case sensitively. The rules about the values (a name that is empty
/// after trimming) belong to the room use cases, not to this adapter.
/// </summary>
internal static class RoomRequestParser
{
    private const int MaxBodyBytes = 16 * 1024;

    public static async Task<OneOf<JsonElement, ValidationErrors>> ReadObjectAsync(HttpRequest request, CancellationToken cancellationToken, int maxBodyBytes = MaxBodyBytes)
    {
        ArgumentNullException.ThrowIfNull(request);
        var buffer = new byte[maxBodyBytes + 1];
        var read = await request.Body.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read > maxBodyBytes)
        {
            return ValidationErrors.For("body", "is too large");
        }

        if (read == 0)
        {
            return ValidationErrors.For("body", "is required");
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, read));
            var element = document.RootElement.Clone();
            return element.ValueKind == JsonValueKind.Object
                ? element
                : ValidationErrors.For("body", "must be a JSON object");
        }
        catch (JsonException)
        {
            return ValidationErrors.For("body", "is not valid JSON");
        }
    }

    public static OneOf<CreateRoomCommand, ValidationErrors> ParseCreate(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = ReadString(body, "name", required: true, errors);
        var sortOrder = ReadInteger(body, "sortOrder", errors);
        var isVirtual = ReadBoolean(body, "virtual", errors);
        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new CreateRoomCommand(name!, sortOrder, isVirtual ?? false);
    }

    public static OneOf<RoomPatch, ValidationErrors> ParsePatch(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = ReadString(body, "name", required: false, errors);
        var sortOrder = ReadInteger(body, "sortOrder", errors);
        var active = ReadBoolean(body, "active", errors);
        var isVirtual = ReadBoolean(body, "virtual", errors);
        return errors.Count > 0
            ? new ValidationErrors(errors)
            : new RoomPatch(name, sortOrder, active, isVirtual);
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

    private static int? ReadInteger(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
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
}
