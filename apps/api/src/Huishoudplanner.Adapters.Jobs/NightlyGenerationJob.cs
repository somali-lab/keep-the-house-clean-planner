using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Jobs;

/// <summary>
/// The nightly run at 03:00 (requirements 4.10), attributed to the system: the generation of the current and the next cycle, followed by the
/// reconciliation of the points ledger (<see cref="INightlyService"/>, ADR-0011), which repairs drift between an occurrence and its entry within a
/// day. A reconciliation that fails is logged by the use case and never fails the run. The bonus step (slice 4.2) and the badge step (slice 4.5)
/// join the same reconciliation. The name stays <c>nightly-generation</c>, the stable tag of the run counter; the manual generation of the planners
/// is a separate trigger that never reconciles.
/// </summary>
public sealed partial class NightlyGenerationJob(ILogger<NightlyGenerationJob> logger) : IJob
{
    public const string JobName = "nightly-generation";

    public const string CronSchedule = "0 3 * * *";

    public string Name => JobName;

    public string Schedule => CronSchedule;

    public async Task<JobOutcome> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var nightly = services.GetRequiredService<INightlyService>();
        var result = await nightly.RunAsync(AuditActor.System, GenerationRunIds.New(), cancellationToken).ConfigureAwait(false);
        return result.Match(
            run =>
            {
                var inserted = run.Generation.Generated.Sum(g => g.Inserted);
                LogCompleted(logger, run.Generation.RunId, run.Generation.Removed, inserted);
                if (run.Points is { } points)
                {
                    LogReconciled(logger, run.Generation.RunId, points.Created, points.Updated, points.Removed, points.Skipped, points.CorrectionsTotal);
                }

                return JobOutcome.Succeeded;
            },
            _ =>
            {
                LogFailed(logger, "settings_missing");
                return JobOutcome.Failed;
            },
            conflict =>
            {
                LogFailed(logger, conflict.Code);
                return JobOutcome.Failed;
            },
            _ =>
            {
                // The port error text belongs to the failing adapter's own log; the job only needs to know it failed.
                LogFailed(logger, "port_error");
                return JobOutcome.Failed;
            });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Generation run {RunId} completed: removed {Removed}, inserted {Inserted}")]
    private static partial void LogCompleted(ILogger logger, string runId, int removed, int inserted);

    [LoggerMessage(Level = LogLevel.Information, Message = "Points reconciliation of run {RunId} completed: created {Created}, updated {Updated}, removed {Removed}, skipped {Skipped}, corrections {Corrections}")]
    private static partial void LogReconciled(ILogger logger, string runId, int created, int updated, int removed, int skipped, int corrections);

    [LoggerMessage(Level = LogLevel.Error, Message = "Generation run failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
