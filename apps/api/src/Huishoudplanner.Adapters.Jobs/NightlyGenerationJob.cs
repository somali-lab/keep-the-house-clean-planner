using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Jobs;

/// <summary>
/// The nightly generation at 03:00 (requirements 4.10): the current and the next cycle, attributed to the system. The Node job continues with the
/// reconciliation of the points ledger, the bonuses and the badge awards; that step joins this job with the points slices (6.3b).
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
        var generation = services.GetRequiredService<IGenerationService>();
        var result = await generation.GenerateUpcomingAsync(AuditActor.System, GenerationRunIds.New(), cancellationToken).ConfigureAwait(false);
        return result.Match(
            run =>
            {
                var inserted = run.Generated.Sum(g => g.Inserted);
                LogCompleted(logger, run.RunId, run.Removed, inserted);
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Generation run failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason);
}
