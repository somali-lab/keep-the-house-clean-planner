using Huishoudplanner.Domain.Ai;
using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// Stores the cycle plans (collection <c>cyclePlans</c>, shared with the Node server). The writes only run inside
/// <see cref="ForRunningTransactions"/> (together with their audit entry); called outside a transaction they write nothing and return a
/// <see cref="PortError"/> whose message starts with <c>cycle_plans.no_transaction</c>. Reads join the running transaction when there is one.
/// The order of plans is oldest first (<c>createdAt</c>, then id): the first plan is the default plan.
/// </summary>
public interface ForStoringCyclePlans
{
    /// <summary>At most <paramref name="take"/> plans after the cursor, oldest first.</summary>
    Task<OneOf<IReadOnlyList<CyclePlan>, PortError>> ListAsync(CyclePlanCursor? after, int take, CancellationToken cancellationToken);

    /// <summary><see cref="NotFound"/> also for an id that is not a valid id.</summary>
    Task<OneOf<CyclePlan, NotFound, PortError>> FindAsync(string id, CancellationToken cancellationToken);

    /// <summary>The active plan, or <see cref="NotFound"/> when none is active.</summary>
    Task<OneOf<CyclePlan, NotFound, PortError>> FindActiveAsync(CancellationToken cancellationToken);

    /// <summary>The oldest plan (the default plan), or <see cref="NotFound"/> when there are no plans.</summary>
    Task<OneOf<CyclePlan, NotFound, PortError>> FindDefaultAsync(CancellationToken cancellationToken);

    /// <summary>Every plan that holds at least one slot of the task, oldest first (a task that is deleted leaves them all).</summary>
    Task<OneOf<IReadOnlyList<CyclePlan>, PortError>> ListHoldingTaskAsync(string taskId, CancellationToken cancellationToken);

    Task<OneOf<long, PortError>> CountAsync(CancellationToken cancellationToken);

    /// <summary>Stores a new plan (manual, not a draft, no proposal); the store assigns the id and sets both timestamps to the <c>CreatedAt</c> of the new plan.</summary>
    Task<OneOf<CyclePlan, PortError>> InsertAsync(NewCyclePlan plan, CancellationToken cancellationToken);

    /// <summary>Stores an AI draft (inactive, <c>draft: true</c>, <c>source: ai</c>, with proposal id and rationale); the store assigns the id and sets both timestamps to the <c>CreatedAt</c> of the proposal.</summary>
    Task<OneOf<CyclePlan, PortError>> InsertProposalAsync(NewPlanProposal proposal, CancellationToken cancellationToken);

    /// <summary>
    /// Sets the given fields and <c>updatedAt</c>, raises the version by one in the same write, and returns the plan as stored afterwards. With an
    /// <paramref name="expectedVersion"/> the write is conditional on the stored version (<see cref="PreconditionFailed"/> when it differs, nothing written).
    /// </summary>
    Task<OneOf<CyclePlan, NotFound, PortError, PreconditionFailed>> UpdateMetaAsync(
        string id, PlanMetaChanges changes, DateTimeOffset updatedAt, CancellationToken cancellationToken, int? expectedVersion = null);

    /// <summary>
    /// Replaces all slots (as given, in order), sets <c>updatedAt</c> and raises the version by one; returns the plan as stored afterwards. Conditional on
    /// <paramref name="expectedVersion"/> like <see cref="UpdateMetaAsync"/>.
    /// </summary>
    Task<OneOf<CyclePlan, NotFound, PortError, PreconditionFailed>> ReplaceSlotsAsync(
        string id, IReadOnlyList<CyclePlanSlot> slots, DateTimeOffset updatedAt, CancellationToken cancellationToken, int? expectedVersion = null);

    /// <summary>Deletes the plan; with an <paramref name="expectedVersion"/> the delete is conditional on the stored version (<see cref="PreconditionFailed"/> when it differs).</summary>
    Task<OneOf<Success, NotFound, PortError, PreconditionFailed>> DeleteAsync(string id, CancellationToken cancellationToken, int? expectedVersion = null);
}
