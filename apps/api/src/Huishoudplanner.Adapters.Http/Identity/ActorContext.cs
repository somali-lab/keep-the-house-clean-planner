using System.Security.Claims;
using Huishoudplanner.Domain.Identity;

namespace Huishoudplanner.Adapters.Http.Identity;

/// <summary>How the resolved actor travels through a request. Endpoint code reads <see cref="GetActor"/>, never a header.</summary>
public static class ActorContext
{
    internal const string ActorItemKey = "huishoudplanner.actor";
    internal const string RoleClaim = "huishoudplanner/role";

    /// <summary>The actor of this request, or <c>null</c> when the request names no active profile.</summary>
    public static Actor? GetActor(this HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(ActorItemKey, out var actor) ? actor as Actor : null;
    }

    internal static ClaimsPrincipal ToPrincipal(Actor actor, string scheme) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, actor.ActorId),
                new Claim(RoleClaim, actor.Role.ToString()),
            ],
            scheme));

    internal static Role? RoleOf(ClaimsPrincipal user) =>
        Enum.TryParse<Role>(user.FindFirstValue(RoleClaim), out var role) && Enum.IsDefined(role) ? role : null;
}
