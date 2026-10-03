using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Occurrences;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// The plan states an activation changed: the plan before and after, and the ids of the plans it deactivated (there is normally one).
/// </summary>
public sealed record PlanActivation(CyclePlan Before, CyclePlan After, IReadOnlyList<string> DeactivatedIds);

/// <summary>
/// The writes of a plan activation (collections <c>cyclePlans</c> and <c>settings</c>, shared with the Node server). They only run inside
/// <see cref="ForRunningTransactions"/> together with their audit entries; called outside a transaction they write nothing and return a
/// <see cref="PortError"/> whose message starts with <c>cycle_plans.no_transaction</c>.
/// </summary>
public interface ForActivatingCyclePlans
{
    /// <summary>
    /// Writes the shared guard document (ADR-0008 as amended, ADR-0021): snapshot isolation lets two transactions that read the same state and
    /// write different documents both commit, so every activation first writes this one document, and of two concurrent activations one hits a
    /// write conflict. <see cref="SettingsMissing"/> when the installation has no settings document (the guard lives on it).
    /// </summary>
    Task<OneOf<Success, SettingsMissing, PortError>> TouchGuardAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Makes the plan the only active one: every other active plan is deactivated and the plan is activated; an AI draft stops being a
    /// draft (<c>draft: false</c>). <see cref="NotFound"/> for an unknown plan.
    /// </summary>
    Task<OneOf<PlanActivation, NotFound, PortError>> ActivateAsync(string planId, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>
/// What the activation preview reads from the occurrences: everything on a day (or planned for a day) in a window, plus everything that belongs
/// to the given cycles. Reads join the running transaction when there is one.
/// </summary>
public interface ForReadingOccurrencesForActivation
{
    /// <summary>
    /// The occurrences whose <c>date</c> or <c>plannedDate</c> lies in [<paramref name="rangeStart"/>, <paramref name="rangeEnd"/>) or that belong to one of
    /// <paramref name="cycleIds"/>, in no particular order.
    /// </summary>
    Task<OneOf<IReadOnlyList<Occurrence>, PortError>> FindForActivationAsync(
        DateTimeOffset rangeStart, DateTimeOffset rangeEnd, IReadOnlyCollection<string> cycleIds, CancellationToken cancellationToken);
}
