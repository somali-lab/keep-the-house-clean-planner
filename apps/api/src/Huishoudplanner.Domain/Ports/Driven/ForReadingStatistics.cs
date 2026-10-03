using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Statistics;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Reads what the statistics reports are computed from (collections <c>occurrences</c>, <c>users</c>, <c>tasks</c> and <c>rooms</c>, shared with
/// the Node server), as plain records with only the fields the reports need. Reads join the running transaction when there is one.
/// </summary>
public interface ForReadingStatistics
{
    /// <summary>
    /// The occurrences of these cycles, optionally only those on a day in [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>) (both set, or neither).
    /// Bounded by the cycles asked for (at most the 26 a period may span) and projected to the fields the reports read.
    /// </summary>
    Task<OneOf<IReadOnlyList<StatisticsOccurrence>, PortError>> FindOccurrencesAsync(
        IReadOnlyCollection<string> cycleIds,
        DateTimeOffset? rangeStart,
        DateTimeOffset? rangeEnd,
        CancellationToken cancellationToken);

    /// <summary>The people of the household, oldest first (creation time, then id), active or not.</summary>
    Task<OneOf<IReadOnlyList<StatisticsPerson>, PortError>> ListPeopleAsync(CancellationToken cancellationToken);

    /// <summary>The tasks of the household, ordered by name and id, active or not.</summary>
    Task<OneOf<IReadOnlyList<StatisticsTask>, PortError>> ListTasksAsync(CancellationToken cancellationToken);

    /// <summary>The rooms of the house.</summary>
    Task<OneOf<IReadOnlyList<StatisticsRoom>, PortError>> ListRoomsAsync(CancellationToken cancellationToken);
}
