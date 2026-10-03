using System.Text.Json;
using System.Text.RegularExpressions;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Adapters.Http.CyclePlans;

/// <summary>Reads the body of an activation the way the Node schema did: <c>previewToken</c> is required and must be 64 lowercase hexadecimal characters.</summary>
internal static partial class ActivationRequestParser
{
    public static Task<OneOf<JsonElement, ValidationErrors>> ReadBodyAsync(HttpContext http, CancellationToken cancellationToken) =>
        Rooms.RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken);

    public static OneOf<string, ValidationErrors> ParseToken(JsonElement body)
    {
        if (!body.TryGetProperty("previewToken", out var value))
        {
            return ValidationErrors.For("previewToken", "is required");
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return ValidationErrors.For("previewToken", "must be a string");
        }

        var token = value.GetString()!;
        return TokenShape().IsMatch(token) ? token : ValidationErrors.For("previewToken", "invalid");
    }

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TokenShape();
}
