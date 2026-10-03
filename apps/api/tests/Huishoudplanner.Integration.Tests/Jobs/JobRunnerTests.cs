using System.Diagnostics;
using System.Diagnostics.Metrics;
using Huishoudplanner.Adapters.Jobs;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.Logging;

namespace Huishoudplanner.Integration.Tests.Jobs;

/// <summary>The runner: noOverlap, a failure that never escapes and never logs a value, one span and one counter increment per run.</summary>
public sealed class JobRunnerTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_trigger_that_finds_the_previous_run_still_going_does_not_start_and_the_next_one_after_it_ended_does()
    {
        using var rig = new JobsRig(Start);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = new RecordingJob("hold", "0 3 * * *", async _ =>
        {
            await release.Task;
            return JobOutcome.Succeeded;
        });

        var first = rig.Runner.RunAsync(job, Ct);
        var second = await rig.Runner.RunAsync(job, Ct);
        release.SetResult();
        var firstOutcome = await first;
        var third = await rig.Runner.RunAsync(job, Ct);

        second.Should().Be(JobOutcome.Overlapped);
        firstOutcome.Should().Be(JobOutcome.Succeeded);
        third.Should().Be(JobOutcome.Succeeded);
        job.Runs.Should().Be(2);
    }

    [Fact]
    public async Task Two_different_jobs_may_run_at_the_same_time()
    {
        using var rig = new JobsRig(Start);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new RecordingJob("slow", "0 3 * * *", async _ =>
        {
            await release.Task;
            return JobOutcome.Succeeded;
        });
        var quick = new RecordingJob("quick", "45 3 * * *");

        var running = rig.Runner.RunAsync(slow, Ct);
        var quickOutcome = await rig.Runner.RunAsync(quick, Ct);
        release.SetResult();
        await running;

        quickOutcome.Should().Be(JobOutcome.Succeeded);
    }

    [Fact]
    public async Task A_failing_job_is_logged_with_its_name_and_exception_type_only_and_never_throws()
    {
        var logs = new LogCollector();
        using var rig = new JobsRig(Start, logs);
        var job = new RecordingJob("boom", "0 3 * * *", _ => throw new InvalidOperationException("mongodb://user:secret-password@host/db"));

        var outcome = await rig.Runner.RunAsync(job, Ct);

        outcome.Should().Be(JobOutcome.Failed);
        var errors = logs.Entries.Where(e => e.Level == LogLevel.Error).ToList();
        errors.Should().ContainSingle().Which.Message.Should().Contain("boom").And.Contain(nameof(InvalidOperationException)).And.NotContain("secret");
    }

    [Fact]
    public async Task A_failed_run_does_not_block_the_next_one()
    {
        using var rig = new JobsRig(Start);
        var calls = 0;
        var job = new RecordingJob("flaky", "0 3 * * *", _ => ++calls == 1 ? throw new TimeoutException() : Task.FromResult(JobOutcome.Succeeded));

        var first = await rig.Runner.RunAsync(job, Ct);
        var second = await rig.Runner.RunAsync(job, Ct);

        (first, second).Should().Be((JobOutcome.Failed, JobOutcome.Succeeded));
    }

    [Fact]
    public async Task A_run_that_ends_because_the_host_stops_is_cancelled_not_failed()
    {
        var logs = new LogCollector();
        using var rig = new JobsRig(Start, logs);
        using var stopping = new CancellationTokenSource();
        var job = new RecordingJob("long", "0 3 * * *", async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return JobOutcome.Succeeded;
        });

        var running = rig.Runner.RunAsync(job, stopping.Token);
        await stopping.CancelAsync();
        var outcome = await running;

        outcome.Should().Be(JobOutcome.Cancelled);
        logs.Entries.Should().NotContain(e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Every_run_gets_its_own_scope()
    {
        using var rig = new JobsRig(Start);
        var job = new RecordingJob("scoped", "0 3 * * *");

        await rig.Runner.RunAsync(job, Ct);
        var first = job.LastServices;
        await rig.Runner.RunAsync(job, Ct);

        first.Should().NotBeNull();
        job.LastServices.Should().NotBeSameAs(first);
    }

    [Fact]
    public async Task A_run_is_one_span_and_one_counter_increment_by_job_and_outcome()
    {
        using var rig = new JobsRig(Start);
        var spans = new List<Activity>();
        var counts = new List<(string Job, string Outcome, long Value)>();
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == JobTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Add,
        };
        ActivitySource.AddActivityListener(activities);
        using var meter = new MeterListener();
        meter.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == JobTelemetry.Name && instrument.Name == JobTelemetry.RunsInstrument)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meter.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var job = tags.ToArray().Single(t => t.Key == "job").Value as string;
            if (job == "telemetry-ok" || job == "telemetry-bad")
            {
                counts.Add((job, (string)tags.ToArray().Single(t => t.Key == "outcome").Value!, value));
            }
        });
        meter.Start();

        await rig.Runner.RunAsync(new RecordingJob("telemetry-ok", "0 3 * * *"), Ct);
        await rig.Runner.RunAsync(new RecordingJob("telemetry-bad", "0 3 * * *", _ => throw new InvalidOperationException()), Ct);

        counts.Should().BeEquivalentTo([("telemetry-ok", "succeeded", 1L), ("telemetry-bad", "failed", 1L)]);
        spans.Where(s => s.OperationName.StartsWith("job telemetry", StringComparison.Ordinal)).Select(s => (s.OperationName, s.Status)).Should().BeEquivalentTo(
            [("job telemetry-ok", ActivityStatusCode.Unset), ("job telemetry-bad", ActivityStatusCode.Error)]);
    }
}
