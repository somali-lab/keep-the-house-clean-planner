using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Brings the storage to the state the application expects, once at startup and before anything is seeded: first the
/// pending migrations (they may drop an index the index step would conflict with), then the collections and indexes.
/// Idempotent; running it on a prepared database changes nothing.
/// </summary>
public interface ForPreparingStorage
{
    Task<OneOf<Success, PortError>> PrepareAsync(CancellationToken cancellationToken);
}
