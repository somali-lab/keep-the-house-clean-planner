using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>Asks the database whether it can be reached right now.</summary>
public interface ForCheckingHealth
{
    Task<OneOf<Success, PortError>> CheckDatabaseAsync(CancellationToken cancellationToken);
}
