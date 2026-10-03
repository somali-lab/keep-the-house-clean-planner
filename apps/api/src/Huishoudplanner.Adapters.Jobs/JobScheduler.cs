using Cronos;

namespace Huishoudplanner.Adapters.Jobs;

/// <summary>
/// The schedule state of the jobs: when each one fires next. It has no timer of its own, so a test drives it with a fake
/// <see cref="TimeProvider"/> and <see cref="StartDue"/> instead of waiting for a cron time; <see cref="JobSchedulerService"/> is the timer around it.
/// Expressions are evaluated in the household timezone with Cronos, whose daylight-saving rules are those of *nix cron: a fixed time that falls
/// in the hour skipped by the spring change runs right after the gap, a fixed time that occurs twice in the autumn runs once.
/// With <see cref="JobsOptions.Enabled"/> false (<c>DISABLE_SCHEDULER=true</c>) no job is scheduled at all.
/// </summary>
public sealed class JobScheduler
{
    private readonly List<Entry> entries = [];
    private readonly JobRunner runner;
    private readonly TimeProvider time;
    private readonly TimeZoneInfo zone;

    private sealed class Entry(IJob job, CronExpression cron, DateTimeOffset next)
    {
        public IJob Job { get; } = job;

        public CronExpression Cron { get; } = cron;

        public DateTimeOffset Next { get; set; } = next;
    }

    public JobScheduler(IEnumerable<IJob> jobs, JobRunner runner, TimeProvider time, JobsOptions options)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(options);
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.time = time ?? throw new ArgumentNullException(nameof(time));
        zone = options.TimeZone;
        if (!options.Enabled)
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var job in jobs)
        {
            var cron = CronExpression.Parse(job.Schedule, CronFormat.Standard);
            if (cron.GetNextOccurrence(now, zone) is { } next)
            {
                entries.Add(new Entry(job, cron, next));
            }
        }
    }

    /// <summary>The names of the scheduled jobs; empty when the scheduler is disabled.</summary>
    public IReadOnlyList<string> JobNames => [.. entries.Select(e => e.Job.Name)];

    /// <summary>When the first job fires next; <c>null</c> when nothing is scheduled.</summary>
    public DateTimeOffset? NextDue => entries.Count == 0 ? null : entries.Min(e => e.Next);

    /// <summary>When a job fires next, or <c>null</c> when it is not scheduled.</summary>
    public DateTimeOffset? NextRunOf(string jobName) => entries.FirstOrDefault(e => e.Job.Name == jobName)?.Next;

    /// <summary>
    /// Starts every job whose time has come and schedules its next run after now (a run that was missed because the process was suspended
    /// happens once, not once per missed time). The returned tasks end when the runs end; they never fault.
    /// </summary>
    public IReadOnlyList<Task<JobOutcome>> StartDue(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var started = new List<Task<JobOutcome>>();
        foreach (var entry in entries.Where(e => e.Next <= now))
        {
            started.Add(runner.RunAsync(entry.Job, cancellationToken));
            entry.Next = entry.Cron.GetNextOccurrence(now, zone) ?? DateTimeOffset.MaxValue;
        }

        return started;
    }
}
