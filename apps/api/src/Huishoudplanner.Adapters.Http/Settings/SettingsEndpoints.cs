using System.Text.Json;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.OpenApi;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Settings;

public static class SettingsEndpoints
{
    private const string IntervalInUseCode = "interval_in_use";

    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        // Open like the Node read: no policy, no actor needed.
        routes.MapGet("/api/v2/settings", async (ISettingsService settings, ILoggerFactory loggers, CancellationToken cancellationToken) =>
            {
                var result = await settings.GetAsync(cancellationToken);
                return result.Match(
                    view => Results.Ok(SettingsResponse.From(view)),
                    missing => ProblemResults.From(missing),
                    error => ProblemResults.From(error, Logger(loggers)));
            })
            .WithName("getSettings")
            .WithTags(OpenApiSetup.SettingsTag)
            .WithSummary("Returns the household settings.")
            .WithDescription("Missing optional values are delivered as their defaults: no bonus schedule, EUR, 0 cents per point and automatic reward goals. The bonus schedule rows carry startsInFuture and bonusesInForce holds the amounts in force today, so the client computes nothing. The AI API key is never part of the settings.")
            .Produces<SettingsResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPatch("/api/v2/settings", async (HttpContext http, ISettingsService settings, ILoggerFactory loggers, CancellationToken cancellationToken) =>
            {
                var actor = await http.GetActorAsync();
                var body = await ReadBodyAsync(http, cancellationToken);
                if (body.Errors is { } shapeErrors)
                {
                    return ProblemResults.From(shapeErrors);
                }

                var (patch, errors) = SettingsPatchReader.Read(body.Json);
                if (errors is not null)
                {
                    return ProblemResults.From(errors);
                }

                // The policy guarantees an actor; the check only keeps the compiler honest.
                var result = await settings.UpdateAsync(actor!, patch!, cancellationToken);
                return result.Match(
                    view => Results.Ok(SettingsResponse.From(view)),
                    invalid => ProblemResults.From(invalid),
                    inUse => ProblemResults.Problem(
                        StatusCodes.Status409Conflict,
                        IntervalInUseCode,
                        "Interval is used by tasks.",
                        new Dictionary<string, object?> { ["keys"] = inUse.Keys }),
                    conflict => ProblemResults.From(conflict),
                    missing => ProblemResults.From(missing),
                    error => ProblemResults.From(error, Logger(loggers)));
            })
            .RequireAdmin()
            .Accepts<UpdateSettingsRequest>("application/json")
            .WithName("updateSettings")
            .WithTags(OpenApiSetup.SettingsTag)
            .WithSummary("Changes the given settings (administrators only).")
            .WithDescription("Every field is optional and only the given ones change; a patch that changes nothing writes and audits nothing. periodBonuses sets the four bonus amounts from today on: the server writes a schedule row, and a client never sends the schedule. Amounts, currency, cents per point and goals equal to the ones in force change nothing. Removing an interval that tasks use is 409 interval_in_use (with the blocked keys in keys); when concurrent writers keep winning a schedule write the answer is 409 bonus_schedule_conflict. The timezone, the week start and the AI API key cannot be set.")
            .Produces<SettingsResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);
        return routes;
    }

    private static ILogger Logger(ILoggerFactory loggers) => loggers.CreateLogger("Huishoudplanner.Adapters.Http.Settings");

    private static async Task<(JsonElement Json, ValidationErrors? Errors)> ReadBodyAsync(HttpContext http, CancellationToken cancellationToken)
    {
        try
        {
            var json = await JsonSerializer.DeserializeAsync<JsonElement>(http.Request.Body, cancellationToken: cancellationToken);
            return (json, null);
        }
        catch (JsonException)
        {
            return (default, ValidationErrors.For("body", "invalid_json"));
        }
    }
}
