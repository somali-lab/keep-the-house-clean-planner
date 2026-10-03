using Huishoudplanner.Adapters.Jobs;

namespace Huishoudplanner.Integration.Tests.Jobs;

/// <summary>
/// The scheduler of slice 6.3a. The three <c>scheduler</c> tests of generation.test.ts (disabled, starts and stops, the cron patterns) become tests of
/// <see cref="JobScheduler"/> and <see cref="JobSchedulerService"/> on a fake clock: no test waits for a cron time. The jobs fire in the household
/// timezone (Europe/Amsterdam), also across the two daylight-saving changes.
/// </summary>
public sealed class JobSchedulerTests
{
    private static DateTimeOffset Utc(string instant) => DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture);

    private static (RecordingJob Nightly, RecordingJob Retention) TheTwoJobs() =>
        (new RecordingJob(NightlyGenerationJob.JobName, NightlyGenerationJob.CronSchedule), new RecordingJob(AuditRetentionJob.JobName, AuditRetentionJob.CronSchedule));

    [Fact]
    public void A_disabled_scheduler_schedules_no_job()
    {
        using var rig = new JobsRig(Utc("2026-09-14T00:00:00Z"));
        var (nightly, retention) = TheTwoJobs();

        var scheduler = rig.Scheduler(enabled: false, nightly, retention);
        rig.Clock.Advance(TimeSpan.FromDays(3));

        scheduler.JobNames.Should().BeEmpty();
        scheduler.NextDue.Should().BeNull();
        scheduler.StartDue(TestContext.Current.CancellationToken).Should().BeEmpty();
        nightly.Runs.Should().Be(0);
    }

    [Fact]
    public void An_enabled_scheduler_schedules_generation_at_0300_and_retention_at_0345_in_the_household_timezone()
    {
        using var rig = new JobsRig(Utc("2026-09-14T00:00:00Z")); // 02:00 in Amsterdam (CEST)
        var (nightly, retention) = TheTwoJobs();

        var scheduler = rig.Scheduler(enabled: true, nightly, retention);

        scheduler.JobNames.Should().Equal("nightly-generation", "audit-retention");
        scheduler.NextRunOf("nightly-generation").Should().Be(Utc("2026-09-14T01:00:00Z"));
        scheduler.NextRunOf("audit-retention").Should().Be(Utc("2026-09-14T01:45:00Z"));
        scheduler.NextDue.Should().Be(Utc("2026-09-14T01:00:00Z"));
    }

    [Fact]
    public async Task A_tick_before_the_time_starts_nothing_and_a_tick_at_the_time_starts_that_job_once()
    {
        using var rig = new JobsRig(Utc("2026-09-14T00:00:00Z"));
        var (nightly, retention) = TheTwoJobs();
        var scheduler = rig.Scheduler(true, nightly, retention);

        scheduler.StartDue(TestContext.Current.CancellationToken).Should().BeEmpty();
        rig.Clock.SetUtcNow(Utc("2026-09-14T01:00:00Z"));
        var started = scheduler.StartDue(TestContext.Current.CancellationToken);
        var outcomes = await Task.WhenAll(started);
        var again = scheduler.StartDue(TestContext.Current.CancellationToken);

        outcomes.Should().Equal(JobOutcome.Succeeded);
        nightly.Runs.Should().Be(1);
        retention.Runs.Should().Be(0);
        again.Should().BeEmpty();
        scheduler.NextRunOf("nightly-generation").Should().Be(Utc("2026-09-15T01:00:00Z"));
    }

    [Fact]
    public async Task A_run_that_was_missed_while_the_process_slept_happens_once_not_once_per_missed_night()
    {
        using var rig = new JobsRig(Utc("2026-09-14T00:00:00Z"));
        var (nightly, retention) = TheTwoJobs();
        var scheduler = rig.Scheduler(true, nightly, retention);

        rig.Clock.SetUtcNow(Utc("2026-09-18T12:00:00Z"));
        await Task.WhenAll(scheduler.StartDue(TestContext.Current.CancellationToken));

        nightly.Runs.Should().Be(1);
        retention.Runs.Should().Be(1);
        scheduler.NextRunOf("nightly-generation").Should().Be(Utc("2026-09-19T01:00:00Z"));
    }

    [Theory]
    [InlineData("2026-03-28T12:00:00Z", "2026-03-29T01:00:00Z", "2026-03-29T01:45:00Z")] // the night of the spring change: 03:00 and 03:45 are CEST (UTC+2)
    [InlineData("2026-03-29T12:00:00Z", "2026-03-30T01:00:00Z", "2026-03-30T01:45:00Z")]
    [InlineData("2026-03-27T12:00:00Z", "2026-03-28T02:00:00Z", "2026-03-28T02:45:00Z")] // the night before it is still CET (UTC+1)
    [InlineData("2026-10-24T12:00:00Z", "2026-10-25T02:00:00Z", "2026-10-25T02:45:00Z")] // the night of the autumn change: 03:00 is CET again, after the repeated hour
    [InlineData("2026-10-25T12:00:00Z", "2026-10-26T02:00:00Z", "2026-10-26T02:45:00Z")]
    [InlineData("2026-10-23T12:00:00Z", "2026-10-24T01:00:00Z", "2026-10-24T01:45:00Z")] // the night before it is still CEST
    public void The_jobs_keep_their_local_time_across_the_daylight_saving_changes(string now, string generation, string retention)
    {
        using var rig = new JobsRig(Utc(now));
        var (nightly, audit) = TheTwoJobs();

        var scheduler = rig.Scheduler(true, nightly, audit);

        scheduler.NextRunOf("nightly-generation").Should().Be(Utc(generation));
        scheduler.NextRunOf("audit-retention").Should().Be(Utc(retention));
    }

    [Fact]
    public void A_local_time_inside_the_hour_the_spring_change_skips_runs_right_after_the_gap_and_a_repeated_autumn_time_runs_once()
    {
        // Documents the Cronos (*nix cron) rule for a household that moves a job into the changeover hour. Not one of today's times.
        using var rig = new JobsRig(Utc("2026-03-28T12:00:00Z"));
        var skipped = new RecordingJob("skipped-hour", "30 2 * * *");
        var repeated = new RecordingJob("repeated-hour", "30 2 * * *");

        var spring = rig.Scheduler(true, skipped);
        spring.NextRunOf("skipped-hour").Should().Be(Utc("2026-03-29T01:00:00Z")); // 02:30 does not exist on 29 March; 03:00 CEST is the first moment after the gap

        rig.Clock.SetUtcNow(Utc("2026-10-24T12:00:00Z"));
        var autumn = rig.Scheduler(true, repeated);
        var first = autumn.NextRunOf("repeated-hour")!.Value;
        first.Should().Be(Utc("2026-10-25T00:30:00Z")); // 02:30 CEST, the first of the two 02:30 on 25 October
        rig.Clock.SetUtcNow(first);
        autumn.StartDue(TestContext.Current.CancellationToken);
        autumn.NextRunOf("repeated-hour").Should().Be(Utc("2026-10-26T01:30:00Z")); // not again at 02:30 CET
    }

    [Fact]
    public async Task The_hosted_service_runs_a_job_when_the_clock_reaches_its_time_and_stops_with_the_host()
    {
        using var rig = new JobsRig(Utc("2026-09-14T00:00:00Z"));
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nightly = new RecordingJob(NightlyGenerationJob.JobName, NightlyGenerationJob.CronSchedule, _ =>
        {
            ran.TrySetResult();
            return Task.FromResult(JobOutcome.Succeeded);
        });
        var service = new JobSchedulerService(rig.Scheduler(true, nightly), rig.Clock);
        var ct = TestContext.Current.CancellationToken;

        await service.StartAsync(ct);
        nightly.Runs.Should().Be(0);
        rig.Clock.Advance(TimeSpan.FromHours(1)); // 00:00Z is 02:00 in Amsterdam, so this is 03:00
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await service.StopAsync(ct);

        nightly.Runs.Should().Be(1);
    }

    [Fact]
    public async Task The_hosted_service_has_nothing_to_wait_for_when_the_scheduler_is_disabled()
    {
        using var rig = new JobsRig(Utc("2026-09-14T00:00:00Z"));
        var service = new JobSchedulerService(rig.Scheduler(false, new RecordingJob("x", "0 3 * * *")), rig.Clock);
        var ct = TestContext.Current.CancellationToken;

        await service.StartAsync(ct);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), ct);
        await service.StopAsync(ct);

        service.ExecuteTask.IsCompletedSuccessfully.Should().BeTrue();
    }
}
