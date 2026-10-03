using Huishoudplanner.Adapters.Jobs;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driving;
using Huishoudplanner.Integration.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using OneOf;

namespace Huishoudplanner.Integration.Tests.Jobs;

/// <summary>The two jobs against fake driving ports: who they act as and how each answer of the port ends the run.</summary>
public sealed class JobPortTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Services(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        configure(services);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task The_nightly_generation_runs_as_the_system_with_a_fresh_run_id_and_succeeds()
    {
        var nightly = new Mock<INightlyService>();
        AuditActor? actor = null;
        nightly.Setup(g => g.RunAsync(It.IsAny<AuditActor>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<AuditActor, string, CancellationToken>((a, _, _) => actor = a)
            .ReturnsAsync(new NightlyRun(new GenerationRun("run-1", 0, [new GenerationResult(0, "c0", "p", 4, 0)]), null));
        var logs = new LogCollector();
        using var services = Services(s => s.AddSingleton(nightly.Object));
        var job = new NightlyGenerationJob(new LoggerFactory([logs]).CreateLogger<NightlyGenerationJob>());

        var outcome = await job.RunAsync(services, Ct);

        outcome.Should().Be(JobOutcome.Succeeded);
        actor.Should().Be(AuditActor.System);
        actor!.Source.Should().Be(AuditSource.System);
        logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Information).Which.Message.Should().Contain("run-1").And.Contain("inserted 4");
        job.Name.Should().Be("nightly-generation");
        job.Schedule.Should().Be("0 3 * * *");
    }

    [Fact]
    public async Task The_nightly_run_logs_what_the_points_reconciliation_did_and_a_failed_reconciliation_does_not_fail_the_job()
    {
        var nightly = new Mock<INightlyService>();
        var generation = new GenerationRun("run-1", 0, []);
        var reconciled = PointsRecomputeResult.Empty(PointsRecomputeTrigger.Nightly) with { Created = 2, Updated = 1, Removed = 3, Skipped = 4, CorrectionsTotal = 4 };
        var logs = new LogCollector();
        using var services = Services(s => s.AddSingleton(nightly.Object));
        var job = new NightlyGenerationJob(new LoggerFactory([logs]).CreateLogger<NightlyGenerationJob>());

        nightly.Setup(g => g.RunAsync(It.IsAny<AuditActor>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new NightlyRun(generation, reconciled));
        var done = await job.RunAsync(services, Ct);
        var message = logs.Entries.Last(e => e.Level == LogLevel.Information).Message;
        nightly.Setup(g => g.RunAsync(It.IsAny<AuditActor>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new NightlyRun(generation, null));
        var failedReconcile = await job.RunAsync(services, Ct);

        (done, failedReconcile).Should().Be((JobOutcome.Succeeded, JobOutcome.Succeeded));
        message.Should().Contain("created 2").And.Contain("updated 1").And.Contain("removed 3").And.Contain("skipped 4").And.Contain("corrections 4");
    }

    public static TheoryData<OneOf<GenerationRun, SettingsMissing, ConflictError, PortError>> FailedGenerations => new()
    {
        new SettingsMissing(),
        new ConflictError("conflict", "busy"),
        new PortError("mongodb://user:secret@host failed"),
    };

    [Theory]
    [MemberData(nameof(FailedGenerations))]
    public async Task The_nightly_generation_fails_without_logging_the_port_error_text(OneOf<GenerationRun, SettingsMissing, ConflictError, PortError> answer)
    {
        var nightly = new Mock<INightlyService>();
        nightly.Setup(g => g.RunAsync(It.IsAny<AuditActor>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(answer.Match<OneOf<NightlyRun, SettingsMissing, ConflictError, PortError>>(run => new NightlyRun(run, null), missing => missing, conflict => conflict, error => error));
        var logs = new LogCollector();
        using var services = Services(s => s.AddSingleton(nightly.Object));
        var job = new NightlyGenerationJob(new LoggerFactory([logs]).CreateLogger<NightlyGenerationJob>());

        var outcome = await job.RunAsync(services, Ct);

        outcome.Should().Be(JobOutcome.Failed);
        logs.Entries.Should().ContainSingle(e => e.Level == LogLevel.Error).Which.Message.Should().NotContain("secret");
    }

    [Fact]
    public async Task Audit_retention_is_skipped_without_retention_succeeds_when_it_ran_and_fails_on_a_port_error()
    {
        var retention = new Mock<IAuditRetentionService>();
        using var services = Services(s => s.AddSingleton(retention.Object));
        var job = new AuditRetentionJob(new LoggerFactory().CreateLogger<AuditRetentionJob>());

        retention.Setup(r => r.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new RetentionDisabled());
        var disabled = await job.RunAsync(services, Ct);
        retention.Setup(r => r.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new RetentionDone(DateTimeOffset.UnixEpoch, 3));
        var done = await job.RunAsync(services, Ct);
        retention.Setup(r => r.RunAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new PortError("down"));
        var failed = await job.RunAsync(services, Ct);

        (disabled, done, failed).Should().Be((JobOutcome.Skipped, JobOutcome.Succeeded, JobOutcome.Failed));
        job.Name.Should().Be("audit-retention");
        job.Schedule.Should().Be("45 3 * * *");
    }
}
