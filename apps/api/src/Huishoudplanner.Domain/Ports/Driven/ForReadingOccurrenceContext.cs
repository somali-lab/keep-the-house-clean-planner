using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

#pragma warning disable CA1715

/// <summary>Looks up the snapshot of occurrences (task name, room name, date) for the history of occurrences.</summary>
public interface ForReadingOccurrenceContext
{
    /// <summary>The context of the occurrences that exist, by id; ids that are unknown or malformed are simply absent.</summary>
    Task<OneOf<IReadOnlyDictionary<string, OccurrenceContext>, PortError>> FindAsync(IReadOnlyCollection<string> occurrenceIds, CancellationToken cancellationToken);
}
