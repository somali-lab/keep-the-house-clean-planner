using System.ComponentModel;
using System.Globalization;
using Huishoudplanner.Domain.Activation;

namespace Huishoudplanner.Adapters.Http.CyclePlans;

/// <summary>The body of <c>POST /api/v2/cycle-plans/{id}/activation</c>; read by <see cref="ActivationRequestParser"/>, documented here for OpenAPI.</summary>
public sealed record ActivatePlanRequest(
    [property: Description("The previewToken of GET /api/v2/cycle-plans/{id}/activation-preview: 64 lowercase hexadecimal characters.")] string? PreviewToken);

/// <summary>An occurrence in an activation preview. <c>occurrenceId</c> is null for one that does not exist yet, <c>taskId</c> for a one-off task.</summary>
public sealed record ActivationPreviewItemResponse(string? OccurrenceId, int CycleIndex, string? TaskId, string TaskName, [property: Description("YYYY-MM-DD.")] string Date, string? AssigneeId)
{
    internal static ActivationPreviewItemResponse From(ActivationPreviewItem item) => new(
        item.OccurrenceId, item.CycleIndex, item.TaskId, item.TaskName, item.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), item.AssigneeId);
}

/// <summary>What an activation leaves alone: completed, skipped, manually moved and ad-hoc occurrences.</summary>
public sealed record PreservedOccurrencesResponse(
    IReadOnlyList<ActivationPreviewItemResponse> Done,
    IReadOnlyList<ActivationPreviewItemResponse> Skipped,
    IReadOnlyList<ActivationPreviewItemResponse> Moved,
    IReadOnlyList<ActivationPreviewItemResponse> Adhoc);

/// <summary>What activating a plan would change, with the opaque token of the state it was computed from.</summary>
public sealed record ActivationPreviewResponse(
    string PlanId,
    [property: Description("An opaque fingerprint of the previewed state; send it back to activate.")] string PreviewToken,
    [property: Description("Today in the household timezone, YYYY-MM-DD.")] string AsOfDate,
    IReadOnlyList<ActivationPreviewItemResponse> Removed,
    IReadOnlyList<ActivationPreviewItemResponse> Added,
    PreservedOccurrencesResponse Preserved)
{
    internal static ActivationPreviewResponse From(ActivationPreview preview) => new(
        preview.PlanId,
        preview.PreviewToken,
        preview.AsOfDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Items(preview.Removed),
        Items(preview.Added),
        new PreservedOccurrencesResponse(
            Items(preview.Preserved.Done), Items(preview.Preserved.Skipped), Items(preview.Preserved.Moved), Items(preview.Preserved.Adhoc)));

    private static List<ActivationPreviewItemResponse> Items(IEnumerable<ActivationPreviewItem> items) => [.. items.Select(ActivationPreviewItemResponse.From)];
}

/// <summary>The activated plan, the run id of its audit entries and what the replacement of the upcoming occurrences did.</summary>
public sealed record PlanActivatedResponse(
    CyclePlanResponse Plan,
    string RunId,
    [property: Description("How many open generated occurrences of the current and the next cycle were removed.")] int Removed,
    IReadOnlyList<GenerationResultResponse> Generated)
{
    internal static PlanActivatedResponse From(PlanActivated activated) => new(
        CyclePlanResponse.From(activated.Plan),
        activated.RunId,
        activated.Replacement.Removed,
        [.. activated.Replacement.Generated.Select(GenerationResultResponse.From)]);
}
