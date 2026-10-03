using Huishoudplanner.Application.Points;
using Huishoudplanner.Application.Tests.Occurrences;
using Huishoudplanner.Domain.Audit;
using Huishoudplanner.Domain.Errors;
using Huishoudplanner.Domain.Generation;
using Huishoudplanner.Domain.Occurrences;
using Huishoudplanner.Domain.Points;
using Huishoudplanner.Domain.Ports.Driving;
using Microsoft.Extensions.Logging.Abstractions;
using OneOf;

namespace Huishoudplanner.Application.Tests.Points;

/// <summary>The nightly composite, the safe reconciliation that startup and the nightly run use, and the follow-up the occurrence use cases request.</summary>
public sealed class NightlyAndHookTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeGeneration : IGenerationService
    {
        public List<string> Calls { get; } = [];

        public OneOf<GenerationRun, SettingsMissing, ConflictError, PortError>? Outcome { get; set; }

        public Task<OneOf<GenerationRun, SettingsMissing, ConflictError, PortError>> GenerateUpcomingAsync(AuditActor actor, string runId, CancellationToken cancellationToken)
        {
            Calls.Add("generate");
            return Task.FromResult(Outcome ?? new GenerationRun(runId, 0, []));
        }

        public Task<OneOf<GenerationResult, SettingsMissing, ConflictError, PortError>> GenerateCycleAsync(AuditActor actor, int cycleIndex, string runId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OneOf<ReplacementResult, NotFound, SettingsMissing, ConflictError, PortError>> ReplaceUpcomingAsync(AuditActor actor, string planId, string runId, string reason, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakePoints(List<string> calls) : IPointsService
    {
        public Func<OneOf<PointsRecomputeResult, ConflictError, PortError>>? Outcome { get; set; }

        public Exception? Throws { get; set; }

        public PointsRecomputeTrigger? Trigger { get; private set; }

        public Task<OneOf<PointsBalances, ValidationErrors, SettingsMissing, PortError>> BalancesAsync(DateOnly? fromDay, DateOnly? toDay, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OneOf<PointEntryList, ValidationErrors, SettingsMissing, PortError>> EntriesAsync(PointEntriesRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<OneOf<PointsRecomputeResult, ConflictError, PortError>> RecomputeAsync(AuditActor actor, PointsRecomputeTrigger trigger, CancellationToken cancellationToken)
        {
            calls.Add("reconcile");
            Trigger = trigger;
            if (Throws is { } e)
            {
                throw e;
            }

            return Task.FromResult(Outcome?.Invoke() ?? PointsRecomputeResult.Empty(trigger));
        }
    }

    // ---- the nightly composite: generation, then the reconciliation

    [Fact]
    public async Task Nightly_generatesThenReconcilesWithTheNightlyTrigger()
    {
        var generation = new FakeGeneration();
        var points = new FakePoints(generation.Calls);
        var service = new NightlyService(generation, points, NullLogger<NightlyService>.Instance);

        var run = (await service.RunAsync(AuditActor.System, "run-1", Ct)).AsT0;

        generation.Calls.Should().Equal("generate", "reconcile");
        points.Trigger.Should().Be(PointsRecomputeTrigger.Nightly);
        run.Generation.RunId.Should().Be("run-1");
        run.Points.Should().Be(PointsRecomputeResult.Empty(PointsRecomputeTrigger.Nightly));
    }

    [Fact]
    public async Task Nightly_aFailingReconciliationIsLoggedAndNeverFailsTheRun()
    {
        var generation = new FakeGeneration();
        var points = new FakePoints(generation.Calls) { Outcome = () => new PortError("pointEntries.failed: boom") };
        var service = new NightlyService(generation, points, NullLogger<NightlyService>.Instance);

        var run = (await service.RunAsync(AuditActor.System, "run-1", Ct)).AsT0;

        run.Points.Should().BeNull();
        run.Generation.RunId.Should().Be("run-1");
    }

    [Fact]
    public async Task Nightly_aReconciliationThatThrowsIsAlsoSwallowed()
    {
        var generation = new FakeGeneration();
        var points = new FakePoints(generation.Calls) { Throws = new InvalidOperationException("boom") };
        var service = new NightlyService(generation, points, NullLogger<NightlyService>.Instance);

        (await service.RunAsync(AuditActor.System, "run-1", Ct)).AsT0.Points.Should().BeNull();
    }

    [Fact]
    public async Task Nightly_aFailedGenerationFailsTheRunAndDoesNotReconcile()
    {
        var generation = new FakeGeneration { Outcome = new PortError("cycles.failed: boom") };
        var points = new FakePoints(generation.Calls);
        var service = new NightlyService(generation, points, NullLogger<NightlyService>.Instance);

        var result = await service.RunAsync(AuditActor.System, "run-1", Ct);

        result.AsT3.Message.Should().StartWith("cycles.failed");
        generation.Calls.Should().Equal("generate");
    }

    [Fact]
    public async Task SafeReconcile_answersTheResultOrNullAndNeverThrows()
    {
        var calls = new List<string>();
        var points = new FakePoints(calls);

        (await SafeReconcile.RunAsync(points, AuditActor.System, PointsRecomputeTrigger.Startup, NullLogger.Instance, Ct)).Should().NotBeNull();
        points.Outcome = () => new ConflictError("write_conflict", "lost");
        (await SafeReconcile.RunAsync(points, AuditActor.System, PointsRecomputeTrigger.Startup, NullLogger.Instance, Ct)).Should().BeNull();
        points.Throws = new InvalidOperationException("boom");
        (await SafeReconcile.RunAsync(points, AuditActor.System, PointsRecomputeTrigger.Startup, NullLogger.Instance, Ct)).Should().BeNull();
    }

    [Fact]
    public async Task SafeReconcile_stillHonoursACancellation()
    {
        var points = new FakePoints([]) { Throws = new OperationCanceledException() };

        var act = () => SafeReconcile.RunAsync(points, AuditActor.System, PointsRecomputeTrigger.Startup, NullLogger.Instance, Ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- the follow-up the occurrence use cases request (ADR-0011, syncExecutionPoints)

    [Fact]
    public async Task OccurrenceActions_completeUncompleteCorrectAndDeleteSyncTheLedgerInTheSameTransactionWithTheirReason()
    {
        var w = new OccurrenceWorld();
        var actor = OccurrenceWorld.Actor(w.Admin);
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P1);

        (await w.Service.CompleteAsync(actor, occurrence.Id, new CompleteCommand(w.P1.Id), Ct)).IsT0.Should().BeTrue();
        var completedAt = w.Stored(occurrence).CompletedAt!.Value;
        (await w.Service.EditCompletionAsync(actor, occurrence.Id, new EditCompletionCommand(new DateOnly(2026, 9, 15), completedAt.AddDays(-1), w.P2.Id), Ct)).IsT0.Should().BeTrue();
        (await w.Service.UncompleteAsync(actor, occurrence.Id, Ct)).IsT0.Should().BeTrue();
        (await w.Service.CompleteAsync(actor, occurrence.Id, new CompleteCommand(w.P1.Id), Ct)).IsT0.Should().BeTrue();
        (await w.Service.DeleteCompletedAsync(actor, occurrence.Id, Ct)).IsT0.Should().BeTrue();

        w.Points.Calls.Should().Equal(
            (occurrence.Id, PointsSyncReason.Complete, w.Admin.Id),
            (occurrence.Id, PointsSyncReason.Correction, w.Admin.Id),
            (occurrence.Id, PointsSyncReason.Uncomplete, w.Admin.Id),
            (occurrence.Id, PointsSyncReason.Complete, w.Admin.Id),
            (occurrence.Id, PointsSyncReason.Correction, w.Admin.Id));
    }

    [Fact]
    public async Task OccurrenceActions_thatAreNotAboutAnExecutionNeverTouchTheLedger()
    {
        var w = new OccurrenceWorld();
        var actor = OccurrenceWorld.Actor(w.P1);
        var occurrence = w.Seed(w.Weekly, "2026-09-17", null);

        await w.Service.AssignAsync(actor, occurrence.Id, w.P1.Id, Ct);
        await w.Service.RescheduleAsync(actor, occurrence.Id, new DateOnly(2026, 9, 18), Ct);
        await w.Service.SkipAsync(actor, occurrence.Id, "ziek", Ct);
        var other = w.Seed(w.Weekly, "2026-09-18", null);
        await w.Service.ClaimAsync(actor, other.Id, Ct);

        w.Points.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task OccurrenceActions_aRefusedCompletionNeverSyncs()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P1, OccurrenceStatus.Done, o => o with { PointsSnapshot = 30, CompletedBy = w.P1.Id });

        var result = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct);

        result.IsT3.Should().BeTrue();
        w.Points.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task OccurrenceActions_aLedgerFailureRollsTheWholeCompletionBack()
    {
        var w = new OccurrenceWorld();
        var occurrence = w.Seed(w.Weekly, "2026-09-16", w.P1);
        w.Points.Failure = new PortError("pointEntries.failed: boom");

        var result = await w.Service.CompleteAsync(OccurrenceWorld.Actor(w.P1), occurrence.Id, new CompleteCommand(), Ct);

        result.AsT5.Message.Should().StartWith("pointEntries.failed");
        w.Transactions.Aborts.Should().Be(1);
        w.Stored(occurrence).Status.Should().Be(OccurrenceStatus.Open);
        w.Audit.Entries.Should().BeEmpty();
    }
}
