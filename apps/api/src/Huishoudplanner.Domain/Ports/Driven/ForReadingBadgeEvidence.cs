using Huishoudplanner.Domain.Badges;
using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>
/// The audited data the badge rules count (ADR-0014): the done executions credited to a person, read from the occurrences (not from the ledger, so work
/// of 0 points counts), and the on-time week bonuses of the ledger. Reads only; they join the running transaction when there is one.
/// </summary>
public interface ForReadingBadgeEvidence
{
    /// <summary>
    /// Every done occurrence credited to somebody, or only to the given people: the person is <c>completedBy</c>, else the assignee (the credit rule
    /// of the points ledger, ADR-0011); work nobody can be credited for is left out. Each row is reduced to what a rule needs.
    /// </summary>
    Task<OneOf<IReadOnlyList<CreditedExecution>, PortError>> FindCreditedExecutionsAsync(IReadOnlyCollection<string>? personIds, CancellationToken cancellationToken);

    /// <summary>The <c>bonus_week_ontime</c> entries of the given people (of everybody when <see langword="null"/>), dated on the last day of their week.</summary>
    Task<OneOf<IReadOnlyList<OnTimeWeek>, PortError>> FindOnTimeWeeksAsync(IReadOnlyCollection<string>? personIds, CancellationToken cancellationToken);
}
