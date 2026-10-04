using Huishoudplanner.Adapters.Http.Ai;
using Huishoudplanner.Adapters.Http.Badges;
using Huishoudplanner.Adapters.Http.Concurrency;
using Huishoudplanner.Adapters.Http.CyclePlans;
using Huishoudplanner.Adapters.Http.Cycles;
using Huishoudplanner.Adapters.Http.Due;
using Huishoudplanner.Adapters.Http.Promotion;
using Huishoudplanner.Adapters.Http.Export;
using Huishoudplanner.Adapters.Http.Health;
using Huishoudplanner.Adapters.Http.Statistics;
using Huishoudplanner.Adapters.Http.Occurrences;
using Huishoudplanner.Adapters.Http.Points;
using Huishoudplanner.Adapters.Http.Tasks;
using Huishoudplanner.Adapters.Http.Users;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;

namespace Huishoudplanner.Adapters.Http.OpenApi;

/// <summary>The v2 OpenAPI document: its name, metadata and tags. Endpoints describe themselves with metadata.</summary>
public static class OpenApiSetup
{
    /// <summary>The document name; also the file name <c>v2.json</c> and the route <c>/openapi/v2.json</c>.</summary>
    public const string DocumentName = "v2";

    public const string HealthTag = "Health";

    public const string MetaTag = "Meta";

    public const string CalendarTag = "Calendar";

    public const string SettingsTag = "Settings";
    public const string RoomsTag = "Rooms";

    public const string AuditTag = "Audit";

    public const string JobsTag = "Jobs";

    public static IServiceCollection AddOpenApiDocument(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddOpenApi(DocumentName, options =>
        {
            options.AddOperationTransformer((operation, context, _) =>
            {
                DescribeConcurrency(operation, context.Description.ActionDescriptor.EndpointMetadata);
                return Task.CompletedTask;
            });
            options.AddDocumentTransformer((document, context, _) =>
            {
                var version = context.ApplicationServices.GetRequiredService<AppVersion>().Value;
                document.Info = new OpenApiInfo
                {
                    Title = "Huishoudplanner API",
                    Version = version,
                    Description = "The v2 HTTP API of Keep the House Clean Planner. Errors are RFC 9457 Problem Details (application/problem+json).",
                };

                // Paths already carry the /api/v2 prefix, so the only server is the origin itself. Set explicitly: the
                // runtime document would otherwise list the request host, and build time and runtime must be identical.
                document.Servers = [new OpenApiServer { Url = "/" }];
                document.Tags = new HashSet<OpenApiTag>
                {
                    new() { Name = HealthTag, Description = "Liveness and database reachability, for load balancers and the container healthcheck." },
                    new() { Name = MetaTag, Description = "Limits and defaults the web app reads once per session." },
                    new() { Name = CalendarTag, Description = "Cycle, week and ISO week of calendar days." },
                    new() { Name = SettingsTag, Description = "Household settings: calendar, intervals, AI provider, bonuses, currency and reward goals." },
                    new() { Name = RoomsTag, Description = "The rooms of the house: everyone reads them, administrators create, change and delete them." },
                    new() { Name = JobsTag, Description = "Manual triggers of the scheduled jobs: planners start them." },
                    new() { Name = AuditTag, Description = "The history of changes: everyone reads it, administrators clear it." },
                    new() { Name = StatisticsEndpoints.StatisticsTag, Description = "Statistics over the execution history: workload, completion, intervals and deviations are open to everyone; administrators start the statistics over or purge old history." },
                    new() { Name = TaskEndpoints.TasksTag, Description = "The recurring household tasks: everyone reads them, planners create, change, deactivate and bulk-change them." },
                    new() { Name = DueEndpoints.DueTag, Description = "The due engine: every active task ranked by how far it has drifted past its interval." },
                    new() { Name = PromoteEndpoints.PromoteTag, Description = "Promote suggestions: slots of the active plan whose occurrences keep being moved the same way." },
                    new() { Name = ExportEndpoints.ExportTag, Description = "Printable PDF sheets: the week schedule, a single day, the due list and the task list. Everyone downloads them." },
                    new() { Name = CyclePlanEndpoints.CyclePlansTag, Description = "The four-week cycle plans: everyone reads and compares them, planners create, change, delete, save slots and validate." },
                    new() { Name = CycleEndpoints.CyclesTag, Description = "The generated four-week cycles: a read-only list, created by generation only." },
                    new() { Name = OccurrenceEndpoints.OccurrencesTag, Description = "What actually happened on a day: everyone reads the occurrences; members complete, uncomplete, skip, reschedule, assign and claim them, plan or record extra executions and one-off tasks and retract recorded work, administrators correct or delete a completion." },
                    new() { Name = PointsEndpoints.PointsTag, Description = "The points ledger: everyone reads the balances and the entries, administrators reconcile the ledger with the occurrences." },
                    new() { Name = BadgeEndpoints.BadgesTag, Description = "Badges: administrators define them with a rule and a picture, awards are derived from the audited executions; everyone reads the badges, the awards, the progress and the pictures." },
                    new() { Name = UserEndpoints.UsersTag, Description = "The people of the household: list, create, change, and their browser notification moments." },
                    new() { Name = AiEndpoints.AiTag, Description = "The AI assistant: prompt information, connection test, plan proposals and rebalancing (stored as drafts), task suggestions and plan explanations (stored nowhere); planners use it." },
                };
                return Task.CompletedTask;
            });
        });
        return services;
    }

    /// <summary>
    /// Optimistic concurrency (ADR-0022): the required <c>If-Match</c> header of an entity write and the <c>ETag</c> header of the success answer,
    /// described from the endpoint metadata of <see cref="IfMatchEndpointExtensions"/>.
    /// </summary>
    private static void DescribeConcurrency(OpenApiOperation operation, IList<object> metadata)
    {
        if (metadata.OfType<IfMatchRequiredMetadata>().Any())
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = ETags.IfMatchHeader,
                In = ParameterLocation.Header,
                Required = true,
                Description = "The ETag of the version you read, exactly as the server sent it (a version number in double quotes). Missing: 428 precondition_required; not the stored version: 412 precondition_failed with the current ETag; malformed, weak or *: 400 validation_error.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String },
            });
        }

        if (metadata.OfType<ReturnsETagMetadata>().Any() && operation.Responses is { } responses)
        {
            foreach (var (status, response) in responses)
            {
                if (status.StartsWith('2') && response is OpenApiResponse concrete)
                {
                    concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
                    concrete.Headers["ETag"] = new OpenApiHeader
                    {
                        Description = "The strong validator of the entity's current version (a version number in double quotes); send it as If-Match on the next write.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.String },
                    };
                }
            }
        }
    }
}
