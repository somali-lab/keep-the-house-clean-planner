using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Adapters.Jobs;

/// <summary>
/// Runs one trigger of a job: noOverlap through a <see cref="SemaphoreSlim"/> per job (a trigger that finds the previous run
/// still going does not start, like <c>noOverlap</c> of the Node scheduler), a fresh DI scope, one span, one counter increment, and
/// a failure that is logged without values and never escapes (a failing job must not take the host down).
/// </summary>
public sealed partial class JobRunner(IServiceScopeFactory scopes, ILogger<JobRunner> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

    public async Task<JobOutcome> RunAsync(IJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var gate = gates.GetOrAdd(job.Name, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, CancellationToken.None).ConfigureAwait(false))
        {
            LogOverlap(logger, job.Name);
            JobTelemetry.Record(job.Name, JobOutcome.Overlapped);
            return JobOutcome.Overlapped;
        }

        try
        {
            using var activity = JobTelemetry.Source.StartActivity("job " + job.Name);
            activity?.SetTag("job.name", job.Name);
            var outcome = await ExecuteAsync(job, cancellationToken).ConfigureAwait(false);
            activity?.SetTag("job.outcome", JobTelemetry.OutcomeTag(outcome));
            if (outcome == JobOutcome.Failed)
            {
                activity?.SetStatus(ActivityStatusCode.Error);
            }

            JobTelemetry.Record(job.Name, outcome);
            return outcome;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<JobOutcome> ExecuteAsync(IJob job, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            return await job.RunAsync(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return JobOutcome.Cancelled;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Only the type is logged: the message of a driver or provider exception can carry configured values.
            LogFailure(logger, job.Name, e.GetType().Name);
            return JobOutcome.Failed;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scheduled job {Job} skipped: the previous run is still running")]
    private static partial void LogOverlap(ILogger logger, string job);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled job {Job} failed ({ExceptionType})")]
    private static partial void LogFailure(ILogger logger, string job, string exceptionType);
}
