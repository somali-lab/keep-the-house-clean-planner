using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Points;
using OneOf;

namespace Huishoudplanner.Domain.Ports.Driving;

/// <summary>
/// The scheduled nightly run at 03:00 (requirements 4.10): the generation of the current and the next cycle, followed by the reconciliation of
/// the points ledger, which repairs drift between an occurrence and its entry within a day (ADR-0011). Only the scheduler (slice 6.3) calls it, with
/// the system as actor. The manual generation of the planners never reconciles, so it does not come through here.
/// </summary>
public interface INightlyService
{
    /// <summary>
    /// Generates, then reconciles with the trigger <see cref="PointsRecomputeTrigger.Nightly"/>. A failing reconciliation is logged and never fails
    /// the run (<c>reconcilePointsSafely</c>): <see cref="NightlyRun.Points"/> is then <see langword="null"/>.
    /// </summary>
    Task<OneOf<NightlyRun, SettingsMissing, ConflictError, PortError>> RunAsync(AuditActor actor, string runId, CancellationToken cancellationToken);
}

/// <summary>What the nightly run did: the generation, and the reconciliation unless it failed.</summary>
public sealed record NightlyRun(GenerationRun Generation, PointsRecomputeResult? Points);
