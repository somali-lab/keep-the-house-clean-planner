using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Points;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// What the reconciliation of the points ledger reads and migrates outside the ledger itself (ADR-0011): the done occurrences, the task values and
/// the two fields an installation from before points has to gain (<c>tasks.points</c> and <c>occurrences.pointsSnapshot</c>). The writes only run
/// inside <see cref="ForRunningTransactions"/>, and they are not audited one by one: the reconciliation records one summary. Both writes filter on
/// the missing field, so a second run matches nothing.
/// </summary>
public interface ForBackfillingPoints
{
    /// <summary>Every done occurrence, reduced to what the ledger needs; a row whose date cannot be read has no date.</summary>
    Task<OneOf<IReadOnlyList<ExecutionSource>, PortError>> FindDoneOccurrencesAsync(CancellationToken cancellationToken);

    /// <summary>Every occurrence, whatever its status, reduced to the eight fields the bonus rules need (ADR-0012); a row that cannot be read is returned as such.</summary>
    Task<OneOf<IReadOnlyList<BonusSource>, PortError>> FindBonusOccurrencesAsync(CancellationToken cancellationToken);

    /// <summary>The points and duration of every task, for the snapshot backfill.</summary>
    Task<OneOf<IReadOnlyList<TaskPointValue>, PortError>> FindTaskPointValuesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gives every task without points the default for its duration. Returns the ids of the tasks that were without points and how many were
    /// written (a task that gained points meanwhile is left alone).
    /// </summary>
    Task<OneOf<DefaultedTasks, PortError>> DefaultMissingTaskPointsAsync(CancellationToken cancellationToken);

    /// <summary>Writes the snapshots onto done occurrences that still have none; returns how many were written.</summary>
    Task<OneOf<int, PortError>> SetMissingSnapshotsAsync(IReadOnlyList<SnapshotWrite> snapshots, CancellationToken cancellationToken);
}

/// <summary>The tasks <see cref="ForBackfillingPoints.DefaultMissingTaskPointsAsync"/> found without points, and how many it wrote.</summary>
public sealed record DefaultedTasks(IReadOnlyList<string> TaskIds, int Count);
