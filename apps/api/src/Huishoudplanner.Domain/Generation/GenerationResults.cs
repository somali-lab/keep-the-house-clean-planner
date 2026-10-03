namespace Huishoudplanner.Domain.Generation;

/// <summary>
/// What generating one cycle did: how many occurrences were inserted and how many were already there (<see cref="Skipped"/>, the
/// idempotency of the unique slot index). <see cref="PlanId"/> is <see langword="null"/> when no plan is active, in which case nothing is generated.
/// </summary>
public sealed record GenerationResult(int CycleIndex, string CycleId, string? PlanId, int Inserted, int Skipped);

/// <summary>
/// The outcome of replacing the upcoming occurrences of the current and the next cycle: how many open generated occurrences were removed
/// and what was generated again from the plan. This is also the <c>synchronized</c> member of a slot save of the active plan.
/// </summary>
public sealed record ReplacementResult(int Removed, IReadOnlyList<GenerationResult> Generated);

/// <summary>One run of <c>GenerateUpcoming</c> (the nightly job and the manual trigger): its id, and what it removed and generated.</summary>
public sealed record GenerationRun(string RunId, int Removed, IReadOnlyList<GenerationResult> Generated);

/// <summary>The reasons the audit entries of a replacement carry in <c>meta.reason</c>.</summary>
public static class ReplacementReasons
{
    public const string PlanActivation = "plan_activation";

    public const string PlanUpdate = "plan_update";

    public const string NightlyReconciliation = "nightly_reconciliation";
}

/// <summary>The id of one generation run, recorded in <c>cycles.generationRunId</c> and in <c>meta.runId</c> of the audit entries of that run.</summary>
public static class GenerationRunIds
{
    /// <summary>A fresh run id (the Node server uses a random UUID). It names a run, not an entity: entity ids stay ObjectIds.</summary>
    public static string New() => Guid.NewGuid().ToString("D");
}
