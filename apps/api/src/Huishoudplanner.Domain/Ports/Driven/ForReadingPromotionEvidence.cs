using Huishoudplanner.Domain.Errors;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driven;

// Driven ports are named ForXxx by the architecture rules, not IXxx.
#pragma warning disable CA1715

/// <summary>A generated occurrence of a plan reduced to what the promote rule reads: its task, planned and current day instants and assignee.</summary>
public sealed record PromotionOccurrence(string Id, string TaskId, DateTimeOffset PlannedDate, DateTimeOffset Date, string? AssigneeId);

/// <summary>
/// The one question the promote suggestions ask of the occurrences: where the generated occurrences of a plan were planned and where they sit now.
/// Read only; occurrences of one-off tasks (no task id) never appear.
/// </summary>
public interface ForReadingPromotionEvidence
{
    Task<OneOf<IReadOnlyList<PromotionOccurrence>, PortError>> FindGeneratedOfPlanAsync(string planId, CancellationToken cancellationToken);
}
