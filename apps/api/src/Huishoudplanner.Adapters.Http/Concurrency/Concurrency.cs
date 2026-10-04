using System.Globalization;
using Huishoudplanner.Adapters.Http.Problems;
using Huishoudplanner.Domain.Errors;
using Microsoft.Extensions.Primitives;

namespace Huishoudplanner.Adapters.Http.Concurrency;

/// <summary>
/// Optimistic concurrency over HTTP (ADR-0022), built once for every entity write. An entity carries an integer <c>version</c>; its ETag is the
/// strong validator <c>"&lt;version&gt;"</c>. A write on an entity (PATCH, PUT, DELETE) must send the ETag it read as <c>If-Match</c>:
/// missing is <c>428 precondition_required</c>, malformed <c>400 validation_error</c> on <c>If-Match</c> (a weak validator, a list and the
/// wildcard <c>*</c> are not accepted), and a version that is no longer the stored one <c>412 precondition_failed</c> with the current ETag.
/// The endpoint states the requirement with <see cref="IfMatchEndpointExtensions.RequireIfMatch{TBuilder}"/>; the comparison itself is made
/// by the use case inside its transaction, as a conditional write.
/// </summary>
public static class ETags
{
    /// <summary>The header name of the precondition.</summary>
    public const string IfMatchHeader = "If-Match";

    /// <summary>The strong validator of a version: the number in double quotes.</summary>
    public static string Format(int version) => "\"" + version.ToString(CultureInfo.InvariantCulture) + "\"";

    /// <summary>Sets the <c>ETag</c> response header to the validator of <paramref name="version"/>.</summary>
    public static void Set(HttpResponse response, int version)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ETag = Format(version);
    }

    /// <summary>
    /// Reads the version out of the <c>If-Match</c> values. <see langword="false"/> with <paramref name="problem"/> set: the header is missing
    /// (<see cref="PreconditionRequired"/>) or malformed (<see cref="ValidationErrors"/>).
    /// </summary>
    internal static bool TryParse(StringValues values, out int version, out IResult? problem)
    {
        version = 0;
        problem = null;
        if (values.Count == 0)
        {
            problem = ProblemResults.PreconditionRequired();
            return false;
        }

        if (values.Count > 1 || values[0] is not { } raw || raw.Contains(',', StringComparison.Ordinal))
        {
            problem = Malformed("Send exactly one entity tag.");
            return false;
        }

        var value = raw.Trim();
        if (value == "*")
        {
            problem = Malformed("The wildcard is not accepted: send the ETag of the version you read.");
            return false;
        }

        if (value.StartsWith("W/", StringComparison.Ordinal))
        {
            problem = Malformed("A weak entity tag is not accepted: send the strong ETag you read.");
            return false;
        }

        if (value.Length < 3 || value[0] != '"' || value[^1] != '"' ||
            !int.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out version))
        {
            problem = Malformed("Send the ETag as you read it, a version number in double quotes.");
            return false;
        }

        return true;

        static IResult Malformed(string message) => ProblemResults.From(ValidationErrors.For(IfMatchHeader, message));
    }
}

/// <summary>Marks an operation whose request must carry <c>If-Match</c>; the OpenAPI transformer documents the header from it, the audit test reads it.</summary>
public sealed record IfMatchRequiredMetadata;

/// <summary>Marks an operation that answers its success with an <c>ETag</c> header (an entity read or write).</summary>
public sealed record ReturnsETagMetadata;

/// <summary>
/// Marks a write the concurrency audit knowingly leaves without <c>If-Match</c>: an intent endpoint, a bulk reset or an event that is not edited.
/// <see cref="Reason"/> is the reason in one sentence; the audit test lists them so that an exemption is a visible decision.
/// </summary>
public sealed record NoIfMatchMetadata(string Reason);

public static class IfMatchEndpointExtensions
{
    private const string ItemKey = "Huishoudplanner.IfMatch";

    /// <summary>
    /// Requires <c>If-Match</c> on the endpoint: the filter answers 428 or 400 before the handler runs, the handler reads the version with
    /// <see cref="GetIfMatch"/>. Documents the header and the 412 and 428 answers in OpenAPI. Put it after the authorization policy so that
    /// the checks happen in that order (an unauthorised caller never learns whether the header was right).
    /// </summary>
    public static TBuilder RequireIfMatch<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.WithMetadata(new IfMatchRequiredMetadata());
        builder.AddEndpointFilter(async (context, next) =>
        {
            if (!ETags.TryParse(context.HttpContext.Request.Headers.IfMatch, out var version, out var problem))
            {
                return problem;
            }

            context.HttpContext.Items[ItemKey] = version;
            return await next(context);
        });
        return builder.ProducesProblem(StatusCodes.Status412PreconditionFailed).ProducesProblem(StatusCodes.Status428PreconditionRequired);
    }

    /// <summary>Documents that the success answer carries an <c>ETag</c> header with the version of the entity.</summary>
    public static TBuilder ReturnsETag<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new ReturnsETagMetadata());
    }

    /// <summary>States that this write needs no <c>If-Match</c> and why (see <see cref="NoIfMatchMetadata"/>).</summary>
    public static TBuilder WithoutIfMatch<TBuilder>(this TBuilder builder, string reason)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return builder.WithMetadata(new NoIfMatchMetadata(reason));
    }

    /// <summary>The version the request sent as <c>If-Match</c>; only valid in a handler of an endpoint that called <see cref="RequireIfMatch{TBuilder}"/>.</summary>
    public static int GetIfMatch(this HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return http.Items.TryGetValue(ItemKey, out var value) && value is int version
            ? version
            : throw new InvalidOperationException("The endpoint reads If-Match but did not call RequireIfMatch().");
    }
}
