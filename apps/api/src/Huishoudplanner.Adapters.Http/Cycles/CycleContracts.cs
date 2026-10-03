using System.ComponentModel;
using System.Globalization;
using Huishoudplanner.Domain.Generation;

namespace Huishoudplanner.Adapters.Http.Cycles;

/// <summary>A generated cycle: 28 days from a Monday. A day in a cycle that has no entry here is not generated yet.</summary>
public sealed record CycleResponse(
    string Id,
    [property: Description("0 is the cycle that starts on the anchor date; negative before it.")] int Index,
    [property: Description("First day (a Monday), YYYY-MM-DD.")] string StartDate,
    [property: Description("Last day (a Sunday), YYYY-MM-DD.")] string EndDate,
    [property: Description("The plan the cycle was generated from; null when no plan was active.")] string? PlanId,
    DateTimeOffset GeneratedAt,
    string GenerationRunId)
{
    internal static CycleResponse From(Cycle cycle) => new(
        cycle.Id,
        cycle.Index,
        cycle.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        cycle.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        cycle.PlanId,
        cycle.GeneratedAt,
        cycle.GenerationRunId);
}

/// <summary>One page of cycles in index order; <see cref="NextCursor"/> is <c>null</c> on the last page.</summary>
public sealed record CycleListResponse(IReadOnlyList<CycleResponse> Items, string? NextCursor);
