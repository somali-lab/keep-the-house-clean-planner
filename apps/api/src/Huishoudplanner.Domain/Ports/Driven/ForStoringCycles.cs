using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Stores the generated cycles (collection <c>cycles</c>, shared with the Node server; one document per index, a unique index backs it).
/// The writes only run inside <see cref="ForRunningTransactions"/> (together with their audit entry); called outside a transaction they
/// write nothing and return a <see cref="PortError"/> whose message starts with <c>cycles.no_transaction</c>. Reads join the running
/// transaction when there is one.
/// </summary>
public interface ForStoringCycles
{
    /// <summary>At most <paramref name="take"/> cycles after the cursor, in index order (negative indexes first).</summary>
    Task<OneOf<IReadOnlyList<Cycle>, PortError>> ListAsync(CycleCursor? after, int take, CancellationToken cancellationToken);

    Task<OneOf<Cycle, NotFound, PortError>> FindByIndexAsync(int index, CancellationToken cancellationToken);

    Task<OneOf<Cycle, PortError>> InsertAsync(NewCycle cycle, CancellationToken cancellationToken);

    /// <summary>Sets <c>startDate</c>, <c>endDate</c>, <c>generatedAt</c> and <c>generationRunId</c>; returns the cycle as stored afterwards.</summary>
    Task<OneOf<Cycle, NotFound, PortError>> UpdateBoundsAsync(string id, DateOnly startDate, DateOnly endDate, DateTimeOffset generatedAt, string runId, CancellationToken cancellationToken);

    /// <summary>Sets <c>planId</c>, <c>generatedAt</c> and <c>generationRunId</c>; returns the cycle as stored afterwards.</summary>
    Task<OneOf<Cycle, NotFound, PortError>> UpdatePlanAsync(string id, string planId, DateTimeOffset generatedAt, string runId, CancellationToken cancellationToken);
}
