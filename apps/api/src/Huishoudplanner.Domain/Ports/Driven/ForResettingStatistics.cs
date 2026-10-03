using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Statistics;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Applies a statistics reset to the stored history: occurrences, the tasks' last completion, the cycles, the points ledger and the bonus floor of
/// the settings. It only runs inside <see cref="ForRunningTransactions"/> (together with the reset audit entry); called outside a transaction it
/// writes nothing and returns a <see cref="PortError"/> whose message starts with <c>statistics.no_transaction</c>. People, rooms, tasks and
/// cycle plans are never deleted.
/// </summary>
public interface ForResettingStatistics
{
    Task<OneOf<StatisticsResetResult, PortError>> ResetAsync(StatisticsResetPlan plan, CancellationToken cancellationToken);
}
