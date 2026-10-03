using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Jobs;

/// <summary>
/// Audit retention at 03:45 (requirements 4.9, 4.10). The Node scheduler only schedules it with <c>AUDIT_RETENTION_DAYS</c>; here the job is
/// always registered and ends as <see cref="JobOutcome.Skipped"/> without retention, which has the same effect (nothing is deleted) and
/// keeps the registration independent of configuration.
/// </summary>
public sealed partial class AuditRetentionJob(ILogger<AuditRetentionJob> logger) : IJob
{
    public const string JobName = "audit-retention";

    public const string CronSchedule = "45 3 * * *";

    public string Name => JobName;

    public string Schedule => CronSchedule;

    public async Task<JobOutcome> RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        var retention = services.GetRequiredService<IAuditRetentionService>();
        var result = await retention.RunAsync(cancellationToken).ConfigureAwait(false);
        return result.Match(
            _ => JobOutcome.Skipped,
            done =>
            {
                LogCompleted(logger, done.Deleted);
                return JobOutcome.Succeeded;
            },
            _ =>
            {
                LogFailed(logger);
                return JobOutcome.Failed;
            });
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audit retention completed: deleted {Deleted}")]
    private static partial void LogCompleted(ILogger logger, int deleted);

    [LoggerMessage(Level = LogLevel.Error, Message = "Audit retention failed")]
    private static partial void LogFailed(ILogger logger);
}
