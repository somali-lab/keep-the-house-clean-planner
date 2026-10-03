using Huishoudplanner.Domain.CyclePlans;

namespace Huishoudplanner.Domain.Ai;

/// <summary>
/// An AI draft as the store is asked to create it: inactive, <c>draft: true</c>, <c>source: ai</c>, not discarded, with its proposal id
/// and the rationale per week. The store assigns the id and sets both timestamps to <see cref="CreatedAt"/>.
/// </summary>
public sealed record NewPlanProposal(
    string Name,
    IReadOnlyList<CyclePlanSlot> Slots,
    IReadOnlyList<string> WeekThemes,
    string ProposalId,
    IReadOnlyList<string> Rationale,
    DateTimeOffset CreatedAt);
