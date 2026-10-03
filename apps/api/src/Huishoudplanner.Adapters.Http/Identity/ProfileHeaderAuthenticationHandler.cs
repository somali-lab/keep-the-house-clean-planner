using System.Text.Encodings.Web;
using Huishoudplanner.Adapters.Http.Problems;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Huishoudplanner.Adapters.Http.Identity;

/// <summary>
/// The authentication scheme the policies run under. It never authenticates by itself: the profile header is a claim,
/// not a login, and ASP.NET Core runs the default scheme in a middleware that sits before the exception handler, where a
/// failing user lookup could not become a Problem Details 500 and where every endpoint (health, static files) would pay for
/// a lookup. The actor is therefore resolved lazily, once per request, by the authorization requirement of an endpoint
/// that has a policy or by <see cref="ActorContext.GetActorAsync"/>. What remains here is the response of a direct
/// ChallengeAsync or ForbidAsync call, so no path answers with an empty 401 or 403.
/// </summary>
internal sealed class ProfileHeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ProfileHeader";
    public const string ProfileRequiredDetail = "An active profile is required (X-Profile-Id).";
    public const string RoleTooLowDetail = "The role is too low.";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        ProblemResults.Problem(StatusCodes.Status400BadRequest, ProblemTypes.ProfileRequired, ProfileRequiredDetail).ExecuteAsync(Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ProblemResults.Problem(StatusCodes.Status403Forbidden, ProblemTypes.PermissionDenied, RoleTooLowDetail).ExecuteAsync(Context);
}
