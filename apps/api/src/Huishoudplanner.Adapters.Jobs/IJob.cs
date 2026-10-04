namespace Huishoudplanner.Adapters.Jobs;

/// <summary>
/// One scheduled job. Adding a job is one class that implements this and one registration
/// (<see cref="JobsRegistration.AddJob{TJob}"/>). The job calls driving ports only, as the system actor.
/// </summary>
public interface IJob
{
    /// <summary>Stable name, used as span name, metric tag and log field (for example <c>nightly-generation</c>).</summary>
    string Name { get; }

    /// <summary>A five-field cron expression, evaluated in the household timezone.</summary>
    string Schedule { get; }

    /// <summary>
    /// False when the job must not be scheduled at all in this installation (the morning message without a notification channel). Evaluated once,
    /// when the scheduler is built. Most jobs are always scheduled and end as <see cref="JobOutcome.Skipped"/> instead.
    /// </summary>
    bool Enabled => true;

    /// <summary>
    /// Runs the job once. <paramref name="services"/> is a fresh scope per run. Report a failure by returning
    /// <see cref="JobOutcome.Failed"/> (and logging it without values); an exception that escapes is treated the same way.
    /// </summary>
    Task<JobOutcome> RunAsync(IServiceProvider services, CancellationToken cancellationToken);
}
