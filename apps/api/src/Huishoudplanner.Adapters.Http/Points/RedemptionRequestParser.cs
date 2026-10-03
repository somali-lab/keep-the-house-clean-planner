using System.Text.Json;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Points;

/// <summary>
/// The body of a booking, read like the Node Zod schema: a wrong type, an explicit <c>null</c>, a fractional number or malformed JSON is a
/// <c>400 validation_error</c> keyed by field (malformed JSON by <c>body</c>). Unknown fields are ignored. Only the syntax is checked here; the rules
/// (at least 1 point, the length of the note, the shape of the id and the request key) belong to <see cref="RedemptionRules"/>.
/// </summary>
internal static class RedemptionRequestParser
{
    public static Task<OneOf<JsonElement, ValidationErrors>> ReadBodyAsync(HttpContext http, CancellationToken cancellationToken) =>
        Rooms.RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken);

    public static OneOf<RedemptionCommand, ValidationErrors> Parse(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var personId = ReadString(body, "personId", errors);
        var points = ReadPoints(body, errors);
        var note = ReadString(body, "note", errors);
        var requestId = ReadString(body, "requestId", errors);
        return errors.Count > 0 ? new ValidationErrors(errors) : new RedemptionCommand(personId, points!.Value, note, requestId);
    }

    private static int? ReadPoints(JsonElement body, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty("points", out var value))
        {
            errors["points"] = ["is required"];
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) &&
            number == Math.Floor(number) && number is >= int.MinValue and <= int.MaxValue)
        {
            return (int)number;
        }

        errors["points"] = ["must be a whole number"];
        return null;
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
}
