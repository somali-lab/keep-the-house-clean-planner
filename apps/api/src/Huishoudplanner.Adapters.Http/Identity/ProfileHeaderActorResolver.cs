using System.Text.RegularExpressions;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using Huishoudplanner.Domain.Ports.Driven;
using OneOf;

namespace Huishoudplanner.Adapters.Http.Identity;

/// <summary>
/// The first identity adapter (ADR-0003, ADR-0018), the .NET twin of <c>apps/server/src/identity/index.ts</c>: the profile
/// header is a claim, not authentication. A well-formed id of an active user is the actor; anything else is "no actor",
/// which only matters to endpoints that need one. <c>X-Client: web</c> (exact) marks the change as made in the interface.
/// </summary>
public sealed partial class ProfileHeaderActorResolver(ForFindingUsers users) : ForResolvingActors
{
    public const string WebClient = "web";

    // \A and \z, not ^ and $: .NET lets $ match before a trailing newline.
    [GeneratedRegex(@"\A[0-9a-fA-F]{24}\z")]
    private static partial Regex ObjectIdPattern();

    public async Task<OneOf<Actor, ProfileRequired, PortError>> ResolveAsync(ActorRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProfileId is not { } profileId || !ObjectIdPattern().IsMatch(profileId))
        {
            return new ProfileRequired();
        }

        var found = await users.FindAsync(profileId, cancellationToken).ConfigureAwait(false);
        return found.Match<OneOf<Actor, ProfileRequired, PortError>>(
            user => user.Active
                ? new Actor(user.Id, user.Role, request.Client == WebClient ? ActorSource.Ui : ActorSource.Api)
                : new ProfileRequired(),
            _ => new ProfileRequired(),
            error => error);
    }
}
