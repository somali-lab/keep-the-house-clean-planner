using System.Globalization;
using Huishoudplanner.Adapters.Http.Concurrency;
using Huishoudplanner.Adapters.Http.Identity;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Http.Badges;

/// <summary>
/// <c>/api/v2/badges</c> (requirements 4.13, 8; <c>routes/badges.ts</c>). Reads, the picture included, need no profile like in the Node server; every
/// write is an administrator's (<c>requireAdmin</c> there, <see cref="AuthorizationPolicies.AdminPolicy"/> here). Query values are bound as strings so
/// that a malformed value is a field-keyed <c>400 validation_error</c>, not a framework binding failure.
/// </summary>
public static class BadgeEndpoints
{
    public const string BadgesTag = "Badges";

    private const string Path = "/api/v2/badges";

    public static IEndpointRouteBuilder MapBadgeEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapGet(Path, ListAsync)
            .WithName("listBadges")
            .WithTags(BadgesTag)
            .WithSummary("Lists the badges, oldest first.")
            .WithDescription("Needs no profile. active=true or active=false limits the list to active or inactive badges. A badge carries its rule and, when it has a picture, image { contentType, size, hash, url }, never the bytes. The list is paged: pass the nextCursor of a page as cursor for the next one (limit 1 to 100, default 50).")
            .Produces<BadgeListResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/{id}", GetAsync)
            .WithName("getBadge")
            .WithTags(BadgesTag)
            .WithSummary("Reads one badge.")
            .WithDescription("Needs no profile. The ETag header carries the version of the badge; send it as If-Match when you change or delete the badge. Answers 404 not_found for an unknown badge and 400 validation_error on id for a malformed id.")
            .Produces<BadgeResponse>(StatusCodes.Status200OK)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path, CreateAsync)
            .RequireAdmin()
            .WithName("createBadge")
            .WithTags(BadgesTag)
            .WithSummary("Creates a badge (administrators).")
            .WithDescription("The badge is active unless active is false. The rule is executions or minutes over the chosen tasks (none chosen counts every task) or onTimeWeeks; tasks that do not exist are dropped and a rule that names only unknown tasks is refused (rule.taskIds unknown_task). The picture is { contentType, data } with at most 256 KB of PNG, JPEG or WebP as base64; image.data answers invalid_base64, image_too_large or unsupported_image_type and image.contentType image_type_mismatch. At most 100 badges can exist (409 badge_limit). The change is audited, then the awards are evaluated again.")
            .Accepts<CreateBadgeRequest>("application/json")
            .Produces<BadgeResponse>(StatusCodes.Status201Created)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPost(Path + "/examples", AddExamplesAsync)
            .RequireAdmin()
            .WithName("addExampleBadges")
            .WithTags(BadgesTag)
            .WithSummary("Adds the example badges that do not exist yet (administrators).")
            .WithDescription("Takes an optional body { language: 'nl' | 'en' } (default nl). The examples are created once by their stable key, so calling it again, or after one was renamed, creates nothing. The tasks of an example are found among the active tasks by name; an example without a task is created inactive. Answers the badges it created and how many examples already existed. 409 badge_limit beyond 100 badges.")
            .Accepts<AddExampleBadgesRequest>("application/json")
            .Produces<AddExampleBadgesResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/awards", AwardsAsync)
            .WithName("listBadgeAwards")
            .WithTags(BadgesTag)
            .WithSummary("The awards, oldest first, optionally of one person.")
            .WithDescription("Needs no profile. An award is derived from the audited executions and recomputed, so it can disappear when the data does; awardedAt is the moment the data first crossed the threshold. The list is paged: pass the nextCursor of a page as cursor for the next one (limit 1 to 500, default 100).")
            .Produces<BadgeAwardListResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/progress", ProgressAsync)
            .WithName("getBadgeProgress")
            .WithTags(BadgesTag)
            .WithSummary("How far one person is towards every active badge.")
            .WithDescription("Needs no profile; personId is required. Evaluated on the data at the moment of the request, not read from the stored awards. awardedAt is null while the badge is not earned and current can exceed threshold.")
            .Produces<BadgeProgressResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapGet(Path + "/{id}/image", ImageAsync)
            .WithName("getBadgeImage")
            .WithTags(BadgesTag)
            .WithSummary("The picture of a badge.")
            .WithDescription("Needs no profile. Serves the checked bytes with their content type, an ETag of the hash, nosniff and a restrictive Content-Security-Policy, and answers 304 to a matching If-None-Match (a list of tags, weak ones and * included). The address of a badge view ends in ?v=<first 12 characters of the hash>; only such an address is cached for good (public, max-age=31536000, immutable), any other revalidates (no-cache). 404 for an unknown badge and for a badge without a picture.")
            .Produces<byte[]>(StatusCodes.Status200OK, "image/png", "image/jpeg", "image/webp")
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapPatch(Path + "/{id}", UpdateAsync)
            .RequireAdmin()
            .RequireIfMatch()
            .WithName("updateBadge")
            .WithTags(BadgesTag)
            .WithSummary("Changes a badge (administrators).")
            .WithDescription("Every field is optional; image: null removes the picture and a new image replaces it. A change that changes nothing writes and audits nothing. A change of the rule or the active flag evaluates the awards again. Answers 404 not_found for an unknown badge. Needs If-Match with the ETag of the badge you read: another version is 412 precondition_failed (also for a change that would change nothing), and a change that changes nothing keeps the version.")
            .Accepts<UpdateBadgeRequest>("application/json")
            .Produces<BadgeResponse>(StatusCodes.Status200OK)
            .ReturnsETag()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        routes.MapDelete(Path + "/{id}", DeleteAsync)
            .RequireAdmin()
            .RequireIfMatch()
            .WithName("deleteBadge")
            .WithTags(BadgesTag)
            .WithSummary("Deletes a badge (administrators).")
            .WithDescription("The awards of the badge are withdrawn with it. Answers 404 not_found for an unknown badge. Needs If-Match with the ETag of the badge you read; another version is 412 precondition_failed and nothing is deleted.")
            .Produces<BadgeDeletedResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return routes;
    }

    private static async Task<IResult> ListAsync(
        IBadgeService badges,
        ILogger<IBadgeService> logger,
        CancellationToken cancellationToken,
        string? active = null,
        string? limit = null,
        string? cursor = null)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        bool? activeFilter = null;
        if (active is not null)
        {
            if (active is "true" or "false")
            {
                activeFilter = active == "true";
            }
            else
            {
                errors["active"] = ["Must be 'true' or 'false'."];
            }
        }

        var limitValue = ParseLimit(limit, errors);
        if (errors.Count > 0)
        {
            return ProblemResults.From(new ValidationErrors(errors));
        }

        var result = await badges.ListAsync(activeFilter, limitValue, cursor, cancellationToken);
        return result.Match(
            list => Results.Ok(new BadgeListResponse([.. list.Items.Select(BadgeResponse.From)], list.NextCursor)),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> GetAsync(
        string id,
        HttpContext http,
        IBadgeService badges,
        ILogger<IBadgeService> logger,
        CancellationToken cancellationToken)
    {
        var result = await badges.GetAsync(id, cancellationToken);
        return result.Match(
            badge =>
            {
                ETags.Set(http.Response, badge.Version);
                return Results.Ok(BadgeResponse.From(badge));
            },
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> AwardsAsync(
        IBadgeService badges,
        ILogger<IBadgeService> logger,
        CancellationToken cancellationToken,
        string? personId = null,
        string? limit = null,
        string? cursor = null)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var limitValue = ParseLimit(limit, errors);
        if (errors.Count > 0)
        {
            return ProblemResults.From(new ValidationErrors(errors));
        }

        var result = await badges.AwardsAsync(personId, limitValue, cursor, cancellationToken);
        return result.Match(
            list => Results.Ok(new BadgeAwardListResponse([.. list.Items.Select(BadgeAwardResponse.From)], list.NextCursor)),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> ProgressAsync(
        IBadgeService badges,
        ILogger<IBadgeService> logger,
        CancellationToken cancellationToken,
        string? personId = null)
    {
        if (personId is null)
        {
            return ProblemResults.From(ValidationErrors.For("personId", "is required"));
        }

        var result = await badges.ProgressAsync(personId, cancellationToken);
        return result.Match(
            progress => Results.Ok(BadgeProgressResponse.From(progress)),
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> ImageAsync(
        string id,
        HttpContext http,
        IBadgeService badges,
        ILogger<IBadgeService> logger,
        CancellationToken cancellationToken)
    {
        var result = await badges.ImageAsync(id, cancellationToken);
        if (!result.TryPickT0(out var image, out var failure))
        {
            return failure.Match(ProblemResults.From, error => ProblemResults.From(error, logger));
        }

        // Only an address that carries the hash of these bytes may be cached for good; any other address revalidates.
        var version = http.Request.Query["v"].ToString();
        var versioned = version.Length >= BadgeImageInfo.VersionLength && image.Hash.StartsWith(version, StringComparison.Ordinal);
        var headers = http.Response.Headers;
        headers.ETag = $"\"{image.Hash}\"";
        headers.CacheControl = versioned ? "public, max-age=31536000, immutable" : "no-cache";
        headers.XContentTypeOptions = "nosniff";
        headers.ContentSecurityPolicy = "default-src 'none'; sandbox";
        return MatchesETag(http.Request.Headers.IfNoneMatch.ToString(), image.Hash)
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Bytes(image.Bytes, image.ContentType.ContentType());
    }

    /// <summary>Whether an <c>If-None-Match</c> header (a list of entity tags, weak ones and * included) names the bytes with this hash.</summary>
    internal static bool MatchesETag(string? header, string hash)
    {
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        return header.Split(',').Any(part =>
        {
            var tag = part.Trim();
            return tag == "*" || (tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag) == $"\"{hash}\"";
        });
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        IBadgeService badges,
        ILogger<IBadgeService> logger,
        CancellationToken cancellationToken)
    {
        var body = await BadgeRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (BadgeRequestParser.ParseCreate(json).TryPickT1(out var invalid, out var command))
        {
            return ProblemResults.From(invalid);
        }

        var result = await badges.CreateAsync(await ActorOf(http), command, cancellationToken);
        return result.Match(
            badge =>
            {
                ETags.Set(http.Response, badge.Version);
                return Results.Json(BadgeResponse.From(badge), statusCode: StatusCodes.Status201Created);
            },
            ProblemResults.From,
            LimitProblem,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> AddExamplesAsync(
        HttpContext http,
        IBadgeService badges,
        ILogger<IBadgeService> logger,
        CancellationToken cancellationToken)
    {
        var body = await BadgeRequestParser.ReadOptionalBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (BadgeRequestParser.ParseExamples(json).TryPickT1(out var invalid, out var language))
        {
            return ProblemResults.From(invalid);
        }

        var result = await badges.AddExamplesAsync(await ActorOf(http), language, cancellationToken);
        return result.Match(
            added => Results.Ok(new AddExampleBadgesResponse([.. added.Created.Select(BadgeResponse.From)], added.Skipped)),
            LimitProblem,
            ProblemResults.From,
            error => ProblemResults.From(error, logger));
    }

    private static async Task<IResult> UpdateAsync(
        string id,
        HttpContext http,
        IBadgeService badges,
        ILogger<IBadgeService> logger,
        CancellationToken cancellationToken)
    {
        var body = await BadgeRequestParser.ReadBodyAsync(http, cancellationToken);
        if (body.TryPickT1(out var invalidBody, out var json))
        {
            return ProblemResults.From(invalidBody);
        }

        if (BadgeRequestParser.ParsePatch(json).TryPickT1(out var invalid, out var patch))
        {
            return ProblemResults.From(invalid);
        }

        var result = await badges.UpdateAsync(await ActorOf(http), id, patch, cancellationToken, http.GetIfMatch());
        return result.Match(
            badge =>
            {
                ETags.Set(http.Response, badge.Version);
                return Results.Ok(BadgeResponse.From(badge));
            },
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger),
            ProblemResults.From);
    }

    private static async Task<IResult> DeleteAsync(
        string id,
        HttpContext http,
        IBadgeService badges,
        ILogger<IBadgeService> logger,
        CancellationToken cancellationToken)
    {
        var result = await badges.DeleteAsync(await ActorOf(http), id, cancellationToken, http.GetIfMatch());
        return result.Match(
            _ => Results.Ok(new BadgeDeletedResponse(true)),
            ProblemResults.From,
            ProblemResults.From,
            ProblemResults.From,
            error => ProblemResults.From(error, logger),
            ProblemResults.From);
    }

    private static int? ParseLimit(string? limit, Dictionary<string, string[]> errors)
    {
        if (limit is null)
        {
            return null;
        }

        if (int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        errors["limit"] = ["Must be an integer."];
        return null;
    }

    /// <summary><c>409 badge_limit</c>; the limit travels as the <c>limit</c> extension (Node: the response member <c>limit</c>).</summary>
    private static IResult LimitProblem(BadgeLimitReached reached) =>
        Results.Problem(new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Type = ProblemTypes.UrnFor("badge_limit"),
            Detail = $"At most {reached.Limit} badges can exist",
            Extensions = { ["limit"] = reached.Limit },
        });

    /// <summary>The policy guarantees an actor on every write; a missing one is a programming error and becomes a 500.</summary>
    private static async Task<Actor> ActorOf(HttpContext http) =>
        await http.GetActorAsync() ?? throw new InvalidOperationException("A badge write ran without an actor; the endpoint must require authorization.");
}
