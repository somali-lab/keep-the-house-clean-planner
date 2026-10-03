using Huishoudplanner.Domain.CyclePlans;
using Huishoudplanner.Domain.Generation;

namespace Huishoudplanner.Domain.Activation;

/// <summary>
/// One occurrence in an activation preview. <see cref="OccurrenceId"/> is <see langword="null"/> for an occurrence that does not exist yet,
/// <see cref="TaskId"/> for a one-off task (ADR-0009), which has no task record.
/// </summary>
public sealed record ActivationPreviewItem(string? OccurrenceId, int CycleIndex, string? TaskId, string TaskName, DateOnly Date, string? AssigneeId);

/// <summary>What an activation leaves alone, grouped by why: completed, skipped, moved by hand and ad-hoc occurrences.</summary>
public sealed record PreservedOccurrences(
    IReadOnlyList<ActivationPreviewItem> Done,
    IReadOnlyList<ActivationPreviewItem> Skipped,
    IReadOnlyList<ActivationPreviewItem> Moved,
    IReadOnlyList<ActivationPreviewItem> Adhoc);

/// <summary>
/// What activating a plan would change (requirements 4.3): the open generated occurrences it removes, the occurrences it adds, what stays,
/// and the opaque <see cref="PreviewToken"/> that fingerprints the state this was computed from. Read-only: previewing writes nothing.
/// </summary>
public sealed record ActivationPreview(
    string PlanId,
    string PreviewToken,
    DateOnly AsOfDate,
    IReadOnlyList<ActivationPreviewItem> Removed,
    IReadOnlyList<ActivationPreviewItem> Added,
    PreservedOccurrences Preserved);

/// <summary>The outcome of an activation: the plan as stored, the run id of its audit entries and what the replacement of the upcoming occurrences did.</summary>
public sealed record PlanActivated(CyclePlan Plan, string RunId, ReplacementResult Replacement);

/// <summary>The conflict code of a confirmation whose preview is no longer current (requirements section 8).</summary>
public static class ActivationCodes
{
    public const string StalePreview = "stale_activation_preview";

    public const string StalePreviewDetail = "Activation preview is no longer current";
}
