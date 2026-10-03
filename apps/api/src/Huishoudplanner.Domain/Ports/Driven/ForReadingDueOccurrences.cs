using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>The earliest open occurrence of a task from a given instant on.</summary>
public sealed record UpcomingOccurrence(string Id, DateTimeOffset Date, string? AssigneeId);

/// <summary>
/// The two questions the due list asks of the occurrences: when a task was first planned in a generated cycle (its initial due date)
/// and which open occurrence is next. Read only, bounded by the tasks asked for.
/// </summary>
public interface ForReadingDueOccurrences
{
    /// <summary>The earliest planned instant of any generated occurrence, per task id; a task that was never planned is absent.</summary>
    Task<OneOf<IReadOnlyDictionary<string, DateTimeOffset>, PortError>> FindFirstGeneratedPlannedDatesAsync(IReadOnlyCollection<string> taskIds, CancellationToken cancellationToken);

    /// <summary>
    /// Per task id the first open occurrence dated at or after <paramref name="from"/> (by date, task name, id); a task without one is absent.
    /// An occurrence of a one-off task (no task id) never appears.
    /// </summary>
    Task<OneOf<IReadOnlyDictionary<string, UpcomingOccurrence>, PortError>> FindNextOpenAsync(DateTimeOffset from, IReadOnlyCollection<string> taskIds, CancellationToken cancellationToken);
}
