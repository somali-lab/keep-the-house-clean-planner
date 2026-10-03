using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Identity;

/// <summary>How the resolved actor travels through a request. Endpoint code reads <see cref="GetActorAsync"/>, never a header.</summary>
public static class ActorContext
{
    internal const string ProfileHeader = "X-Profile-Id";
    internal const string ClientHeader = "X-Client";

    private const string ResolutionItemKey = "huishoudplanner.actor-resolution";

    /// <summary>
    /// The actor of this request, or <c>null</c> when the request names no active profile. Resolved on first use and cached
    /// for the request; a failing user lookup throws (a 500), so call it only where an actor matters.
    /// </summary>
    public static async Task<Actor?> GetActorAsync(this HttpContext context)
    {
        var result = await ResolveAsync(context).ConfigureAwait(false);
        return result.Match<Actor?>(actor => actor, _ => null, error => throw FailedLookup(error));
    }

    /// <summary>Resolves once per request: the header values go to the port, the outcome is cached in the request items.</summary>
    internal static Task<OneOf<Actor, ProfileRequired, PortError>> ResolveAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.TryGetValue(ResolutionItemKey, out var cached) && cached is Task<OneOf<Actor, ProfileRequired, PortError>> task)
        {
            return task;
        }

        var resolver = context.RequestServices.GetRequiredService<ForResolvingActors>();
        var request = new ActorRequest(HeaderValue(context, ProfileHeader), HeaderValue(context, ClientHeader));
        var resolution = resolver.ResolveAsync(request, context.RequestAborted);
        context.Items[ResolutionItemKey] = resolution;
        return resolution;
    }

    // The port error text is for logs only: the exception handler logs it and answers a bare 500.
    internal static InvalidOperationException FailedLookup(PortError error) =>
        new($"The actor could not be resolved: {error.Message}");

    private static string? HeaderValue(HttpContext context, string name) =>
        context.Request.Headers.TryGetValue(name, out var values) && values.Count > 0 ? values.ToString() : null;
}
