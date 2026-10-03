using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Identity;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Turns what a request carries into the actor behind it (ADR-0018). The input is driver-free, so a header adapter
/// and a later bearer-token adapter are interchangeable. <see cref="ProfileRequired"/> is a normal outcome (a read
/// without a profile), not a failure.
/// </summary>
public interface ForResolvingActors
{
    Task<OneOf<Actor, ProfileRequired, PortError>> ResolveAsync(ActorRequest request, CancellationToken cancellationToken);
}
