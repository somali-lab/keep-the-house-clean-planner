using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>First-run rooms: the default rooms on an empty rooms collection.</summary>
public interface IRoomSeedService
{
    /// <summary>
    /// When there are no rooms, creates Keuken, Badkamer, Toilet, Woonkamer, Slaapkamer, Hal and the virtual Hele huis
    /// (sort order 10, 20, ... 70) with an audit entry of the system actor each, all in one transaction. Otherwise does nothing.
    /// Returns the number of rooms created.
    /// </summary>
    Task<OneOf<int, ConflictError, PortError>> SeedAsync(CancellationToken cancellationToken);
}
