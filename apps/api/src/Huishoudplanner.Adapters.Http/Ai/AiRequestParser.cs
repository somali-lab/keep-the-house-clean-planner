using System.Globalization;
using System.Text.Json;
using Huishoudplanner.Adapters.Http.Rooms;
using Huishoudplanner.Adapters.Http.Settings;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Settings;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Ai;

/// <summary>
/// Reads the JSON bodies of the AI endpoints the way the Node Zod schemas did: every problem is a <c>400 validation_error</c> keyed by field
/// (<c>required</c>, <c>expected_string</c>, <c>expected_array</c>), malformed JSON keyed <c>body</c>. Unknown fields are ignored. The rules about the
/// values (id shape, lengths, ranges) belong to the use case.
/// </summary>
internal static class AiRequestParser
{
    private const string Required = "required";
    private const string ExpectedString = "expected_string";

    public static Task<OneOf<JsonElement, ValidationErrors>> ReadBodyAsync(HttpContext http, CancellationToken cancellationToken) =>
        RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken);

    /// <summary>A proposal needs no body at all: nothing sent is the same as <c>{}</c> (Node: <c>request.body ?? {}</c>).</summary>
    public static async Task<OneOf<JsonElement, ValidationErrors>> ReadOptionalBodyAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var body = await RoomRequestParser.ReadObjectAsync(http.Request, cancellationToken).ConfigureAwait(false);
        if (body.TryPickT1(out var errors, out var json))
        {
            return errors.Errors is { Count: 1 } single && single.TryGetValue("body", out var messages) && messages is ["is required"]
                ? JsonDocument.Parse("{}").RootElement.Clone()
                : errors;
        }

        return json;
    }

    public static OneOf<AiProviderSettings, ValidationErrors> ParseTest(JsonElement body)
    {
        var (provider, errors) = SettingsPatchReader.ReadProviderBody(body);
        return errors is not null ? errors : provider!;
    }

    public static OneOf<ProposePlanCommand, ValidationErrors> ParsePropose(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        List<string>? taskIds = null;
        if (body.TryGetProperty("taskIds", out var ids))
        {
            if (ids.ValueKind != JsonValueKind.Array)
            {
                errors["taskIds"] = ["expected_array"];
            }
            else
            {
                taskIds = [];
                var index = 0;
                foreach (var item in ids.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        taskIds.Add(item.GetString()!);
                    }
                    else
                    {
                        errors[string.Create(CultureInfo.InvariantCulture, $"taskIds[{index}]")] = [ExpectedString];
                    }

                    index++;
                }
            }
        }

        var constraints = Optional(body, "constraints", errors);
        return errors.Count > 0 ? new ValidationErrors(errors) : new ProposePlanCommand(taskIds, constraints);
    }

    public static OneOf<RebalancePlanCommand, ValidationErrors> ParseRebalance(JsonElement body)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var planId = RequiredString(body, "planId", errors);
        var constraints = Optional(body, "constraints", errors);
        return errors.Count > 0 ? new ValidationErrors(errors) : new RebalancePlanCommand(planId!, constraints);
    }

    public static OneOf<string, ValidationErrors> ParseRoomId(JsonElement body) => ParseRequired(body, "roomId");

    public static OneOf<string, ValidationErrors> ParsePlanId(JsonElement body) => ParseRequired(body, "planId");

    private static OneOf<string, ValidationErrors> ParseRequired(JsonElement body, string field)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var value = RequiredString(body, field, errors);
        return errors.Count > 0 ? new ValidationErrors(errors) : value!;
    }

    private static string? RequiredString(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            errors[field] = [Required];
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        errors[field] = [ExpectedString];
        return null;
    }

    private static string? Optional(JsonElement body, string field, Dictionary<string, string[]> errors)
    {
        if (!body.TryGetProperty(field, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        errors[field] = [ExpectedString];
        return null;
    }
}
