using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// Generation of occurrences from the active plan (requirements 4.3): the use cases behind the nightly job, the manual trigger (slice 6.3),
/// the synchronisation when the slots of the active plan are saved, and later the activation (slice 2.4). Every call runs in one
/// transaction together with its audit entries, or joins the transaction of the caller, so a caller can make the replacement part of its
/// own atomic change. The <see cref="AuditActor"/> says who the entries are attributed to (the system for the nightly job, the profile that
/// started a manual run, the saving profile with the system as source for a slot save).
/// </summary>
public interface IGenerationService
{
    /// <summary>
    /// Generates the current and the next cycle in advance (the nightly job). When the replaceable future occurrences no longer match the active
    /// plan (for example after the anchor date moved) they are replaced by those of the plan instead. Existing cycles are realigned to the anchor.
    /// </summary>
    Task<OneOf<GenerationRun, SettingsMissing, ConflictError, PortError>> GenerateUpcomingAsync(AuditActor actor, string runId, CancellationToken cancellationToken);

    /// <summary>
    /// Generates one cycle from the active plan, idempotently: what is already generated is skipped and only real inserts are audited. Never
    /// creates an occurrence in the past, on a vacation day or for an inactive task. Without an active plan only the cycle document is ensured.
    /// </summary>
    Task<OneOf<GenerationResult, SettingsMissing, ConflictError, PortError>> GenerateCycleAsync(AuditActor actor, int cycleIndex, string runId, CancellationToken cancellationToken);

    /// <summary>
    /// The replacement rule of requirements 4.3 for the current and the next cycle: the open generated occurrences from today on that were not
    /// moved are removed (audited) and generated again from the plan. Done, skipped, moved and ad-hoc occurrences stay. <see cref="NotFound"/>
    /// when the plan does not exist.
    /// </summary>
    Task<OneOf<ReplacementResult, NotFound, SettingsMissing, ConflictError, PortError>> ReplaceUpcomingAsync(
        AuditActor actor, string planId, string runId, string reason, CancellationToken cancellationToken);
}
