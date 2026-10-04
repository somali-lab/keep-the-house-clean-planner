using Huishoudplanner.Adapters.Http.Export;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Domain.Transfer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using Microsoft.OpenApi;

namespace Huishoudplanner.Adapters.Http.Transfer;

/// <summary>
/// The JSON export and import of the whole dataset, <c>GET /api/v2/export/json</c> and <c>POST /api/v2/import/json</c> (requirements 4.11, 8;
/// <c>routes/transfer.ts</c> of the Node server). The export is open like in the Node server, so it needs no profile; the import replaces everything and
/// is for administrators (<see cref="AuthorizationPolicies.AdminPolicy"/>, <c>requireAdmin</c> there). A household's full history easily exceeds the
/// default body limit, so the import takes up to <see cref="MaxImportBytes"/>, the limit of the Node server, and nothing more: the body is never read
/// beyond it.
/// </summary>
public static class TransferEndpoints
{
    public const string TransferTag = "Transfer";

    /// <summary>The body limit of the import: 200 MB, as in the Node server.</summary>
    public const long MaxImportBytes = 200L * 1024 * 1024;

    public static IEndpointRouteBuilder MapTransferEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet("/api/v2/export/json", ExportAsync)
            .WithName("exportJson")
            .WithTags(TransferTag)
            .WithSummary("Downloads the whole dataset as one JSON file.")
            .WithDescription("Needs no profile, like the Node route. The file has schemaVersion 6, the exportedAt instant and, per collection, the documents as MongoDB relaxed Extended JSON ($oid, $date, $binary), so ids, dates and the badge images survive the round trip: settings, users, rooms, tasks, cyclePlans, cycles, occurrences, pointEntries (the redemptions only, the rest of the ledger is rebuilt on import), badges (with their images) and auditLog. The file is named huishoudplanner-YYYYMMDD.json after today in the household timezone.")
            .Produces<byte[]>(StatusCodes.Status200OK, "application/json")
            .AddOpenApiOperationTransformer(JsonAsBinary)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost("/api/v2/import/json", ImportAsync)
            .RequireAdmin()
            .WithMetadata(new RequestSizeLimitAttribute(MaxImportBytes))
            .WithName("importJson")
            .WithTags(TransferTag)
            .WithSummary("Replaces all data with an export file (administrators).")
            .WithDescription("The body is the export file; versions 1 to 6 are accepted. The query needs mode=replace (400 validation_error on mode otherwise) and confirm=true (400 confirmation_required). A file older than version 5 while redemptions exist needs acknowledgeRedemptions=true and one older than version 6 while badges exist needs acknowledgeBadges=true; without them the import is 409 redemptions_would_be_removed or badges_would_be_removed with the count, before anything is written. The whole file is validated before anything is touched: every problem is a 400 validation_error whose errors are keyed by the path in the file (collections.users.0.color), at most 200 of them. The replacement is one transaction with its audit entry (entity import, create): all or nothing. The audit log is merged, not replaced. The points ledger is dropped except for the redemptions of the file and rebuilt from the imported occurrences afterwards. The body limit is 200 MB.")
            .Accepts<object>("application/json")
            .Produces<ImportResult>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ExportAsync(
        HttpContext http,
        ITransferService transfer,
        ILogger<ITransferService> logger,
        CancellationToken cancellationToken)
    {
        var result = await transfer.ExportAsync(cancellationToken);
        return result.Match(
            file =>
            {
                http.Response.Headers[HeaderNames.ContentDisposition] = ExportEndpoints.AttachmentHeader(file.FileName);
                return Results.File(file.Content, file.ContentType);
            },
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> ImportAsync(
        HttpContext http,
        ITransferService transfer,
        ILogger<ITransferService> logger,
        CancellationToken cancellationToken,
        [FromQuery] string? mode = null,
        [FromQuery] string? confirm = null,
        [FromQuery] string? acknowledgeRedemptions = null,
        [FromQuery] string? acknowledgeBadges = null)
    {
        var actor = await http.GetActorAsync() ?? throw new InvalidOperationException("An import ran without an actor; the endpoint must require authorization.");
        var result = await transfer.ImportAsync(actor, new ImportOptions(mode, confirm, acknowledgeRedemptions, acknowledgeBadges), http.Request.Body, cancellationToken);
        return result.Match(
            done => Results.Ok(done),
            ProblemResults.From,
            _ => ProblemResults.Problem(StatusCodes.Status400BadRequest, "confirmation_required", "Importing replaces all data; add confirm=true."),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    /// <summary>A byte array is documented as base64 text (<c>format: byte</c>); a download is raw bytes, so the JSON content says <c>binary</c>.</summary>
    private static Task JsonAsBinary(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (operation.Responses is { } responses
            && responses.TryGetValue("200", out var ok)
            && ok.Content is { } content
            && content.TryGetValue("application/json", out var json))
        {
            json.Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
        }

        return Task.CompletedTask;
    }
}
