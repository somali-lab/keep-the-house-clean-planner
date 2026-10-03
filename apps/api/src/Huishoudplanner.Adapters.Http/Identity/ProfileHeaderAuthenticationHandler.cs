using System.Text.Encodings.Web;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Huishoudplanner.Adapters.Http.Identity;

/// <summary>
/// Resolves the actor once per request (ASP.NET Core caches the result per scheme) and exposes it as the principal and
/// through <see cref="ActorContext.GetActor"/>. No profile is not a failure: the request stays anonymous and the
/// policies decide whether that is acceptable. The header values are never logged.
/// </summary>
internal sealed class ProfileHeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ForResolvingActors resolver)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ProfileHeader";
    public const string ProfileHeader = "X-Profile-Id";
    public const string ClientHeader = "X-Client";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var request = new ActorRequest(HeaderValue(ProfileHeader), HeaderValue(ClientHeader));
        var result = await resolver.ResolveAsync(request, Context.RequestAborted).ConfigureAwait(false);
        return result.Match(
            actor =>
            {
                Context.Items[ActorContext.ActorItemKey] = actor;
                var principal = ActorContext.ToPrincipal(actor, Scheme.Name);
                return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
            },
            _ => AuthenticateResult.NoResult(),
            // The port error text is for logs only: the exception handler logs it and answers a bare 500.
            error => throw new InvalidOperationException($"The actor could not be resolved: {error.Message}"));
    }

    private string? HeaderValue(string name) =>
        Request.Headers.TryGetValue(name, out var values) && values.Count > 0 ? values.ToString() : null;
}
