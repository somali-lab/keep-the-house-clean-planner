using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Rewards;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// The work planned in a period, for the automatic goal of the reward meter (requirements 4.12). A pure read of <c>occurrences</c> and <c>tasks</c>:
/// it writes and audits nothing.
/// </summary>
public interface ForReadingPlannedWork
{
    /// <summary>
    /// The occurrences planned for a day in [<paramref name="from"/>, <paramref name="toExclusive"/>) (by <c>plannedDate</c>, which survives a reschedule),
    /// everything that was not recorded as done. The task of work without a points snapshot is read along, once per task.
    /// </summary>
    Task<OneOf<IReadOnlyList<PlannedWork>, PortError>> FindPlannedAsync(DateTimeOffset from, DateTimeOffset toExclusive, CancellationToken cancellationToken);
}
