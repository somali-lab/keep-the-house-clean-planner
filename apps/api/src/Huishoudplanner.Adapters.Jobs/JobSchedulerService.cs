using Microsoft.Extensions.Hosting;

namespace Huishoudplanner.Adapters.Jobs;

/// <summary>
/// The timer around <see cref="JobScheduler"/>: one hosted service for every registered job (the jobs share a registry instead of
/// each owning a service, so a new job is a class and a registration). It sleeps until the next cron time, never longer than
/// <see cref="MaximumSleep"/> so a changed clock is noticed, and waits for the runs that are still going when the host stops.
/// </summary>
public sealed class JobSchedulerService(JobScheduler scheduler, TimeProvider time) : BackgroundService
{
    internal static readonly TimeSpan MaximumSleep = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var running = new List<Task<JobOutcome>>();
        while (!stoppingToken.IsCancellationRequested && scheduler.NextDue is { } next)
        {
            var delay = next - time.GetUtcNow();
            if (delay > MaximumSleep)
            {
                delay = MaximumSleep;
            }

            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, time, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }

            running.RemoveAll(t => t.IsCompleted);
            running.AddRange(scheduler.StartDue(stoppingToken));
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }
}
