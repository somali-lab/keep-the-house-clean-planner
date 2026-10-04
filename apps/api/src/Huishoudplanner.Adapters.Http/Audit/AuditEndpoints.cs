using Huishoudplanner.Adapters.Http.Concurrency;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.OpenApi;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Audit;

/// <summary>
/// <c>/api/v2/audit</c> (requirements 4.9, 8). Reading needs no profile, like in the Node server; clearing the history needs
/// an administrator (<c>requireAdmin</c> there, <see cref="AuthorizationPolicies.AdminPolicy"/> here).
/// </summary>
public static class AuditEndpoints
{
    private const string Path = "/api/v2/audit";

    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path, ListAsync)
            .WithName("listAuditEntries")
            .WithTags(OpenApiSetup.AuditTag)
            .WithSummary("Lists the history of changes, newest first.")
            .WithDescription("Filters: entity, entityId, actorId, source (ui, api, ai or system), and from and to (ISO instants, both inclusive). The list is paged: pass the nextCursor of a page as cursor for the next one (limit 1 to 200, default 50); nextCursor is null on the last page. A malformed cursor is a validation_error with invalid_cursor on cursor. Old entries of occurrences carry the task, room and date of their occurrence in meta.occurrence.")
            .Produces<AuditListResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapDelete(Path, ClearAsync)
            .RequireAdmin()
            .WithoutIfMatch("Clears the whole history; there is no single entity to version.")
            .WithName("clearAuditLog")
            .WithTags(OpenApiSetup.AuditTag)
            .WithSummary("Clears the complete history (administrators).")
            .WithDescription("Deletes every audit entry and answers the number removed. The log is append-only except for this explicit clear and the retention job; clearing is deliberately not recorded in the log, because the entry would make the emptied history non-empty again.")
            .Produces<AuditClearedResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        IAuditLogService audit,
        ILogger<IAuditLogService> logger,
        CancellationToken cancellationToken,
        string? entity = null,
        string? entityId = null,
        string? actorId = null,
        string? source = null,
        string? from = null,
        string? to = null,
        string? cursor = null,
        string? limit = null)
    {
        // Bound as strings so that a malformed value is a field-keyed validation_error, not a framework binding failure.
        if (AuditQueryParser.Parse(entity, entityId, actorId, source, from, to, limit).TryPickT1(out var invalid, out var query))
        {
            return ProblemResults.From(invalid);
        }

        var result = await audit.ListAsync(query.Filter, query.Limit, cursor, cancellationToken);
        return result.Match(
            page => Results.Ok(new AuditListResponse([.. page.Items.Select(AuditEntryResponse.From)], page.NextCursor)),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> ClearAsync(
        IAuditLogService audit,
        ILogger<IAuditLogService> logger,
        CancellationToken cancellationToken)
    {
        var result = await audit.ClearAsync(cancellationToken);
        return result.Match(
            deleted => Results.Ok(new AuditClearedResponse(deleted)),
            error => ProblemResults.From(error, logger));
    }
}
